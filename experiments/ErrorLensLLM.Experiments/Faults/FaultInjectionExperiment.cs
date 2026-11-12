using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorLensLLM.Core.Incidents;
using Npgsql;

namespace ErrorLensLLM.Experiments.Faults;

/// <summary>One action of a scenario, run against the demo in order.</summary>
public abstract record ScenarioStep;

public sealed record Wait(TimeSpan Duration) : ScenarioStep;

/// <summary>Restarts a module as a new release. With faults, it starts the fault; <paramref name="FaultDelay"/> keeps them dormant for a while.</summary>
public sealed record Release(string Module, string Version, string[] Faults, TimeSpan FaultDelay = default) : ScenarioStep;

/// <summary>Switches a fault on in a running module, without a release (an external dependency failing).</summary>
public sealed record EnableFault(string Module, string Fault) : ScenarioStep;

/// <summary>What actually caused the failure; <see cref="DeploymentModule"/> is null when no release did.</summary>
public sealed record RootCause(string Module, string ExceptionType, ErrorCategory Category, string? DeploymentModule, string? DeploymentVersion);

public sealed record FaultScenario(string Name, string Description, IReadOnlyList<ScenarioStep> Steps, RootCause Truth, bool Hard);

public sealed record FaultInjectionOptions
{
    public required string RepositoryRoot { get; init; }
    public required string ReportPath { get; init; }

    /// <summary>Admin connection to the local PostgreSQL; each run gets a fresh database next to the development one.</summary>
    public string ConnectionString { get; init; } = "Host=127.0.0.1;Port=5432;Database=postgres;Username=errorlens;Password=errorlens_dev";

    public string Database { get; init; } = "errorlens_eval";
    public int Repetitions { get; init; } = 3;
    public int AnalysisIntervalSeconds { get; init; } = 2;
    public IReadOnlyList<FaultScenario> Scenarios { get; init; } = DefaultScenarios(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40));

    public static IReadOnlyList<FaultScenario> DefaultScenarios(TimeSpan baseline, TimeSpan fault)
    {
        static RootCause Released(string module, string type, ErrorCategory category) => new(module, type, category, module, "1.1.0");

        return
        [
            new("kayit-missing-column", "kayit 1.1.0 reads a column whose migration never ran; not and odeme call kayit",
                [new Wait(baseline), new Release("kayit", "1.1.0", ["kayit-missing-column"]), new Wait(fault)],
                Released("kayit", "Npgsql.PostgresException", ErrorCategory.DatabaseSchema), Hard: false),
            new("odeme-bank-timeout", "odeme 1.1.0: the bank stops answering in time for 40 % of payments",
                [new Wait(baseline), new Release("odeme", "1.1.0", ["odeme-bank-timeout"]), new Wait(fault)],
                Released("odeme", "System.TimeoutException", ErrorCategory.Timeout), Hard: false),
            new("not-null-reference", "not 1.1.0 dereferences a missing elective course",
                [new Wait(baseline), new Release("not", "1.1.0", ["not-null-reference"]), new Wait(fault)],
                Released("not", "System.NullReferenceException", ErrorCategory.CodeDefect), Hard: false),

            new("odeme-bank-outage", "the bank goes down while odeme runs; no release is involved",
                [new Wait(baseline), new EnableFault("odeme", "odeme-bank-timeout"), new Wait(fault)],
                new RootCause("odeme", "System.TimeoutException", ErrorCategory.Timeout, null, null), Hard: true),
            new("kayit-delayed-fault", "kayit 1.1.0 fails only 90 s after release; a healthy not 1.0.1 is released in between",
                [new Wait(baseline), new Release("kayit", "1.1.0", ["kayit-missing-column"], TimeSpan.FromSeconds(90)),
                 new Wait(TimeSpan.FromSeconds(45)), new Release("not", "1.0.1", []), new Wait(TimeSpan.FromSeconds(45) + fault)],
                Released("kayit", "Npgsql.PostgresException", ErrorCategory.DatabaseSchema), Hard: true),
            new("concurrent-releases", "faulty kayit 1.1.0 and a healthy odeme 1.0.1 are released 5 s apart",
                [new Wait(baseline), new Release("kayit", "1.1.0", ["kayit-missing-column"]), new Wait(TimeSpan.FromSeconds(5)),
                 new Release("odeme", "1.0.1", []), new Wait(fault)],
                Released("kayit", "Npgsql.PostgresException", ErrorCategory.DatabaseSchema), Hard: true),
            new("odeme-logged-error", "odeme 1.1.0 logs bank rejections with LogError and returns 502, throwing nothing",
                [new Wait(baseline), new Release("odeme", "1.1.0", ["odeme-logged-error"]), new Wait(fault)],
                Released("odeme", "LoggedError", ErrorCategory.Dependency), Hard: true)
        ];
    }
}

/// <summary>
/// Measures root cause suggestions against injected faults: each scenario runs the demo from an empty database, applies
/// its steps (waits, releases, runtime faults), and compares the resulting incident with the known cause.
/// </summary>
public static class FaultInjectionExperiment
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly Dictionary<string, int> Ports = new() { ["kayit"] = 5101, ["not"] = 5102, ["odeme"] = 5103 };

    private sealed record Run(
        FaultScenario Scenario, int Repetition, int OtherIncidents, int FaultIncidents, int? TruthRank, bool CategoryCorrect,
        bool DeploymentCorrect, string? Suspected, double? DetectionSeconds, string? Title, int Members, long Occurrences);

    /// <summary>When the fault became active: a release (plus its delay), or a runtime switch at a known time.</summary>
    private sealed record FaultStart(Release? Release, DateTimeOffset? At);

    public static async Task RunAsync(FaultInjectionOptions options)
    {
        var script = Path.Combine(options.RepositoryRoot, "scripts", "demo.sh");
        var evalConnection = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Database = options.Database }.ConnectionString;
        var environment = new Dictionary<string, string>
        {
            ["ConnectionStrings__ErrorLens"] = evalConnection,
            ["Analysis__IntervalSeconds"] = options.AnalysisIntervalSeconds.ToString(),
            ["DEMO_SKIP_BUILD"] = "1"
        };

        Console.WriteLine("Building collector, dashboard and demo...");
        foreach (var project in new[] { "src/ErrorLensLLM.Collector", "src/ErrorLensLLM.Dashboard", "samples/ErrorLensLLM.Demo" })
            await ShellAsync(options.RepositoryRoot, "dotnet", ["build", project, "-c", "Release", "-v", "q", "-nologo"]);

        using var http = new HttpClient();
        var runs = new List<Run>();
        foreach (var scenario in options.Scenarios)
        {
            for (var repetition = 1; repetition <= options.Repetitions; repetition++)
            {
                Console.WriteLine($"[{scenario.Name} #{repetition}] fresh database");
                await ResetDatabaseAsync(options.ConnectionString, options.Database);
                FaultStart? faultStart = null;
                try
                {
                    await ShellAsync(options.RepositoryRoot, "bash", [script, "start"], environment);
                    foreach (var step in scenario.Steps)
                    {
                        Console.WriteLine($"  {step}");
                        switch (step)
                        {
                            case Wait wait:
                                await Task.Delay(wait.Duration);
                                break;
                            case Release release:
                                string[] extra = release.FaultDelay > TimeSpan.Zero ? [$"--Demo:FaultDelaySeconds={release.FaultDelay.TotalSeconds:F0}"] : [];
                                await ShellAsync(options.RepositoryRoot, "bash", [script, "deploy", release.Module, release.Version, .. release.Faults, .. extra], environment);
                                if (release.Faults.Length > 0)
                                    faultStart = new FaultStart(release, null);
                                break;
                            case EnableFault enable:
                                (await http.PostAsync($"http://localhost:{Ports[enable.Module]}/admin/faults/{enable.Fault}", null)).EnsureSuccessStatusCode();
                                faultStart = new FaultStart(null, DateTimeOffset.UtcNow);
                                break;
                        }
                    }

                    // Let the sinks flush and the analyzer run at least twice more
                    await Task.Delay(TimeSpan.FromSeconds(options.AnalysisIntervalSeconds * 2 + 3));
                }
                finally
                {
                    await ShellAsync(options.RepositoryRoot, "bash", [script, "stop"], environment);
                }

                var run = await EvaluateAsync(evalConnection, scenario, repetition, faultStart!);
                runs.Add(run);
                Console.WriteLine($"  -> incidents {run.FaultIncidents} (+{run.OtherIncidents} other), truth rank {run.TruthRank?.ToString() ?? "-"}, " +
                                  $"category {(run.CategoryCorrect ? "ok" : "wrong")}, suspected {run.Suspected ?? "none"} " +
                                  $"({(run.DeploymentCorrect ? "ok" : "wrong")}), detected after {run.DetectionSeconds:F1} s");
            }
        }

        var report = Report(options, runs);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        await File.WriteAllTextAsync(options.ReportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
    }

    private static async Task<Run> EvaluateAsync(string connectionString, FaultScenario scenario, int repetition, FaultStart faultStart)
    {
        await using var db = NpgsqlDataSource.Create(connectionString);

        async Task<(Guid Id, DateTimeOffset StartedAt)?> DeploymentAsync(string module, string version)
        {
            await using var command = db.CreateCommand(
                "select id, started_at from deployments where application = $1 and version = $2 order by started_at desc limit 1");
            command.Parameters.Add(new() { Value = module });
            command.Parameters.Add(new() { Value = version });
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)) : null;
        }

        var started = faultStart.At ?? (await DeploymentAsync(faultStart.Release!.Module, faultStart.Release.Version)
                                        ?? throw new InvalidOperationException("The faulty release was not recorded.")).StartedAt + faultStart.Release!.FaultDelay;
        var expected = scenario.Truth.DeploymentModule is { } module ? (await DeploymentAsync(module, scenario.Truth.DeploymentVersion!))?.Id : null;

        await using var incidentCommand = db.CreateCommand("""
            select i.started_at, i.created_at, i.title, i.category, i.members::text, i.suspected_deployment_id, i.occurrences,
                   d.application || ' ' || d.version
            from incidents i left join deployments d on d.id = i.suspected_deployment_id
            where i.status <> 'merged' order by i.occurrences desc
            """);
        var incidents = new List<(DateTimeOffset Started, DateTimeOffset Created, string Title, string Category, List<IncidentMember> Members,
            Guid? Deployment, long Occurrences, string? Suspected)>();
        await using (var reader = await incidentCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                incidents.Add((
                    reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                    JsonSerializer.Deserialize<List<IncidentMember>>(reader.GetString(4), Json)!,
                    reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        // Errors of a stopping process can precede the release record by a few seconds
        var cut = started - TimeSpan.FromSeconds(30);
        var other = incidents.Count(i => i.Started < cut);
        var fault = incidents.Where(i => i.Started >= cut).ToList();
        if (fault.Count == 0)
            return new Run(scenario, repetition, other, 0, null, false, false, null, null, null, 0, 0);

        var primary = fault[0];
        var rank = primary.Members.FindIndex(m => m.Application == scenario.Truth.Module && m.ExceptionType == scenario.Truth.ExceptionType);
        return new Run(
            scenario, repetition, other, fault.Count,
            rank >= 0 ? rank + 1 : null,
            primary.Category == scenario.Truth.Category.ToString(),
            primary.Deployment == expected,
            primary.Suspected,
            (primary.Created - started).TotalSeconds,
            primary.Title, primary.Members.Count, primary.Occurrences);
    }

    private static async Task ResetDatabaseAsync(string adminConnection, string database)
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(adminConnection);
        await using (var drop = admin.CreateCommand($"drop database if exists \"{database}\" with (force)"))
            await drop.ExecuteNonQueryAsync();
        await using (var create = admin.CreateCommand($"create database \"{database}\""))
            await create.ExecuteNonQueryAsync();
    }

    private static async Task ShellAsync(string workingDirectory, string file, string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} failed ({process.ExitCode}): {await error}{await output}");
    }

    private static string Report(FaultInjectionOptions options, List<Run> runs)
    {
        static string Pct(int part, int whole) => whole == 0 ? "–" : $"{100.0 * part / whole:F0} % ({part}/{whole})";
        static string Truth(RootCause t) =>
            $"{t.Module} · {t.ExceptionType[(t.ExceptionType.LastIndexOf('.') + 1)..]} · {t.Category} · " +
            (t.DeploymentModule is null ? "no release" : $"{t.DeploymentModule} {t.DeploymentVersion}");

        var sb = new StringBuilder();
        sb.AppendLine("# Fault injection: root cause suggestions");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- faults`.");
        sb.AppendLine();
        sb.AppendLine("## Protocol");
        sb.AppendLine();
        sb.AppendLine("Each run starts from an empty database and the demo (kayit, not, odeme and a traffic generator) at version 1.0.0. " +
                      $"Then the scenario's steps run: normal traffic, releases, faults. The incident analyzer runs every {options.AnalysisIntervalSeconds} s. " +
                      $"Each scenario is repeated {options.Repetitions} times. **Basic** scenarios start a continuously triggered fault " +
                      "right after its release. **Hard** scenarios target known weak spots of the correlator.");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Kind | What happens | True root cause |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var s in options.Scenarios)
            sb.AppendLine($"| `{s.Name}` | {(s.Hard ? "hard" : "basic")} | {s.Description} | {Truth(s.Truth)} |");
        sb.AppendLine();
        sb.AppendLine("Measures, on the largest incident that started after the fault became active:");
        sb.AppendLine("- **Top-1 / Top-3:** the true root cause (module + exception type) is ranked first / within the first three candidates");
        sb.AppendLine("- **Category:** the incident's category matches the fault");
        sb.AppendLine("- **Deployment:** the suspected deployment is the faulty release, or there is none when no release caused the fault");
        sb.AppendLine("- **Incidents per fault:** incidents that started once the fault was active (ideal: 1)");
        sb.AppendLine("- **Other incidents:** incidents before the fault, e.g. errors while a healthy release restarts a service");
        sb.AppendLine("- **Detection time:** from the fault becoming active to the incident first being stored");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Top-1 | Top-3 | Category | Deployment | Incidents per fault | Other incidents | Median detection |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        var groups = runs.GroupBy(r => $"`{r.Scenario.Name}`").ToList();
        groups.Add(runs.Where(r => !r.Scenario.Hard).GroupBy(_ => "**Basic**").SingleOrDefault()!);
        groups.Add(runs.Where(r => r.Scenario.Hard).GroupBy(_ => "**Hard**").SingleOrDefault()!);
        foreach (var group in groups.Where(g => g is not null))
        {
            var list = group.ToList();
            var detection = list.Where(r => r.DetectionSeconds is not null).Select(r => r.DetectionSeconds!.Value).Order().ToList();
            sb.AppendLine(
                $"| {group.Key} | {Pct(list.Count(r => r.TruthRank == 1), list.Count)} | {Pct(list.Count(r => r.TruthRank is <= 3), list.Count)} " +
                $"| {Pct(list.Count(r => r.CategoryCorrect), list.Count)} | {Pct(list.Count(r => r.DeploymentCorrect), list.Count)} " +
                $"| {list.Average(r => r.FaultIncidents):F1} | {list.Sum(r => r.OtherIncidents)} " +
                $"| {(detection.Count == 0 ? "–" : $"{detection[detection.Count / 2]:F1} s")} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Runs");
        sb.AppendLine();
        sb.AppendLine("| Scenario | # | Incident | Groups | Errors | True cause rank | Category | Suspected deployment | Detected after |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            sb.AppendLine($"| `{r.Scenario.Name}` | {r.Repetition} | {r.Title ?? "no incident"} | {r.Members} | {r.Occurrences:N0} " +
                          $"| {r.TruthRank?.ToString() ?? "–"} | {(r.CategoryCorrect ? "✓" : "✗")} " +
                          $"| {r.Suspected ?? "none"} {(r.DeploymentCorrect ? "✓" : "✗")} " +
                          $"| {(r.DetectionSeconds is { } s ? $"{s:F1} s" : "–")} |");
        }

        return sb.ToString();
    }
}

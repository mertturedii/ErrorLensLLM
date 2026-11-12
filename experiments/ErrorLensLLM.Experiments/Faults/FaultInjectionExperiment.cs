using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorLensLLM.Core.Incidents;
using Npgsql;

namespace ErrorLensLLM.Experiments.Faults;

/// <summary>A fault scenario of the demo with its known root cause (the ground truth).</summary>
public sealed record FaultScenario(string Fault, string Module, string ExceptionType, ErrorCategory Category);

public sealed record FaultInjectionOptions
{
    public required string RepositoryRoot { get; init; }
    public required string ReportPath { get; init; }

    /// <summary>Admin connection to the local PostgreSQL; each run gets a fresh database next to the development one.</summary>
    public string ConnectionString { get; init; } = "Host=127.0.0.1;Port=5432;Database=postgres;Username=errorlens;Password=errorlens_dev";

    public string Database { get; init; } = "errorlens_eval";
    public int Repetitions { get; init; } = 3;
    public TimeSpan Baseline { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan FaultDuration { get; init; } = TimeSpan.FromSeconds(40);
    public int AnalysisIntervalSeconds { get; init; } = 2;

    public IReadOnlyList<FaultScenario> Scenarios { get; init; } =
    [
        new("kayit-missing-column", "kayit", "Npgsql.PostgresException", ErrorCategory.DatabaseSchema),
        new("odeme-bank-timeout", "odeme", "System.TimeoutException", ErrorCategory.Timeout),
        new("not-null-reference", "not", "System.NullReferenceException", ErrorCategory.CodeDefect)
    ];
}

/// <summary>
/// Measures root cause suggestions against injected faults: for each scenario, runs the demo with normal traffic,
/// releases the faulty version of one module, and compares the resulting incident with the known cause.
/// </summary>
public static class FaultInjectionExperiment
{
    private const string FaultyVersion = "1.1.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Run(
        FaultScenario Scenario, int Repetition, int BaselineIncidents, int FaultIncidents, int? TruthRank,
        bool CategoryCorrect, bool DeploymentCorrect, double? DetectionSeconds, string? Title, int Members, long Occurrences);

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
        await ShellAsync(options.RepositoryRoot, "dotnet", ["build", "src/ErrorLensLLM.Collector", "-c", "Release", "-v", "q", "-nologo"]);
        await ShellAsync(options.RepositoryRoot, "dotnet", ["build", "src/ErrorLensLLM.Dashboard", "-c", "Release", "-v", "q", "-nologo"]);
        await ShellAsync(options.RepositoryRoot, "dotnet", ["build", "samples/ErrorLensLLM.Demo", "-c", "Release", "-v", "q", "-nologo"]);

        var runs = new List<Run>();
        foreach (var scenario in options.Scenarios)
        {
            for (var repetition = 1; repetition <= options.Repetitions; repetition++)
            {
                Console.WriteLine($"[{scenario.Fault} #{repetition}] fresh database, normal traffic for {options.Baseline.TotalSeconds:F0} s");
                await ResetDatabaseAsync(options.ConnectionString, options.Database);
                try
                {
                    await ShellAsync(options.RepositoryRoot, "bash", [script, "start"], environment);
                    await Task.Delay(options.Baseline);

                    Console.WriteLine($"[{scenario.Fault} #{repetition}] releasing {scenario.Module} {FaultyVersion} with the fault for {options.FaultDuration.TotalSeconds:F0} s");
                    await ShellAsync(options.RepositoryRoot, "bash", [script, "deploy", scenario.Module, FaultyVersion, scenario.Fault], environment);
                    await Task.Delay(options.FaultDuration);

                    // Let the sinks flush and the analyzer run at least twice more
                    await Task.Delay(TimeSpan.FromSeconds(options.AnalysisIntervalSeconds * 2 + 3));
                }
                finally
                {
                    await ShellAsync(options.RepositoryRoot, "bash", [script, "stop"], environment);
                }

                var run = await EvaluateAsync(evalConnection, scenario, repetition);
                runs.Add(run);
                Console.WriteLine($"  incidents during fault {run.FaultIncidents}, truth rank {run.TruthRank?.ToString() ?? "-"}, " +
                                  $"category {(run.CategoryCorrect ? "ok" : "wrong")}, deployment {(run.DeploymentCorrect ? "ok" : "wrong")}, " +
                                  $"detected after {run.DetectionSeconds:F1} s");
            }
        }

        var report = Report(options, runs);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        await File.WriteAllTextAsync(options.ReportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
    }

    private static async Task<Run> EvaluateAsync(string connectionString, FaultScenario scenario, int repetition)
    {
        await using var db = NpgsqlDataSource.Create(connectionString);

        await using var deployCommand = db.CreateCommand(
            "select id, started_at from deployments where application = $1 and version = $2 order by started_at desc limit 1");
        deployCommand.Parameters.Add(new() { Value = scenario.Module });
        deployCommand.Parameters.Add(new() { Value = FaultyVersion });
        Guid faultyDeployment;
        DateTimeOffset releasedAt;
        await using (var reader = await deployCommand.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                throw new InvalidOperationException($"The {scenario.Module} {FaultyVersion} release was not recorded.");
            (faultyDeployment, releasedAt) = (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
        }

        await using var incidentCommand = db.CreateCommand("""
            select started_at, created_at, title, category, members::text, suspected_deployment_id, occurrences
            from incidents where status <> 'merged' order by occurrences desc
            """);
        var incidents = new List<(DateTimeOffset Started, DateTimeOffset Created, string Title, string Category, List<IncidentMember> Members, Guid? Deployment, long Occurrences)>();
        await using (var reader = await incidentCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                incidents.Add((
                    reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                    JsonSerializer.Deserialize<List<IncidentMember>>(reader.GetString(4), Json)!,
                    reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetInt64(6)));
            }
        }

        // Errors from the stopping process can precede the release record by a few seconds
        var cut = releasedAt - TimeSpan.FromSeconds(30);
        var baseline = incidents.Count(i => i.Started < cut);
        var fault = incidents.Where(i => i.Started >= cut).ToList();
        if (fault.Count == 0)
            return new Run(scenario, repetition, baseline, 0, null, false, false, null, null, 0, 0);

        var primary = fault[0];
        var rank = primary.Members.FindIndex(m => m.Application == scenario.Module && m.ExceptionType == scenario.ExceptionType);
        return new Run(
            scenario, repetition, baseline, fault.Count,
            rank >= 0 ? rank + 1 : null,
            primary.Category == scenario.Category.ToString(),
            primary.Deployment == faultyDeployment,
            (primary.Created - releasedAt).TotalSeconds,
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

        var sb = new StringBuilder();
        sb.AppendLine("# Fault injection: root cause suggestions");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- faults`.");
        sb.AppendLine();
        sb.AppendLine("## Protocol");
        sb.AppendLine();
        sb.AppendLine($"Each run starts from an empty database. The demo (kayit, not, odeme + traffic generator) runs version 1.0.0 with normal traffic for " +
                      $"{options.Baseline.TotalSeconds:F0} s. Then one module is released as {FaultyVersion} with an injected fault and runs for " +
                      $"{options.FaultDuration.TotalSeconds:F0} s. The incident analyzer runs every {options.AnalysisIntervalSeconds} s. " +
                      $"Each scenario is repeated {options.Repetitions} times.");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Injected fault | True root cause |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| `kayit-missing-column` | kayit 1.1.0 reads a column whose migration never ran; not and odeme call kayit | kayit · PostgresException · DatabaseSchema |");
        sb.AppendLine("| `odeme-bank-timeout` | the bank behind odeme stops answering in time for 40 % of payments | odeme · TimeoutException · Timeout |");
        sb.AppendLine("| `not-null-reference` | not 1.1.0 dereferences a missing elective course | not · NullReferenceException · CodeDefect |");
        sb.AppendLine();
        sb.AppendLine("Measures, on the largest incident that started after the release:");
        sb.AppendLine("- **Top-1 / Top-3:** the true root cause (module + exception type) is ranked first / within the first three candidates");
        sb.AppendLine("- **Category / deployment:** the incident's category and suspected deployment match the injected fault");
        sb.AppendLine("- **Incidents per fault:** how many incidents the release produced (ideal: 1); **false alarms:** incidents during normal traffic");
        sb.AppendLine("- **Detection time:** from the faulty process start to the incident first being stored");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Top-1 | Top-3 | Category | Deployment | Incidents per fault | False alarms | Median detection |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var group in runs.GroupBy(r => r.Scenario.Fault).Append(runs.GroupBy(_ => "**All**").Single()))
        {
            var list = group.ToList();
            var detection = list.Where(r => r.DetectionSeconds is not null).Select(r => r.DetectionSeconds!.Value).Order().ToList();
            var median = detection.Count == 0 ? "–" : $"{detection[detection.Count / 2]:F1} s";
            sb.AppendLine(
                $"| {(group.Key.StartsWith('*') ? group.Key : $"`{group.Key}`")} | {Pct(list.Count(r => r.TruthRank == 1), list.Count)} " +
                $"| {Pct(list.Count(r => r.TruthRank is <= 3), list.Count)} | {Pct(list.Count(r => r.CategoryCorrect), list.Count)} " +
                $"| {Pct(list.Count(r => r.DeploymentCorrect), list.Count)} | {list.Average(r => r.FaultIncidents):F1} " +
                $"| {list.Sum(r => r.BaselineIncidents)} | {median} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Runs");
        sb.AppendLine();
        sb.AppendLine("| Scenario | # | Incident | Groups | Errors | True cause rank | Category | Deployment | Detected after |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            sb.AppendLine($"| `{r.Scenario.Fault}` | {r.Repetition} | {r.Title ?? "no incident"} | {r.Members} | {r.Occurrences:N0} " +
                          $"| {r.TruthRank?.ToString() ?? "–"} | {(r.CategoryCorrect ? "✓" : "✗")} | {(r.DeploymentCorrect ? "✓" : "✗")} " +
                          $"| {(r.DetectionSeconds is { } s ? $"{s:F1} s" : "–")} |");
        }

        return sb.ToString();
    }
}

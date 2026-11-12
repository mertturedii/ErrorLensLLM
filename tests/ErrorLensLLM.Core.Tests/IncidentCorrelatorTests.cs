using ErrorLensLLM.Core.Incidents;

namespace ErrorLensLLM.Core.Tests;

public class IncidentCorrelatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 19, 0, 0, TimeSpan.Zero);
    private readonly IncidentCorrelator _correlator = new();

    private static ErrorSample Sample(string fingerprint, string app, string type, int second, string? trace = null, string message = "m") =>
        new(fingerprint, app, type, message, $"{app}.Frame", T0.AddSeconds(second), trace);

    private static DeploymentInfo Deploy(string app, string version, int second, string? previous = null) =>
        new(Guid.NewGuid(), app, version, null, T0.AddSeconds(second), previous);

    /// <summary>The demo incident: kayit 1.1.0 reads a missing column; not and odeme fail through it.</summary>
    private static (List<ErrorSample>, List<DeploymentInfo>) FaultyKayitRelease()
    {
        var deployments = new List<DeploymentInfo>
        {
            Deploy("kayit", "1.0.0", -600), Deploy("not", "1.0.0", -600), Deploy("odeme", "1.0.0", -600),
            Deploy("kayit", "1.1.0", 0, previous: "1.0.0")
        };

        var samples = new List<ErrorSample>
        {
            // kayit was briefly down during the restart
            Sample("refused-not", "not", "System.Net.Http.HttpRequestException", -2, message: "Connection refused (localhost:<num>)"),
            Sample("refused-odeme", "odeme", "System.Net.Http.HttpRequestException", -1, message: "Connection refused (localhost:<num>)")
        };

        for (var i = 0; i < 30; i++)
        {
            samples.Add(Sample("pg", "kayit", "Npgsql.PostgresException", 2 + i, $"t{i}", "<num>: column '<val>' does not exist"));
            // The caller logs its error after kayit returned 500 for the same request
            var caller = i % 3 == 0 ? ("http-odeme", "odeme") : ("http-not", "not");
            samples.Add(Sample(caller.Item1, caller.Item2, "System.Net.Http.HttpRequestException", 2 + i, $"t{i}") with { Timestamp = T0.AddSeconds(2 + i).AddMilliseconds(40) });
        }

        return (samples, deployments);
    }

    [Fact]
    public void Faulty_release_becomes_one_incident_with_the_database_error_as_root_cause()
    {
        var (samples, deployments) = FaultyKayitRelease();

        var incidents = _correlator.Correlate(samples, deployments);

        var incident = Assert.Single(incidents);
        Assert.Equal(5, incident.Members.Count);
        Assert.Equal("pg", incident.RootCause.Fingerprint);
        Assert.Equal(ErrorCategory.DatabaseSchema, incident.RootCause.Category);
        Assert.Equal(("kayit", "1.1.0"), (incident.SuspectedDeployment!.Application, incident.SuspectedDeployment.Version));
        Assert.Equal(["kayit", "not", "odeme"], incident.Applications);
    }

    [Fact]
    public void Root_cause_is_explained_by_its_evidence()
    {
        var (samples, deployments) = FaultyKayitRelease();

        var root = _correlator.Correlate(samples, deployments)[0].RootCause;

        Assert.Contains(root.Evidence, e => e.Contains("zincirdeki ilk hata"));
        Assert.Contains(root.Evidence, e => e.Contains("kayit 1.1.0 sürümünün yayınından"));
    }

    [Fact]
    public void Callers_are_ranked_below_the_service_they_call()
    {
        var (samples, deployments) = FaultyKayitRelease();

        var members = _correlator.Correlate(samples, deployments)[0].Members;

        var root = members.Single(m => m.Fingerprint == "pg").RootCauseScore;
        Assert.All(members.Where(m => m.Fingerprint != "pg"), m => Assert.True(m.RootCauseScore < root, $"{m.Fingerprint} {m.RootCauseScore} >= {root}"));
        Assert.Equal(ErrorCategory.Dependency, members.Single(m => m.Fingerprint == "http-not").Category);
    }

    [Fact]
    public void Unrelated_error_at_the_same_time_without_a_deployment_stays_separate()
    {
        var (samples, deployments) = FaultyKayitRelease();
        deployments.RemoveAll(d => d.Version == "1.1.0");

        // Without the release, only traces link the groups; the refused connections have no trace in common
        var incidents = _correlator.Correlate(samples, deployments);

        Assert.Equal(3, incidents.Count);
        Assert.Contains(incidents, i => i.Members.Select(m => m.Fingerprint).Order().SequenceEqual(["http-not", "http-odeme", "pg"]));
    }

    [Fact]
    public void Old_deployments_of_other_modules_are_not_blamed()
    {
        var (samples, deployments) = FaultyKayitRelease();

        var incident = _correlator.Correlate(samples, deployments)[0];

        // not and odeme 1.0.0 were deployed 10 minutes earlier, outside the window; kayit 1.1.0 is the last one
        Assert.DoesNotContain(incident.Members.Where(m => m.Application != "kayit"), m => m.Evidence.Any(e => e.Contains("sürüm")));
    }

    [Fact]
    public void Timeout_without_traces_or_deployment_is_its_own_incident()
    {
        var samples = Enumerable.Range(0, 5)
            .Select(i => Sample("bank", "odeme", "System.TimeoutException", i * 10, message: "Banka <num> sn icinde yanit vermedi"))
            .ToList();

        var incident = Assert.Single(_correlator.Correlate(samples, []));

        Assert.Equal(ErrorCategory.Timeout, incident.RootCause.Category);
        Assert.Null(incident.SuspectedDeployment);
        Assert.Equal(5, incident.Occurrences);
    }

    [Fact]
    public void Same_errors_returning_after_a_quiet_period_form_a_new_incident()
    {
        var (first, deployments) = FaultyKayitRelease();
        // The same faulty release is deployed again 30 minutes later
        var later = TimeSpan.FromMinutes(30);
        var second = first.Select(s => s with { Timestamp = s.Timestamp + later, TraceId = s.TraceId is null ? null : s.TraceId + "-b" }).ToList();
        deployments.Add(new DeploymentInfo(Guid.NewGuid(), "kayit", "1.1.0", null, T0 + later, "1.0.0"));

        var incidents = _correlator.Correlate([.. first, .. second], deployments);

        Assert.Equal(2, incidents.Count);
        Assert.All(incidents, i => Assert.Equal(("pg", 5), (i.RootCause.Fingerprint, i.Members.Count)));
        Assert.Equal(T0.AddSeconds(-2) + later, incidents[0].StartedAt);
    }

    [Fact]
    public void Healthy_release_of_a_caller_at_the_same_time_is_not_blamed()
    {
        var (samples, deployments) = FaultyKayitRelease();
        deployments.Add(Deploy("odeme", "1.0.1", 5, previous: "1.0.0"));

        var incident = _correlator.Correlate(samples, deployments)[0];

        Assert.Equal("pg", incident.RootCause.Fingerprint);
        Assert.Equal(("kayit", "1.1.0"), (incident.SuspectedDeployment!.Application, incident.SuspectedDeployment.Version));
    }

    [Fact]
    public void Release_whose_fault_surfaces_later_is_still_found_past_a_release_in_between()
    {
        var (samples, _) = FaultyKayitRelease();
        // kayit 1.1.0 went out 90 s before its errors; a healthy not 1.0.1 went out 45 s before them
        List<DeploymentInfo> deployments = [Deploy("kayit", "1.1.0", -90, previous: "1.0.0"), Deploy("not", "1.0.1", -45, previous: "1.0.0")];

        var incident = _correlator.Correlate(samples.Where(s => !s.Fingerprint.StartsWith("refused")).ToList(), deployments)[0];

        Assert.Equal("pg", incident.RootCause.Fingerprint);
        Assert.Equal(("kayit", "1.1.0"), (incident.SuspectedDeployment!.Application, incident.SuspectedDeployment.Version));
        Assert.Contains(incident.RootCause.Evidence, e => e.StartsWith("kayit 1.1.0 sürümünün yayınından"));
    }

    [Fact]
    public void Restarts_and_first_deployments_are_not_releases()
    {
        // The bank failed 20 s after the services started; nothing new was shipped
        var samples = Enumerable.Range(0, 5).Select(i => Sample("bank", "odeme", "System.TimeoutException", 20 + i)).ToList();
        List<DeploymentInfo> deployments = [Deploy("odeme", "1.0.0", 0), Deploy("odeme", "1.0.0", 10, previous: "1.0.0")];

        var incident = Assert.Single(_correlator.Correlate(samples, deployments));

        Assert.Null(incident.SuspectedDeployment);
        Assert.DoesNotContain(incident.RootCause.Evidence, e => e.Contains("sürüm"));
    }

    [Theory]
    [InlineData("Npgsql.PostgresException", "<num>: relation '<val>' does not exist", ErrorCategory.DatabaseSchema)]
    [InlineData("Npgsql.PostgresException", "<num>: duplicate key value violates unique constraint", ErrorCategory.Database)]
    [InlineData("System.NullReferenceException", "Object reference not set to an instance of an object.", ErrorCategory.CodeDefect)]
    [InlineData("System.Threading.Tasks.TaskCanceledException", "The operation was canceled.", ErrorCategory.Timeout)]
    [InlineData("System.Net.Http.HttpRequestException", "Connection refused (localhost:<num>)", ErrorCategory.Dependency)]
    [InlineData("System.InvalidOperationException", "Sequence contains no elements", ErrorCategory.Unknown)]
    public void Categories_follow_exception_type_and_message(string type, string message, ErrorCategory expected)
    {
        Assert.Equal(expected, ErrorCategorizer.Categorize(type, message));
    }
}

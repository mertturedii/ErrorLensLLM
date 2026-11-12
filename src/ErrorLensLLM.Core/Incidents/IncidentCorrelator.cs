namespace ErrorLensLLM.Core.Incidents;

public sealed record CorrelationOptions
{
    /// <summary>Groups whose first errors are this close are linked if a common deployment precedes both.</summary>
    public TimeSpan CoOnsetWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A deployment this long before an onset is considered related to it.</summary>
    public TimeSpan DeploymentWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Errors can start slightly before a deployment is recorded: the old process stops before the new one reports
    /// its start time, and callers see "connection refused" in between.
    /// </summary>
    public TimeSpan DeploymentLeadTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An error group that returns after this much silence starts a new episode, so a recurrence of the same error
    /// days later becomes a new incident instead of extending the old one.
    /// </summary>
    public TimeSpan EpisodeGap { get; init; } = TimeSpan.FromMinutes(15);

    public double UpstreamWeight { get; init; } = 0.5;
    public double DeploymentWeight { get; init; } = 0.3;
    public double OnsetWeight { get; init; } = 0.2;
}

/// <summary>
/// Groups error groups into incidents and ranks their likely root cause.
/// <para>
/// Two error groups are linked when (a) their errors occur in the same distributed trace, or (b) both start within
/// <see cref="CorrelationOptions.CoOnsetWindow"/> of each other, shortly after the same deployment. Connected groups
/// form one incident. The last deployment before the incident is its suspected trigger. Each member is scored on
/// three signals: how often its error comes first in shared traces (it is upstream of the others), whether it belongs
/// to the suspected deployment's application, and how early it started relative to the other members.
/// </para>
/// </summary>
public sealed class IncidentCorrelator(CorrelationOptions? options = null)
{
    private readonly CorrelationOptions _options = options ?? new CorrelationOptions();

    public IReadOnlyList<IncidentDraft> Correlate(IReadOnlyList<ErrorSample> samples, IReadOnlyList<DeploymentInfo> deployments)
    {
        if (samples.Count == 0)
            return [];

        // From here on an error group means one episode of it; members get their real fingerprint back at the end
        samples = SplitEpisodes(samples);

        var groups = samples.GroupBy(s => s.Fingerprint).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToList());
        var onset = groups.ToDictionary(g => g.Key, g => g.Value[0].Timestamp);
        var traces = TraceChains(samples);

        var links = new UnionFind(groups.Keys);
        foreach (var chain in traces)
        {
            foreach (var other in chain.Skip(1))
                links.Union(chain[0].Fingerprint, other.Fingerprint);
        }

        var keys = groups.Keys.OrderBy(k => onset[k]).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            for (var j = i + 1; j < keys.Count && onset[keys[j]] - onset[keys[i]] <= _options.CoOnsetWindow; j++)
            {
                if (PrecedingDeployment(deployments, onset[keys[i]]) is { } deployment && PrecedingDeployment(deployments, onset[keys[j]])?.Id == deployment.Id)
                    links.Union(keys[i], keys[j]);
            }
        }

        return links.Components()
            .Select(component => BuildIncident(component, groups, onset, traces, deployments))
            .OrderByDescending(incident => incident.StartedAt)
            .ToList();
    }

    private IncidentDraft BuildIncident(
        IReadOnlyList<string> fingerprints,
        Dictionary<string, List<ErrorSample>> groups,
        Dictionary<string, DateTimeOffset> onset,
        IReadOnlyList<List<ErrorSample>> traces,
        IReadOnlyList<DeploymentInfo> deployments)
    {
        var members = new HashSet<string>(fingerprints);
        var start = fingerprints.Min(f => onset[f]);
        var span = (fingerprints.Max(f => onset[f]) - start).TotalSeconds;
        var suspected = PrecedingDeployment(deployments, start);

        // Traces that cross members of this incident: who failed first in each?
        var first = new Dictionary<string, int>();
        var involved = new Dictionary<string, int>();
        foreach (var chain in traces.Where(c => c.Count(s => members.Contains(s.Fingerprint)) > 1))
        {
            foreach (var fingerprint in chain.Select(s => s.Fingerprint).Distinct())
                involved[fingerprint] = involved.GetValueOrDefault(fingerprint) + 1;
            first[chain[0].Fingerprint] = first.GetValueOrDefault(chain[0].Fingerprint) + 1;
        }

        var scored = fingerprints.Select(fingerprint =>
        {
            var samples = groups[fingerprint];
            var head = samples[0];
            var evidence = new List<string>();

            // Without shared traces there is no direction to read, so the signal stays neutral
            var upstream = 0.5;
            if (involved.TryGetValue(fingerprint, out var count))
            {
                upstream = (double)first.GetValueOrDefault(fingerprint) / count;
                evidence.Add($"Paylaşılan {count} izin (trace) {first.GetValueOrDefault(fingerprint)} tanesinde zincirdeki ilk hata bu");
            }

            // Only the last deployment before the incident is suspected; older ones in the window are not blamed
            var deployed = 0.0;
            if (suspected is not null && suspected.Application == head.Application)
            {
                var gap = onset[fingerprint] - suspected.StartedAt;
                deployed = Math.Clamp(1 - gap / _options.DeploymentWindow, 0, 1);
                evidence.Add(gap < TimeSpan.Zero
                    ? $"{head.Application} {suspected.Version} deploy'u sırasında başladı"
                    : $"{head.Application} {suspected.Version} deploy'undan {Seconds(gap)} sonra başladı");
            }

            var early = span <= 0 ? 1.0 : 1 - (onset[fingerprint] - start).TotalSeconds / span;
            if (onset[fingerprint] == start)
                evidence.Add("Olaydaki ilk hata grubu");

            var score = _options.UpstreamWeight * upstream + _options.DeploymentWeight * deployed + _options.OnsetWeight * early;
            return new IncidentMember(
                fingerprint[..fingerprint.IndexOf('#')], head.Application, head.ExceptionType, head.NormalizedMessage, head.TopFrame,
                ErrorCategorizer.Categorize(head.ExceptionType, head.NormalizedMessage),
                samples.Count, onset[fingerprint], samples[^1].Timestamp, Math.Round(score, 3), evidence);
        })
        .OrderByDescending(m => m.RootCauseScore)
        .ThenBy(m => m.FirstSeen)
        .ToList();

        return new IncidentDraft(start, scored.Max(m => m.LastSeen), scored, suspected);
    }

    private List<ErrorSample> SplitEpisodes(IReadOnlyList<ErrorSample> samples)
    {
        var result = new List<ErrorSample>(samples.Count);
        foreach (var group in samples.GroupBy(s => s.Fingerprint))
        {
            var episode = 0;
            DateTimeOffset? previous = null;
            foreach (var sample in group.OrderBy(s => s.Timestamp))
            {
                if (previous is { } p && sample.Timestamp - p > _options.EpisodeGap)
                    episode++;
                previous = sample.Timestamp;
                result.Add(sample with { Fingerprint = $"{group.Key}#{episode}" });
            }
        }

        return result;
    }

    /// <summary>Errors of each trace that spans more than one error group, in time order.</summary>
    private static List<List<ErrorSample>> TraceChains(IReadOnlyList<ErrorSample> samples) =>
        samples
            .Where(s => !string.IsNullOrEmpty(s.TraceId))
            .GroupBy(s => s.TraceId!)
            .Select(g => g.OrderBy(s => s.Timestamp).ToList())
            .Where(chain => chain.Select(s => s.Fingerprint).Distinct().Count() > 1)
            .ToList();

    private DeploymentInfo? PrecedingDeployment(IReadOnlyList<DeploymentInfo> deployments, DateTimeOffset onset) =>
        deployments
            .Where(d => d.StartedAt - _options.DeploymentLeadTime <= onset && onset - d.StartedAt <= _options.DeploymentWindow)
            .MaxBy(d => d.StartedAt);

    private static string Seconds(TimeSpan gap) => gap.TotalSeconds < 90 ? $"{gap.TotalSeconds:F0} sn" : $"{gap.TotalMinutes:F1} dk";

    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> _parent;

        public UnionFind(IEnumerable<string> items) => _parent = items.ToDictionary(i => i, i => i);

        public void Union(string a, string b) => _parent[Find(a)] = Find(b);

        public IEnumerable<List<string>> Components() =>
            _parent.Keys.GroupBy(Find).Select(g => g.ToList());

        private string Find(string item)
        {
            while (_parent[item] != item)
                item = _parent[item] = _parent[_parent[item]];
            return item;
        }
    }
}

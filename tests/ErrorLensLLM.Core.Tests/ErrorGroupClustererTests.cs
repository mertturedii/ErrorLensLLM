using ErrorLensLLM.Core.Grouping;

namespace ErrorLensLLM.Core.Tests;

public class ErrorGroupClustererTests
{
    private static readonly ErrorGroupText Schema = new("a", "Npgsql.PostgresException",
        "<num>: column \"donem_id\" does not exist", "ErrorLensLLM.Demo.KayitService.GetirAsync");
    private static readonly ErrorGroupText FromNot = new("b", "System.Net.Http.HttpRequestException",
        "Response status code does not indicate success: <num> (Internal Server Error).", "ErrorLensLLM.Demo.Modules+<>c.<MapNot>b__1_0");
    private static readonly ErrorGroupText FromOdeme = new("c", "System.Net.Http.HttpRequestException",
        "Response status code does not indicate success: <num> (Internal Server Error).", "ErrorLensLLM.Demo.Modules+<>c.<MapOdeme>b__2_0");
    private static readonly ErrorGroupText Refused = new("d", "System.Net.Http.HttpRequestException",
        "Connection refused (localhost:<num>)", "ErrorLensLLM.Demo.KayitClient.GetirAsync");
    private static readonly ErrorGroupText Timeout = new("e", "System.TimeoutException",
        "Banka <num> numarali odeme icin <num> sn icinde yanit vermedi", "ErrorLensLLM.Demo.OdemeService.TahsilEtAsync");

    [Fact]
    public void The_same_error_from_two_callers_forms_one_cluster()
    {
        var clustering = new ErrorGroupClusterer().Cluster([Schema, FromNot, FromOdeme, Timeout]);

        var cluster = clustering.ClusterOf("b");
        Assert.NotNull(cluster);
        Assert.Equal(["b", "c"], cluster.Fingerprints.Order());
        Assert.Null(clustering.ClusterOf("a"));
        Assert.Null(clustering.ClusterOf("e"));
        Assert.Equal(4, cluster.Terms.Count);
    }

    [Fact]
    public void Similar_groups_are_ranked_by_cosine_similarity()
    {
        var clustering = new ErrorGroupClusterer().Cluster([Schema, FromNot, FromOdeme, Refused, Timeout]);

        var similar = clustering.Similar("b");

        Assert.Equal("c", similar[0].Fingerprint);
        Assert.True(similar[0].Similarity > 0.8);
        // Another HTTP failure of a different kind is related, but less so
        var refused = Assert.Single(similar, s => s.Fingerprint == "d");
        Assert.True(refused.Similarity < similar[0].Similarity);
        Assert.DoesNotContain(similar, s => s.Fingerprint == "e");
    }

    [Fact]
    public void A_single_group_is_its_own_cluster()
    {
        var clustering = new ErrorGroupClusterer().Cluster([Schema]);

        Assert.Single(clustering.Clusters);
        Assert.Null(clustering.ClusterOf("a"));
        Assert.Empty(clustering.Similar("a"));
    }
}

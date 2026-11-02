using System.Text.RegularExpressions;
using ErrorLensLLM.Ml.Parsing;

namespace ErrorLensLLM.Ml.Tests;

public class DrainParserTests
{
    [Fact]
    public void Messages_differing_in_variables_share_a_template()
    {
        var parser = new DrainParser();

        var a = parser.Parse("Ogrenci 20231045 kaydi tamamlandi sure 3 sn");
        var b = parser.Parse("Ogrenci 20229981 kaydi tamamlandi sure 11 sn");

        Assert.Same(a, b);
        Assert.Equal("Ogrenci <*> kaydi tamamlandi sure <*> sn", a.Text);
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void Different_events_get_different_templates()
    {
        var parser = new DrainParser();

        var saved = parser.Parse("Kayit tamamlandi ogrenci 42");
        var failed = parser.Parse("Baglanti havuzu tukendi bekleme 30");

        Assert.NotEqual(saved.Id, failed.Id);
        Assert.Equal(2, parser.Templates.Count);
    }

    [Fact]
    public void Messages_with_different_token_counts_never_merge()
    {
        var parser = new DrainParser();

        var shortMessage = parser.Parse("Receiving block x");
        var longMessage = parser.Parse("Receiving block x from y");

        Assert.NotEqual(shortMessage.Id, longMessage.Id);
    }

    [Fact]
    public void Mask_patterns_are_applied_before_matching()
    {
        var parser = new DrainParser(maskPatterns: [new Regex(@"blk_-?\d+")]);

        var template = parser.Parse("Deleting block blk_-1608999687919862906");

        Assert.Equal("Deleting block <*>", template.Text);
    }

    [Fact]
    public void Original_similarity_cannot_match_variable_heavy_messages_at_high_threshold()
    {
        var parser = new DrainParser(similarityThreshold: 0.6, maskPatterns: [new Regex(@"\d+")]);

        // 3 of 7 tokens are variables: even identical messages score 4/7 < 0.6
        parser.Parse("Receiving block 1 src: 2 dest: 3");
        parser.Parse("Receiving block 4 src: 5 dest: 6");

        Assert.Equal(2, parser.Templates.Count);
    }

    [Fact]
    public void Constant_token_similarity_keeps_variables_out_and_separates_single_word_changes()
    {
        var parser = new DrainParser(similarityThreshold: 0.8, maskPatterns: [new Regex(@"\d+")], similarity: DrainSimilarity.ConstantTokens);

        var a = parser.Parse("Receiving block 1 src: 2 dest: 3");
        var b = parser.Parse("Receiving block 4 src: 5 dest: 6");
        var terminating = parser.Parse("PacketResponder 1 for block 7 terminating");
        var interrupted = parser.Parse("PacketResponder 2 for block 8 interrupted");

        Assert.Same(a, b);
        Assert.NotSame(terminating, interrupted);
    }

    [Fact]
    public void Match_does_not_learn()
    {
        var parser = new DrainParser();
        parser.Parse("Kayit tamamlandi ogrenci 42");

        Assert.NotNull(parser.Match("Kayit tamamlandi ogrenci 43"));
        Assert.Null(parser.Match("Tamamen farkli bir mesaj"));
        Assert.Single(parser.Templates);
    }
}

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
    public void Match_does_not_learn()
    {
        var parser = new DrainParser();
        parser.Parse("Kayit tamamlandi ogrenci 42");

        Assert.NotNull(parser.Match("Kayit tamamlandi ogrenci 43"));
        Assert.Null(parser.Match("Tamamen farkli bir mesaj"));
        Assert.Single(parser.Templates);
    }
}

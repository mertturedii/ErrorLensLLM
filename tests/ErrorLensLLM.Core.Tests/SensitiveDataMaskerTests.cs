using ErrorLensLLM.Core.Processing;

namespace ErrorLensLLM.Core.Tests;

public class SensitiveDataMaskerTests
{
    private readonly SensitiveDataMasker _masker = new(["password", "sifre"]);

    [Theory]
    [InlineData("Kullanıcı 12345678901 bulunamadı", "Kullanıcı *** bulunamadı")]
    [InlineData("Mail gönderilemedi: ali.veli@ornek.edu.tr", "Mail gönderilemedi: ***")]
    [InlineData("SMS hatası 0532 123 45 67", "SMS hatası ***")]
    [InlineData("SMS hatası +905321234567", "SMS hatası ***")]
    public void Personal_data_is_masked(string input, string expected)
    {
        Assert.Equal(expected, _masker.MaskText(input));
    }

    [Theory]
    [InlineData("password=Gizli123;Host=x", "password=***;Host=x")]
    [InlineData("""{"sifre":"abc","ad":"x"}""", """{"sifre":"***","ad":"x"}""")]
    public void Configured_fields_are_masked(string input, string expected)
    {
        Assert.Equal(expected, _masker.MaskText(input));
    }

    [Fact]
    public void Short_numbers_are_not_masked()
    {
        Assert.Equal("Id=42 bulunamadı", _masker.MaskText("Id=42 bulunamadı"));
    }
}

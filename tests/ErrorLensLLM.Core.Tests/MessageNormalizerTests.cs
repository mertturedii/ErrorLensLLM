using ErrorLensLLM.Core.Processing;

namespace ErrorLensLLM.Core.Tests;

public class MessageNormalizerTests
{
    [Fact]
    public void Same_error_with_different_data_produces_same_template()
    {
        var a = MessageNormalizer.Normalize("Kayıt bulunamadı: Id=42, Donem='2025-Güz', Ref=3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        var b = MessageNormalizer.Normalize("Kayıt bulunamadı: Id=1907, Donem='2026-Bahar', Ref=9b2c7a10-1111-4222-8333-444455556666");

        Assert.Equal(a, b);
        Assert.Equal("Kayıt bulunamadı: Id=<num>, Donem='<val>', Ref=<guid>", a);
    }

    [Fact]
    public void Dates_are_replaced()
    {
        Assert.Equal("Timeout at <date>", MessageNormalizer.Normalize("Timeout at 2026-10-27T14:03:11Z"));
    }

    [Fact]
    public void Identifiers_containing_digits_are_kept()
    {
        Assert.Equal("Column Ad2 missing", MessageNormalizer.Normalize("Column Ad2 missing"));
    }

    [Fact]
    public void Turkish_suffix_apostrophes_are_not_quoted_values()
    {
        Assert.Equal("Öğrenci'nin kaydı Ankara'da bulunamadı", MessageNormalizer.Normalize("Öğrenci'nin kaydı Ankara'da bulunamadı"));
    }

    [Fact]
    public void Version_numbers_are_not_dates()
    {
        var normalized = MessageNormalizer.Normalize("Version 1.2.3 hata");

        Assert.DoesNotContain("<date>", normalized);
        Assert.Equal("Version <num>.<num> hata", normalized); // 1.2 reads as a decimal, .3 as a number
    }

    [Fact]
    public void Day_first_dates_are_still_dates()
    {
        Assert.Equal("Sınav <date> tarihinde", MessageNormalizer.Normalize("Sınav 15.01.2026 tarihinde"));
    }
}

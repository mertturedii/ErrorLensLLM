using ErrorLensLLM.Core.Models;
using ErrorLensLLM.Core.Processing;

namespace ErrorLensLLM.Core.Tests;

public class ErrorFingerprinterTests
{
    private static StackFrameInfo App(string type, string method, int line) =>
        new(type, method, $"/src/{type}.cs", line, "Ogrenci.Api", IsApplicationCode: true);

    private static StackFrameInfo Framework(string type, string method) =>
        new(type, method, null, null, "System.Private.CoreLib", IsApplicationCode: false);

    [Fact]
    public void Line_number_changes_do_not_change_fingerprint()
    {
        var before = ErrorFingerprinter.Compute("System.NullReferenceException", "x",
            [App("Ogrenci.KayitService", "GetKayit", 42), App("Ogrenci.KayitController", "Get", 10)]);
        var afterDeploy = ErrorFingerprinter.Compute("System.NullReferenceException", "x",
            [App("Ogrenci.KayitService", "GetKayit", 57), App("Ogrenci.KayitController", "Get", 12)]);

        Assert.Equal(before, afterDeploy);
    }

    [Fact]
    public void Framework_frames_are_ignored()
    {
        var a = ErrorFingerprinter.Compute("E", "x", [Framework("System.Linq.Enumerable", "First"), App("A.B", "C", 1)]);
        var b = ErrorFingerprinter.Compute("E", "x", [App("A.B", "C", 1)]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_method_produces_different_fingerprint()
    {
        var a = ErrorFingerprinter.Compute("E", "x", [App("A.B", "C", 1)]);
        var b = ErrorFingerprinter.Compute("E", "x", [App("A.B", "D", 1)]);

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("Ogrenci.KayitService+<GetKayit>d__4", "MoveNext")]          // async method
    [InlineData("Ogrenci.KayitService", "<GetKayit>b__0")]                      // lambda
    [InlineData("Ogrenci.KayitService+<>c__DisplayClass2_0", "<GetKayit>b__1")] // closure
    public void Compiler_generated_names_resolve_to_the_source_method(string type, string method)
    {
        Assert.Equal("Ogrenci.KayitService.GetKayit", ErrorFingerprinter.FrameKey(App(type, method, 1)));
    }

    [Fact]
    public void Local_function_ordinals_do_not_change_fingerprint()
    {
        var before = ErrorFingerprinter.Compute("E", "x", [App("X.Svc", "<Run>g__Load|4_0", 1)]);
        var after = ErrorFingerprinter.Compute("E", "x", [App("X.Svc", "<Run>g__Load|5_0", 1)]);

        Assert.Equal(before, after);
        Assert.Equal("X.Svc.Run.Load", ErrorFingerprinter.FrameKey(App("X.Svc", "<Run>g__Load|4_0", 1)));
    }

    [Fact]
    public void Different_messages_from_the_same_method_get_different_fingerprints()
    {
        var notFound = ErrorFingerprinter.Compute("System.InvalidOperationException", "Kayit bulunamadi", [App("A.B", "C", 1)]);
        var closed = ErrorFingerprinter.Compute("System.InvalidOperationException", "Donem kapali", [App("A.B", "C", 1)]);

        Assert.NotEqual(notFound, closed);
    }

    [Fact]
    public void Without_application_frames_message_template_is_used()
    {
        var a = ErrorFingerprinter.Compute("E", "Timeout <num>", []);
        var b = ErrorFingerprinter.Compute("E", "Connection refused", []);

        Assert.NotEqual(a, b);
    }
}

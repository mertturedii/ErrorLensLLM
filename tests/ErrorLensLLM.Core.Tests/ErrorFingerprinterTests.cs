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

    [Fact]
    public void Async_state_machine_names_are_cleaned()
    {
        var sync = ErrorFingerprinter.Compute("E", "x", [App("Ogrenci.KayitService", "GetKayit", 1)]);
        var async = ErrorFingerprinter.Compute("E", "x", [App("Ogrenci.KayitService+<GetKayit>d__4", "MoveNext", 1)]);
        var asyncMethod = ErrorFingerprinter.Compute("E", "x", [App("Ogrenci.KayitService", "<GetKayit>b__0", 1)]);

        Assert.NotEqual(sync, async); // MoveNext vs GetKayit differ; type is cleaned
        Assert.Equal(sync, asyncMethod);
    }

    [Fact]
    public void Without_application_frames_message_template_is_used()
    {
        var a = ErrorFingerprinter.Compute("E", "Timeout <num>", []);
        var b = ErrorFingerprinter.Compute("E", "Connection refused", []);

        Assert.NotEqual(a, b);
    }
}

using ErrorLensLLM.Core.Processing;

namespace ErrorLensLLM.Core.Tests;

public class StackFrameExtractorTests
{
    [Fact]
    public void Extracts_innermost_exception_frames_with_line_numbers()
    {
        Exception captured = null!;
        try
        {
            try { Thrower.Fail(); }
            catch (Exception inner) { throw new InvalidOperationException("wrapper", inner); }
        }
        catch (Exception ex) { captured = ex; }

        var frames = StackFrameExtractor.Extract(captured);
        var top = frames.First(f => f.IsApplicationCode);

        Assert.Equal(nameof(Thrower.Fail), top.MethodName);
        Assert.NotNull(top.LineNumber);
        Assert.EndsWith("StackFrameExtractorTests.cs", top.FilePath);
    }

    [Fact]
    public void Wrapped_library_exception_uses_the_wrapper_that_reaches_application_code()
    {
        Exception captured = null!;
        try { System.Text.Json.JsonSerializer.Deserialize<int>("not json"); }
        catch (Exception ex) { captured = ex; }

        // The reader error is thrown and caught inside System.Text.Json; only the wrapper's stack reaches this test
        Assert.NotNull(captured.InnerException);
        var (origin, frames) = StackFrameExtractor.ExtractOrigin(captured);

        Assert.Same(captured, origin);
        Assert.Contains(frames, f => f.IsApplicationCode && f.MethodName == nameof(Wrapped_library_exception_uses_the_wrapper_that_reaches_application_code));
    }

    private static class Thrower
    {
        public static void Fail() => throw new ArgumentException("boom");
    }
}

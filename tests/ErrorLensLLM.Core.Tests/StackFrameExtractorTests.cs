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

    private static class Thrower
    {
        public static void Fail() => throw new ArgumentException("boom");
    }
}

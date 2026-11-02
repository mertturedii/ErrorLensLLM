using System.Runtime.InteropServices;
using static TorchSharp.torch;

namespace ErrorLensLLM.Ml.Detection;

/// <summary>
/// The macOS libtorch packages ship dylibs whose @rpath points at the PyTorch build machine, so TorchSharp
/// cannot resolve their dependencies. Loading them in dependency order first lets its own load succeed.
/// </summary>
internal static class TorchNative
{
    private static readonly string[] MacOsLoadOrder = ["omp", "c10", "torch_cpu", "torch", "LibTorchSharp"];
    private static readonly Lazy<bool> Loaded = new(Preload);

    public static void EnsureLoaded() => _ = Loaded.Value;

    private static bool Preload()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        // Probe through deps.json like TorchSharp does, so the same copy is loaded (a second copy aborts with
        // "Key already registered ... C10"); fall back to the cpu folder a plain build output uses.
        var assembly = typeof(Tensor).Assembly;
        var cpuDirectory = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "cpu");

        foreach (var name in MacOsLoadOrder)
        {
            if (!NativeLibrary.TryLoad(name, assembly, null, out _))
                NativeLibrary.TryLoad(Path.Combine(cpuDirectory, $"lib{name}.dylib"), out _);
        }

        return true;
    }
}

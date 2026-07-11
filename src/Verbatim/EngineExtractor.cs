using System.Reflection;

namespace Verbatim;

/// <summary>
/// The sherpa-onnx executables ride inside Verbatim.exe as embedded resources
/// (keeping the app a single portable file, like Photon). On first run they
/// are unpacked to LocalAppData, versioned by app version so upgrades refresh
/// them.
/// </summary>
public static class EngineExtractor
{
    private static readonly string[] Files =
    [
        "sherpa-onnx-offline.exe",
        "sherpa-onnx-offline-speaker-diarization.exe",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll"
    ];

    public static string EnsureExtracted()
    {
        var version = Application.ProductVersion.Split('+')[0];
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Verbatim", "engines", version);
        if (Files.All(f => File.Exists(Path.Combine(dir, f)))) return dir;

        Directory.CreateDirectory(dir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var f in Files)
        {
            using var stream = asm.GetManifestResourceStream("engines." + f)
                ?? throw new InvalidOperationException($"Missing embedded engine {f}");
            var tmp = Path.Combine(dir, f + ".tmp");
            using (var outp = File.Create(tmp)) stream.CopyTo(outp);
            File.Move(tmp, Path.Combine(dir, f), overwrite: true);
        }

        // prune engine dirs from older versions
        var parent = Path.GetDirectoryName(dir)!;
        foreach (var old in Directory.EnumerateDirectories(parent))
        {
            if (!old.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(old, true); } catch { /* in use */ }
            }
        }
        return dir;
    }
}

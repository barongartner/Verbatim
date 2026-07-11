using System.Diagnostics;
using System.Text;

namespace Verbatim.Core;

public class ProcessResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string StderrTail { get; init; } = "";
}

/// <summary>
/// Async external-process runner. Arguments are passed as a list (never a
/// shell), stderr is streamed line-by-line for progress parsing, and
/// cancellation kills the whole process tree (yt-dlp's onefile binaries
/// re-exec themselves, so killing only the direct child leaves the real
/// worker running).
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderrTail = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrTail.AppendLine(e.Data);
            if (stderrTail.Length > 4000) stderrTail.Remove(0, stderrTail.Length - 3000);
            onStderrLine?.Invoke(e.Data);
        };

        if (!proc.Start()) throw new InvalidOperationException($"Could not start {exe}");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        });

        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        return new ProcessResult
        {
            ExitCode = proc.ExitCode,
            Stdout = stdout.ToString(),
            StderrTail = stderrTail.ToString()
        };
    }

    public static async Task<string> RunCheckedAsync(
        string exe,
        IEnumerable<string> args,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken ct = default)
    {
        var r = await RunAsync(exe, args, onStdoutLine, onStderrLine, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new EngineException(
                $"{Path.GetFileName(exe)} exited with code {r.ExitCode}", r.StderrTail);
        }
        return r.Stdout;
    }
}

public class EngineException(string message, string stderrTail) : Exception(message)
{
    public string StderrTail { get; } = stderrTail;

    /// <summary>yt-dlp and sherpa print helpful "ERROR: ..." lines — surface those.</summary>
    public string FriendlyMessage
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(StderrTail, @"ERROR:\s*(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : Message;
        }
    }
}

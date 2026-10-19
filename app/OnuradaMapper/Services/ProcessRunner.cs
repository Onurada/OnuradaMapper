using System.Diagnostics;
using System.IO;
using System.Text;

namespace OnuradaMapper.Services;

public static class ProcessRunner
{
    /// <summary>Runs a process, streaming stdout+stderr lines (split on \r too, so tqdm bars come through). Kills the tree on cancel.</summary>
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken ct,
                                           string? cwd = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = cwd ?? AppEnv.Workspace,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env ?? AppEnv.ChildEnv()) psi.Environment[kv.Key] = kv.Value;
        if (!Directory.Exists(psi.WorkingDirectory)) psi.WorkingDirectory = AppContext.BaseDirectory;

        using var p = new Process { StartInfo = psi };
        p.Start();
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });
        var t1 = Pump(p.StandardOutput, onLine);
        var t2 = Pump(p.StandardError, onLine);
        await Task.WhenAll(t1, t2);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    public static async Task<(int code, string output)> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct = default,
                                                                      IDictionary<string, string>? env = null)
    {
        var sb = new StringBuilder();
        var code = await RunAsync(exe, args, l => { lock (sb) sb.AppendLine(l); }, ct, env: env);
        return (code, sb.ToString());
    }

    static async Task Pump(StreamReader r, Action<string> onLine)
    {
        var buf = new char[4096];
        var line = new StringBuilder();
        int n;
        while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c == '\n' || c == '\r')
                {
                    if (line.Length > 0) { onLine(line.ToString()); line.Clear(); }
                }
                else line.Append(c);
            }
        }
        if (line.Length > 0) onLine(line.ToString());
    }
}

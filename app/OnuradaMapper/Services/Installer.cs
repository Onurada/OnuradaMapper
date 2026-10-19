using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace OnuradaMapper.Services;

/// <summary>Setup steps: helper tools, the Python environment, BepInEx + the mod, and the official-map corpus.</summary>
public static class Installer
{
    const string UvUrl = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";
    const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    public const string PythonCheck =
        "import torch, torchaudio, demucs, faster_whisper, librosa, UnityPy, speechbrain, etcpak, PIL, scipy; print('OK', torch.__version__, torch.cuda.is_available())";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static Installer() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("OnuradaMapper/1.0");

    public static async Task Download(JobContext c, string url, string dest)
    {
        c.Log("GET " + url);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, c.Ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(c.Ct);
        await using var dst = File.Create(dest + ".part");
        var buf = new byte[1 << 16];
        long got = 0; int n;
        while ((n = await src.ReadAsync(buf, c.Ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), c.Ct);
            got += n;
            if (total > 0) { c.Step.Progress = 100.0 * got / total; c.Step.Detail = $"{got / 1048576} / {total / 1048576} MB"; }
        }
        dst.Close();
        File.Move(dest + ".part", dest, true);
    }

    static string Temp(string name) => Path.Combine(AppEnv.AppData, "downloads", name);

    // ---------------- helper exes ----------------
    public static JobStep Uv() => new("uv", async c =>
    {
        if (File.Exists(Path.Combine(AppEnv.BinDir, "uv.exe"))) { c.Skip("OK"); return; }
        var zip = Temp("uv.zip");
        await Download(c, UvUrl, zip);
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries.Where(e => e.Name.EndsWith(".exe")))
                e.ExtractToFile(Path.Combine(AppEnv.BinDir, e.Name), true);
        File.Delete(zip);
    });

    public static JobStep Ffmpeg() => new("ffmpeg", async c =>
    {
        if (AppEnv.Exe("ffmpeg") != null && AppEnv.Exe("ffprobe") != null) { c.Skip(AppEnv.Exe("ffmpeg")!); return; }
        var zip = Temp("ffmpeg.zip");
        await Download(c, FfmpegUrl, zip);
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries.Where(e => e.FullName.Contains("/bin/") && e.Name.EndsWith(".exe")))
                e.ExtractToFile(Path.Combine(AppEnv.BinDir, e.Name), true);
        File.Delete(zip);
    });

    public static JobStep YtDlp() => new("yt-dlp", async c =>
    {
        var have = AppEnv.Exe("yt-dlp");
        if (have != null) { c.Skip(have); return; }
        await Download(c, YtDlpUrl, Path.Combine(AppEnv.BinDir, "yt-dlp.exe"));
    });

    // ---------------- python ----------------
    public static bool HasNvidiaGpu() => AppEnv.Exe("nvidia-smi") != null || File.Exists(@"C:\Windows\System32\nvidia-smi.exe");

    public static IEnumerable<JobStep> PythonEnv()
    {
        yield return Uv();
        var uv = Path.Combine(AppEnv.BinDir, "uv.exe");
        Dictionary<string, string> UvEnv()
        {
            var env = AppEnv.ChildEnv();
            env["UV_PYTHON_INSTALL_DIR"] = Path.Combine(AppEnv.AppData, "python");
            env["UV_NO_CACHE"] = "1";   // the C drive tends to be full; don't keep a second copy of torch
            return env;
        }
        async Task UvRun(JobContext c, params string[] args)
        {
            var code = await ProcessRunner.RunAsync(uv, args, c.Log, c.Ct, env: UvEnv());
            if (code != 0) throw new StepFailedException("uv exit " + code);
        }
        yield return new JobStep("Python 3.12", async c =>
        {
            if (File.Exists(AppEnv.VenvPython)) { c.Skip("OK"); return; }
            await UvRun(c, "venv", "--python", "3.12", AppEnv.VenvDir);
        });
        yield return new JobStep("PyTorch", async c =>
        {
            var gpu = HasNvidiaGpu();
            c.Step.Detail = gpu ? "CUDA 12.4 (~2.5 GB)" : "CPU";
            await UvRun(c, "pip", "install", "--python", AppEnv.VenvPython, "torch==2.6.0", "torchaudio==2.6.0",
                        "--index-url", "https://download.pytorch.org/whl/" + (gpu ? "cu124" : "cpu"));
        });
        yield return new JobStep("demucs, whisper, librosa...", async c =>
        {
            await UvRun(c, "pip", "install", "--python", AppEnv.VenvPython, "-r", Path.Combine(AppEnv.ToolsDir, "requirements.txt"));
        });
        yield return new JobStep(Loc.T("StepCheck"), async c =>
        {
            AppEnv.Settings.PythonPath = ""; AppEnv.Save();
            var o = await c.Exec(AppEnv.VenvPython, "-c", PythonCheck);
            c.Step.Detail = o.Trim().Split('\n').Last();
        });
    }

    /// <summary>Returns "OK torch cuda" output, or null when the interpreter lacks a package.</summary>
    public static async Task<string?> CheckPython(string python)
    {
        if (string.IsNullOrEmpty(python) || !File.Exists(python)) return null;
        try
        {
            var (code, outp) = await ProcessRunner.CaptureAsync(python, ["-c", PythonCheck]);
            var line = outp.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("OK "));
            return code == 0 ? line : null;
        }
        catch { return null; }
    }

    /// <summary>Other interpreters worth trying (e.g. a Python that already has everything installed).</summary>
    public static IEnumerable<string> PythonCandidates()
    {
        yield return AppEnv.VenvPython;
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var v in new[] { "Python312", "Python311", "Python313" })
            yield return Path.Combine(la, "Programs", "Python", v, "python.exe");
    }

    // ---------------- game side ----------------
    static void NeedGameClosed()
    {
        if (GameLocator.IsRunning()) throw new StepFailedException(Loc.T("ErrGameRunning"));
    }

    static string Game => GameLocator.IsGameDir(AppEnv.Settings.GameDir) ? AppEnv.Settings.GameDir : throw new StepFailedException(Loc.T("ErrNoGame"));

    public static JobStep BepInEx() => new("BepInEx 5.4.23.5", async c =>
    {
        var g = Game;
        if (GameLocator.BepInExInstalled(g)) { c.Skip("OK"); return; }
        NeedGameClosed();
        var zip = Temp("bepinex.zip");
        await Download(c, BepInExUrl, zip);
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(g, e.FullName));
                if (!path.StartsWith(Path.GetFullPath(g), StringComparison.OrdinalIgnoreCase)) continue;
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                if (File.Exists(path)) continue;   // keep the user's existing config
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path);
            }
        Directory.CreateDirectory(Path.Combine(g, "BepInEx", "plugins"));
        File.Delete(zip);
    });

    public static JobStep Mod() => new("OnuradaLyrics", c =>
    {
        var g = Game;
        var src = AppEnv.ModDll ?? throw new StepFailedException(Loc.T("ErrNoModDll"));
        var dst = GameLocator.ModPath(g);
        var have = GameLocator.DllVersion(dst); var ours = GameLocator.DllVersion(src);
        if (have != null && ours != null && have >= ours) { c.Skip("v" + have); return Task.CompletedTask; }
        NeedGameClosed();
        if (File.Exists(dst))
        {
            var bak = AppEnv.WS("backups", DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_mod");
            Directory.CreateDirectory(bak);
            File.Copy(dst, Path.Combine(bak, "OnuradaLyrics.dll"), true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        c.Step.Detail = "v" + ours;
        return Task.CompletedTask;
    });

    public static bool CorpusReady() => File.Exists(AppEnv.WS("corpus.pkl")) && File.Exists(AppEnv.WS("levelenv.json"));

    public static IEnumerable<JobStep> Corpus(bool force = false)
    {
        yield return new JobStep(Loc.T("StepExtractLevels"), async c =>
        {
            if (!force && CorpusReady()) { c.Skip("OK"); return; }
            await c.Py(c.Tool("extract_corpus.py"), Game, AppEnv.WS("corpus"));
        });
        yield return new JobStep(Loc.T("StepLevelEnvs"), async c =>
        {
            if (!force && CorpusReady()) { c.Skip("OK"); return; }
            await c.Py(c.Tool("env_of_levels.py"), Path.Combine(Game, "Beat Saber_Data", "StreamingAssets", "aa", "StandaloneWindows64"), AppEnv.WS("levelenv.json"));
        });
        yield return new JobStep(Loc.T("StepCorpus"), async c =>
        {
            if (!force && CorpusReady()) { c.Skip("OK"); return; }
            await c.Py(c.Tool("corpus.py"), AppEnv.WS("corpus"), AppEnv.WS("levelenv.json"), AppEnv.WS("corpus.pkl"));
        });
    }

    /// <summary>Copies a built level into CustomLevels, moving any older copy into backups first.</summary>
    public static JobStep InstallLevel(string folder) => new(Loc.T("StepInstall"), c =>
    {
        var g = Game;
        NeedGameClosed();
        var src = AppEnv.WS("out", folder);
        if (!File.Exists(Path.Combine(src, "Info.dat"))) throw new StepFailedException(Loc.T("ErrNotBuilt"));
        var dst = Path.Combine(g, "Beat Saber_Data", "CustomLevels", folder);
        if (Directory.Exists(dst))
        {
            var bak = AppEnv.WS("backups", DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_" + folder);
            CopyDir(dst, bak);
            Directory.Delete(dst, true);
            c.Log("backup: " + bak);
        }
        CopyDir(src, dst);
        c.Step.Detail = dst;
        return Task.CompletedTask;
    });

    public static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}

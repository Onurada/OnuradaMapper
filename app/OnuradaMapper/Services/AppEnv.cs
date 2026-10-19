using System.IO;
using System.Text.Json;

namespace OnuradaMapper.Services;

public sealed class AppSettings
{
    public string Workspace { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BeatSaberMapper");
    public string GameDir { get; set; } = "";
    public string PythonPath { get; set; } = "";
    public string MapperName { get; set; } = Environment.UserName;
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    public string Device { get; set; } = "auto";          // auto | cuda | cpu
    public string WhisperModel { get; set; } = "large-v3";
    public bool InstallAfterBuild { get; set; } = true;
    public string Theme { get; set; } = "system";       // system | light | dark
}

/// <summary>Settings plus every path the app works with.</summary>
public static class AppEnv
{
    public static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnuradaMapper");
    static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnuradaMapper", "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public const string RepoSlug = "Onurada/OnuradaMapper";
    public static AppSettings Settings { get; private set; } = new();

    public static void Load()
    {
        try { if (File.Exists(SettingsFile)) Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { Settings = new(); }
        if (string.IsNullOrEmpty(Settings.GameDir)) Settings.GameDir = GameLocator.Find() ?? "";
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
    }

    public static string BinDir => Path.Combine(AppData, "bin");
    public static string VenvDir => Path.Combine(AppData, "venv");
    public static string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");
    public static string Workspace => Settings.Workspace;
    public static string WS(params string[] parts) => Path.Combine([Workspace, .. parts]);

    /// <summary>The folder holding tools/ and mod/: next to the exe in a release, the repository root when run from a dev build.</summary>
    public static readonly string AppRoot = FindAppRoot();

    static string FindAppRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "build.py"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    /// <summary>The Python pipeline (tools/*.py). Independent of the workspace, which only holds data.</summary>
    public static string ToolsDir => File.Exists(Path.Combine(AppRoot, "tools", "build.py")) ? Path.Combine(AppRoot, "tools") : WS("tools");

    public static string SongsJson => File.Exists(WS("tools", "songs.json")) ? WS("tools", "songs.json") : WS("songs.json");

    public static string? ModDll
    {
        get
        {
            foreach (var p in new[] { Path.Combine(AppRoot, "mod", "OnuradaLyrics.dll"),
                                      Path.Combine(AppRoot, "mod", "OnuradaLyrics", "bin", "Release", "net472", "OnuradaLyrics.dll") })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    public static string Python
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.PythonPath) && File.Exists(Settings.PythonPath)) return Settings.PythonPath;
            return File.Exists(VenvPython) ? VenvPython : "";
        }
    }

    /// <summary>Full path of a helper exe: our bin folder first, then PATH.</summary>
    public static string? Exe(string name)
    {
        var local = Path.Combine(BinDir, name + ".exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static Dictionary<string, string> ChildEnv()
    {
        var extra = new List<string> { BinDir };
        var ff = Exe("ffmpeg");
        if (ff != null) extra.Add(Path.GetDirectoryName(ff)!);
        return new()
        {
            ["PATH"] = string.Join(Path.PathSeparator, extra) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
            ["BSM_ROOT"] = Workspace,
            ["BSM_DEVICE"] = Settings.Device,
            ["BSM_MAPPER"] = Settings.MapperName,
            ["BSM_WHISPER"] = Settings.WhisperModel,
        };
    }

    public static string CustomLevelsDir => Path.Combine(Settings.GameDir, "Beat Saber_Data", "CustomLevels");
}

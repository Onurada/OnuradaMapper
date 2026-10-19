using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OnuradaMapper.Services;

public static class GameLocator
{
    public static bool IsGameDir(string? dir) => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Beat Saber.exe"));

    public static string? Find() => Candidates().FirstOrDefault(IsGameDir);

    static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Desktop", "Beat Saber");
        // Steam libraries
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam != null)
        {
            yield return Path.Combine(steam, "steamapps", "common", "Beat Saber");
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Beat Saber");
        }
        // Meta / Oculus libraries
        using (var libs = Registry.CurrentUser.OpenSubKey(@"Software\Oculus VR, LLC\Oculus\Libraries"))
            if (libs != null)
                foreach (var name in libs.GetSubKeyNames())
                    if (libs.OpenSubKey(name)?.GetValue("OriginalPath") is string op)
                        yield return Path.Combine(op, "Software", "hyperbolic-magnetism-beat-saber");
        yield return @"C:\Program Files (x86)\Steam\steamapps\common\Beat Saber";
        yield return @"C:\Program Files\Oculus\Software\Software\hyperbolic-magnetism-beat-saber";
    }

    public static string Version(string dir)
    {
        try { return File.ReadAllText(Path.Combine(dir, "BeatSaberVersion.txt")).Trim(); } catch { return "?"; }
    }

    public static bool IsRunning() => Process.GetProcessesByName("Beat Saber").Length > 0;

    public static bool BepInExInstalled(string dir) =>
        File.Exists(Path.Combine(dir, "winhttp.dll")) && File.Exists(Path.Combine(dir, "BepInEx", "core", "BepInEx.dll"));

    public static string ModPath(string dir) => Path.Combine(dir, "BepInEx", "plugins", "OnuradaLyrics.dll");

    public static Version? DllVersion(string? path)
    {
        try { return path != null && File.Exists(path) ? System.Reflection.AssemblyName.GetAssemblyName(path).Version : null; }
        catch { return null; }
    }
}

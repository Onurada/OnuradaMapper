using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public sealed record FolderUse(string Name, string Path, double Mb, string Note);

public sealed record BackupInfo(string Name, string Path, DateTime Date, double Mb, bool IsLevel);

public partial class CfgEntry : ObservableObject
{
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    public string Description { get; init; } = "";
    public string Default { get; init; } = "";
    public string Type { get; init; } = "";
    [ObservableProperty] private string _value = "";
    public bool IsBool => Type == "Boolean";
    public bool BoolValue { get => Value.Equals("true", StringComparison.OrdinalIgnoreCase); set => Value = value ? "true" : "false"; }
}

/// <summary>Maintenance: disk use and clean-up, backups, the mod's settings file.</summary>
public partial class ToolsViewModel : ObservableObject
{
    public ObservableCollection<FolderUse> Folders { get; } = [];
    public ObservableCollection<BackupInfo> Backups { get; } = [];
    public ObservableCollection<CfgEntry> ModSettings { get; } = [];
    [ObservableProperty] private string _driveText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private BackupInfo? _selectedBackup;
    [ObservableProperty] private string _stemsText = "";
    [ObservableProperty] private string _modCfgText = "";

    static string ModCfg => Path.Combine(AppEnv.Settings.GameDir, "BepInEx", "config", "onurada.lyrics.cfg");
    static string HfCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface");

    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            var notes = new Dictionary<string, string>
            {
                ["stems"] = Loc.T("FoStems"), ["audio"] = Loc.T("FoAudio"), ["out"] = Loc.T("FoOut"), ["backups"] = Loc.T("FoBackups"),
                ["corpus"] = Loc.T("FoCorpus"), ["vid"] = Loc.T("FoVid"), ["ana"] = "", ["words"] = "", ["covers"] = "", ["thumbs"] = "", ["logs"] = "", ["models"] = "",
            };
            var rows = await Task.Run(() =>
            {
                var r = notes.Select(kv => new FolderUse(kv.Key, AppEnv.WS(kv.Key), Mb(AppEnv.WS(kv.Key)), kv.Value)).ToList();
                r.Add(new FolderUse("HF cache", HfCache, Mb(HfCache), Loc.T("FoHf")));
                r.Add(new FolderUse("app python", Path.Combine(AppEnv.AppData), Mb(AppEnv.AppData), ""));
                return r.Where(x => x.Mb > 0.05).OrderByDescending(x => x.Mb).ToList();
            });
            Folders.Clear(); foreach (var f in rows) Folders.Add(f);
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AppEnv.Workspace))!);
                DriveText = string.Format(Loc.T("DriveFree"), d.Name, d.AvailableFreeSpace / 1e9, d.TotalSize / 1e9);
            }
            catch { DriveText = ""; }
            var (n, mb) = await Task.Run(() => StemsToFree());
            StemsText = string.Format(Loc.T("StemsFree"), n, mb / 1024);
            LoadBackups();
            LoadModCfg();
        }
        finally { Busy = false; }
    }

    static double Mb(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            // don't follow junctions (a test workspace may link to the real corpus)
            long sum = 0;
            var stack = new Stack<DirectoryInfo>([new DirectoryInfo(dir)]);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try
                {
                    foreach (var f in d.EnumerateFiles()) sum += f.Length;
                    foreach (var s in d.EnumerateDirectories()) if (!s.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(s);
                }
                catch { }
            }
            return sum / 1048576.0;
        }
        catch { return 0; }
    }

    /// <summary>Stems of songs that are already built: only re-analysis / re-transcription needs them again.
    /// Song 04 is kept (it is the Zoktay voice reference).</summary>
    static List<string> StemDirs()
    {
        var root = AppEnv.WS("stems", "htdemucs");
        if (!Directory.Exists(root)) return [];
        var built = SongStore.List().Where(s => s.Built).Select(s => s.Id).ToHashSet();
        return Directory.GetDirectories(root).Where(d =>
        {
            var id = Path.GetFileName(d);
            return id != "04" && (built.Contains(id) || id.EndsWith("_old"));
        }).ToList();
    }

    static (int n, double mb) StemsToFree()
    {
        var dirs = StemDirs();
        return (dirs.Count, dirs.Sum(Mb));
    }

    [RelayCommand]
    async Task CleanStems()
    {
        var dirs = StemDirs();
        if (dirs.Count == 0) return;
        if (MessageBox.Show(string.Format(Loc.T("StemsConfirm"), dirs.Count), "Onurada Mapper", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Busy = true;
        await Task.Run(() => { foreach (var d in dirs) try { Directory.Delete(d, true); } catch { } });
        Message = string.Format(Loc.T("Cleaned"), dirs.Count);
        await RefreshAsync();
    }

    [RelayCommand]
    async Task CleanDownloads()
    {
        Busy = true;
        await Task.Run(() =>
        {
            foreach (var d in new[] { Path.Combine(AppEnv.AppData, "downloads"), AppEnv.WS("thumbs") })
                if (Directory.Exists(d)) foreach (var f in Directory.GetFiles(d)) try { File.Delete(f); } catch { }
        });
        await RefreshAsync();
    }

    void LoadBackups()
    {
        Backups.Clear();
        var root = AppEnv.WS("backups");
        if (!Directory.Exists(root)) return;
        foreach (var d in new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.CreationTime))
            Backups.Add(new BackupInfo(d.Name, d.FullName, d.CreationTime, Mb(d.FullName), File.Exists(Path.Combine(d.FullName, "Info.dat"))));
    }

    [RelayCommand]
    void OpenBackup() { if (SelectedBackup != null) Process.Start(new ProcessStartInfo("explorer.exe", SelectedBackup.Path) { UseShellExecute = true }); }

    /// <summary>A level backup ("date_Folder" holding Info.dat) goes back into CustomLevels; the current copy is backed up first.</summary>
    [RelayCommand]
    async Task RestoreBackup()
    {
        var b = SelectedBackup;
        if (b == null || !b.IsLevel) return;
        var folder = b.Name.Length > 16 && b.Name[10] == '_' ? b.Name[16..] : b.Name;
        if (MessageBox.Show(string.Format(Loc.T("RestoreConfirm"), folder), "Onurada Mapper", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await JobService.I.RunAsync(Loc.T("Restore") + " " + folder, [new JobStep(Loc.T("Restore"), c =>
        {
            if (GameLocator.IsRunning()) throw new StepFailedException(Loc.T("ErrGameRunning"));
            var dst = Path.Combine(AppEnv.CustomLevelsDir, folder);
            if (Directory.Exists(dst))
            {
                Installer.CopyDir(dst, AppEnv.WS("backups", DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_" + folder));
                Directory.Delete(dst, true);
            }
            Installer.CopyDir(b.Path, dst);
            return Task.CompletedTask;
        })]);
        LoadBackups();
    }

    [RelayCommand]
    void DeleteBackup()
    {
        var b = SelectedBackup;
        if (b == null) return;
        if (MessageBox.Show(string.Format(Loc.T("DeleteBackupConfirm"), b.Name), "Onurada Mapper", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Directory.Delete(b.Path, true); } catch (Exception e) { Message = e.Message; }
        LoadBackups();
    }

    // ---------------- mod settings (BepInEx config) ----------------
    void LoadModCfg()
    {
        ModSettings.Clear();
        if (!File.Exists(ModCfg)) { ModCfgText = Loc.T("ModCfgMissing"); return; }
        ModCfgText = ModCfg;
        string section = "", desc = "", type = "", def = "";
        foreach (var raw in File.ReadAllLines(ModCfg))
        {
            var l = raw.Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) { section = l[1..^1]; continue; }
            if (l.StartsWith("## ")) { desc = l[3..]; continue; }
            if (l.StartsWith("# Setting type:")) { type = l[15..].Trim(); continue; }
            if (l.StartsWith("# Default value:")) { def = l[16..].Trim(); continue; }
            if (l.StartsWith('#') || !l.Contains('=')) continue;
            var i = l.IndexOf('=');
            ModSettings.Add(new CfgEntry { Section = section, Key = l[..i].Trim(), Value = l[(i + 1)..].Trim(), Description = desc, Type = type, Default = def });
            desc = type = def = "";
        }
    }

    [RelayCommand]
    void SaveModCfg()
    {
        if (!File.Exists(ModCfg)) return;
        var lines = File.ReadAllLines(ModCfg);
        string section = "";
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) section = l[1..^1];
            if (!l.StartsWith('#') && l.Contains('='))
            {
                var key = l[..l.IndexOf('=')].Trim();
                var e = ModSettings.FirstOrDefault(x => x.Section == section && x.Key == key);
                if (e != null) { sb.AppendLine($"{key} = {e.Value}"); continue; }
            }
            sb.AppendLine(raw);
        }
        File.Copy(ModCfg, ModCfg + ".bak", true);
        File.WriteAllText(ModCfg, sb.ToString());
        Message = Loc.T("ModCfgSaved");
    }

    [RelayCommand]
    void ResetModCfg() { foreach (var e in ModSettings) if (e.Default.Length > 0) e.Value = e.Default; }

    [RelayCommand] Task Refresh() => RefreshAsync();

    [RelayCommand]
    void OpenGameLog()
    {
        var p = Path.Combine(AppEnv.Settings.GameDir, "BepInEx", "LogOutput.log");
        if (File.Exists(p)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{p}\"") { UseShellExecute = true });
    }
}

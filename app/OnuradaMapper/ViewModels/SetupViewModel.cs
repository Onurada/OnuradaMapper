using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public partial class SetupItem(string key, Func<Task>? fix, bool always = false) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowButton))]
    private bool _ok;
    /// <summary>Install buttons disappear once done; choosing the game folder and rebuilding the library stay available.</summary>
    public bool ShowButton => fix != null && (always || !Ok);
    [ObservableProperty] private bool _checking = true;
    [ObservableProperty] private string _status = "…";
    public bool CanFix => fix != null;
    public string Title => Loc.T(Key);
    public string Desc => Loc.T(Key + "Desc");
    public string Button => Loc.T(Key + "Btn");
    public void Relabel() { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Desc)); OnPropertyChanged(nameof(Button)); }
    [RelayCommand] Task Fix() => fix?.Invoke() ?? Task.CompletedTask;
}

public partial class SetupViewModel : ObservableObject
{
    public ObservableCollection<SetupItem> Items { get; } = [];
    [ObservableProperty] private bool _allOk;
    [ObservableProperty] private string _gpuText = "";

    readonly SetupItem _game, _tools, _python, _bepinex, _mod, _corpus, _workspace;

    public SetupViewModel()
    {
        _workspace = new("SetupWorkspace", null);
        _game = new("SetupGame", BrowseGame, always: true);
        _tools = new("SetupTools", () => Run("SetupTools", [Installer.Ffmpeg(), Installer.YtDlp()]));
        _python = new("SetupPython", () => Run("SetupPython", Installer.PythonEnv()));
        _bepinex = new("SetupBepInEx", () => Run("SetupBepInEx", [Installer.BepInEx()]));
        _mod = new("SetupMod", () => Run("SetupMod", [Installer.Mod()]));
        _corpus = new("SetupCorpus", () => Run("SetupCorpus", Installer.Corpus(force: true)), always: true);
        foreach (var i in new[] { _workspace, _game, _tools, _python, _bepinex, _mod, _corpus }) Items.Add(i);
    }

    async Task Run(string key, IEnumerable<JobStep> steps)
    {
        await JobService.I.RunAsync(Loc.T(key), steps);
        await RefreshAsync();
    }

    Task BrowseGame()
    {
        var d = new OpenFolderDialog { Title = "Beat Saber" };
        if (d.ShowDialog() == true)
        {
            if (GameLocator.IsGameDir(d.FolderName)) { AppEnv.Settings.GameDir = d.FolderName; AppEnv.Save(); }
            else System.Windows.MessageBox.Show(Loc.T("ErrNoGame"), "Onurada Mapper");
        }
        return RefreshAsync();
    }

    /// <summary>Everything missing, in order: tools, Python, BepInEx, mod, corpus.</summary>
    [RelayCommand]
    async Task InstallAll()
    {
        var steps = new List<JobStep>();
        if (!_tools.Ok) steps.AddRange([Installer.Ffmpeg(), Installer.YtDlp()]);
        if (!_python.Ok) steps.AddRange(Installer.PythonEnv());
        if (_game.Ok)
        {
            if (!_bepinex.Ok) steps.Add(Installer.BepInEx());
            if (!_mod.Ok) steps.Add(Installer.Mod());
            if (!_corpus.Ok) steps.AddRange(Installer.Corpus());
        }
        if (steps.Count > 0) await Run("SetupAll", steps);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        foreach (var i in Items) i.Checking = true;
        var s = AppEnv.Settings;

        Directory.CreateDirectory(s.Workspace);
        Set(_workspace, true, s.Workspace);

        var g = GameLocator.IsGameDir(s.GameDir);
        Set(_game, g, g ? $"{s.GameDir}  (v{GameLocator.Version(s.GameDir)})" : Loc.T("NotFound"));

        var ff = AppEnv.Exe("ffmpeg"); var yt = AppEnv.Exe("yt-dlp");
        Set(_tools, ff != null && yt != null, $"ffmpeg: {(ff != null ? "OK" : Loc.T("Missing"))}   yt-dlp: {(yt != null ? "OK" : Loc.T("Missing"))}");

        _python.Status = Loc.T("Checking");
        string? py = null, pyPath = AppEnv.Python;
        py = await Installer.CheckPython(pyPath);
        if (py == null && string.IsNullOrEmpty(s.PythonPath))
            foreach (var cand in Installer.PythonCandidates())   // e.g. a Python that already has everything
                if ((py = await Installer.CheckPython(cand)) != null)
                {
                    pyPath = cand;
                    if (cand != AppEnv.VenvPython) { s.PythonPath = cand; AppEnv.Save(); }
                    break;
                }
        var cuda = py?.EndsWith("True") == true;
        Set(_python, py != null, py != null ? $"{pyPath}  ·  torch {py.Split(' ')[1]}  ·  {(cuda ? "GPU (CUDA)" : "CPU")}" : Loc.T("PythonMissing"));
        GpuText = Installer.HasNvidiaGpu() ? (cuda || py == null ? "" : Loc.T("GpuUnused")) : Loc.T("NoGpu");

        Set(_bepinex, g && GameLocator.BepInExInstalled(s.GameDir), g ? (GameLocator.BepInExInstalled(s.GameDir) ? "OK" : Loc.T("Missing")) : "—");
        var have = g ? GameLocator.DllVersion(GameLocator.ModPath(s.GameDir)) : null;
        var ours = GameLocator.DllVersion(AppEnv.ModDll);
        Set(_mod, have != null && (ours == null || have >= ours),
            (have != null ? "v" + have.ToString(3) : Loc.T("Missing")) + (ours != null && (have == null || have < ours) ? "  →  v" + ours.ToString(3) : ""));

        var n = Directory.Exists(AppEnv.WS("corpus")) ? Directory.GetDirectories(AppEnv.WS("corpus")).Length : 0;
        Set(_corpus, Installer.CorpusReady(), Installer.CorpusReady() ? string.Format(Loc.T("CorpusCount"), n) : Loc.T("Missing"));

        AllOk = Items.All(i => i.Ok);
    }

    static void Set(SetupItem i, bool ok, string status) { i.Ok = ok; i.Status = status; i.Checking = false; }
}

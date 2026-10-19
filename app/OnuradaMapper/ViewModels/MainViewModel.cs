using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public sealed partial class NavItem(string key, string glyph, ObservableObject page) : ObservableObject
{
    public string Glyph { get; } = glyph;
    public ObservableObject Page { get; } = page;
    public string Label => Loc.T(key);
    public void Relabel() => OnPropertyChanged(nameof(Label));
}

public partial class MainViewModel : ObservableObject
{
    public LibraryViewModel Library { get; } = new();
    public NewMapViewModel NewMap { get; } = new();
    public QueueViewModel Queue { get; }
    public ToolsViewModel Tools { get; } = new();
    public SetupViewModel Setup { get; } = new();
    public SettingsViewModel SettingsPage { get; } = new();
    public JobService Job => JobService.I;
    public List<NavItem> Nav { get; }

    [ObservableProperty] private NavItem _selected;
    [ObservableProperty] private bool _gameRunning;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string _updateUrl = "";
    readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainViewModel()
    {
        Queue = new QueueViewModel(NewMap.Languages);
        Nav = [new("NavLibrary", "", Library), new("NavNewMap", "", NewMap), new("NavQueue", "", Queue),
               new("NavTools", "", Tools), new("NavSetup", "", Setup), new("NavSettings", "", SettingsPage)];
        _selected = Nav[0];
        NewMap.Done += () => { Library.Refresh(); };
        NewMap.QueueRequested += r => { Queue.Add(r); Selected = Nav[2]; };
        Queue.Built += Library.Refresh;
        Library.NewRequested += () => Selected = Nav[1];
        Loc.Changed += () => { foreach (var n in Nav) n.Relabel(); foreach (var i in Setup.Items) i.Relabel(); Library.Refresh(); };
        SettingsPage.Saved += () => { Library.Refresh(); NewMap.Reload(); _ = Setup.RefreshAsync(); };
        _gameTimer.Tick += (_, _) => GameRunning = GameLocator.IsRunning();
        _gameTimer.Start();
    }

    /// <summary>First start (or something missing): open the setup page.</summary>
    public async Task StartAsync()
    {
        Library.Refresh();
        GameRunning = GameLocator.IsRunning();
        await Setup.RefreshAsync();
        if (!Setup.AllOk) Selected = Nav[4];
        _ = CheckUpdateAsync();
    }

    partial void OnSelectedChanged(NavItem value)
    {
        if (value.Page is LibraryViewModel l) l.Refresh();
        if (value.Page is NewMapViewModel n) n.Reload();
        if (value.Page is SetupViewModel s) _ = s.RefreshAsync();
        if (value.Page is ToolsViewModel t) _ = t.RefreshAsync();
    }

    public void Drop(string pathOrUrl)
    {
        NewMap.Accept(pathOrUrl);
        Selected = Nav[1];
    }

    /// <summary>Starts Beat Saber through Steam when it is a Steam copy (so SteamVR comes up too), else the exe.</summary>
    [RelayCommand]
    void LaunchGame()
    {
        var g = AppEnv.Settings.GameDir;
        if (!GameLocator.IsGameDir(g)) return;
        if (g.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("steam://rungameid/620980") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(Path.Combine(g, "Beat Saber.exe")) { WorkingDirectory = g, UseShellExecute = true });
        GameRunning = true;
    }

    [RelayCommand]
    void OpenUpdate() { if (UpdateUrl.Length > 0) Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true }); }

    async Task CheckUpdateAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OnuradaMapper");
            var j = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{AppEnv.RepoSlug}/releases/latest"));
            var tag = ((string?)j?["tag_name"] ?? "").TrimStart('v');
            var mine = typeof(MainViewModel).Assembly.GetName().Version;
            if (Version.TryParse(tag, out var v) && mine != null && v > mine)
            {
                UpdateText = string.Format(Loc.T("UpdateAvailable"), tag);
                UpdateUrl = (string?)j?["html_url"] ?? $"https://github.com/{AppEnv.RepoSlug}/releases";
            }
        }
        catch { }   // offline or no release yet
    }
}

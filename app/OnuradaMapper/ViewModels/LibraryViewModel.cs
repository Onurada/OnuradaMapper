using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnuradaMapper.Services;
using OnuradaMapper.Views;

namespace OnuradaMapper.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    List<SongInfo> _all = [];
    public ObservableCollection<SongInfo> Songs { get; } = [];
    public event Action? NewRequested;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand), nameof(InstallCommand), nameof(OpenFolderCommand), nameof(EditCommand), nameof(OpenCommand))]
    private SongInfo? _selected;
    [ObservableProperty] private string _summary = "";

    partial void OnFilterChanged(string value) => ApplyFilter();

    public void Refresh()
    {
        var keep = Selected?.Id;
        try { _all = SongStore.List(); }
        catch (Exception e) { _all = []; Summary = "songs.json: " + e.Message; return; }
        ApplyFilter();
        Selected = Songs.FirstOrDefault(s => s.Id == keep);
        Summary = string.Format(Loc.T("LibSummary"), _all.Count, _all.Count(s => s.Built), _all.Count(s => s.Installed));
    }

    void ApplyFilter()
    {
        Songs.Clear();
        var f = Filter.Trim();
        foreach (var s in _all.Where(s => f.Length == 0 || s.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                                                        || s.Artist.Contains(f, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(f)))
            Songs.Add(s);
    }

    bool HasSel() => Selected != null && !JobService.I.IsRunning;

    [RelayCommand] void New() => NewRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(HasSel))]
    async Task Rebuild()
    {
        var s = Selected!;
        var steps = new List<JobStep> { MapPipeline.Build(s.Id) };
        if (AppEnv.Settings.InstallAfterBuild) steps.Add(Installer.InstallLevel(s.Folder));
        await JobService.I.RunAsync(Loc.T("JobRebuild") + " " + s.Title, steps);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSel))]
    async Task Install()
    {
        var s = Selected!;
        await JobService.I.RunAsync(Loc.T("JobInstall") + " " + s.Title, [Installer.InstallLevel(s.Folder)]);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSel))]
    void OpenFolder()
    {
        var d = Directory.Exists(Selected!.OutDir) ? Selected.OutDir : AppEnv.Workspace;
        Process.Start(new ProcessStartInfo("explorer.exe", d) { UseShellExecute = true });
    }

    /// <summary>Opens the song window (timeline, preview, lyrics, tuning).</summary>
    [RelayCommand(CanExecute = nameof(HasSel))]
    void Open()
    {
        var w = new SongWindow(Selected!.Id) { Owner = Application.Current.MainWindow };
        w.Changed += Refresh;
        w.Show();
    }

    [RelayCommand(CanExecute = nameof(HasSel))]
    void Edit()
    {
        var s = Selected!;
        var all = SongStore.Load();
        var w = new JsonEditWindow(s.Title, SongStore.Pretty(all[s.Id]!)) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() == true && w.Result != null)
        {
            all = SongStore.Load();
            all[s.Id] = w.Result;
            SongStore.Save(all);
            Refresh();
        }
    }

    [RelayCommand] void Refreshed() => Refresh();

    /// <summary>After a pipeline update: build every song again (and copy it to the game if that is switched on).</summary>
    [RelayCommand]
    async Task RebuildAll()
    {
        var built = _all.Where(s => s.Built).ToList();
        if (built.Count == 0) return;
        if (MessageBox.Show(string.Format(Loc.T("LibRebuildAllConfirm"), built.Count), "Onurada Mapper", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var steps = new List<JobStep>();
        foreach (var s in built)
        {
            var b = MapPipeline.Build(s.Id);
            steps.Add(new JobStep($"#{s.Id} {s.Title}", b.Run));
            if (AppEnv.Settings.InstallAfterBuild) steps.Add(new JobStep($"#{s.Id} → {Loc.T("StepInstall")}", Installer.InstallLevel(s.Folder).Run));
        }
        await JobService.I.RunAsync(Loc.T("LibRebuildAll"), steps);
        Refresh();
    }
}

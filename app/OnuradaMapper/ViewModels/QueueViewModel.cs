using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public partial class QueueItem : ObservableObject
{
    public MapRequest Req { get; set; } = new();
    public string Title { get => Req.Title; set { Req.Title = value; OnPropertyChanged(); } }
    public string Artist { get => Req.Artist; set { Req.Artist = value; OnPropertyChanged(); } }
    public string Source => Req.FromYouTube ? Req.Url : Path.GetFileName(Req.AudioFile);
    [ObservableProperty] private StepState _state;
    [ObservableProperty] private string _detail = "";
}

/// <summary>Many songs in a row: paste links or a playlist, start it, go to sleep.</summary>
public partial class QueueViewModel : ObservableObject
{
    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnuradaMapper", "queue.json");
    public ObservableCollection<QueueItem> Items { get; } = [];
    public event Action? Built;

    [ObservableProperty] private string _links = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private QueueItem? _selected;
    // shared options for links added here
    [ObservableProperty] private Choice? _language;
    [ObservableProperty] private int _dream;      // 0 auto, 2 off
    [ObservableProperty] private bool _zoktay;
    [ObservableProperty] private int _karaoke;
    [ObservableProperty] private bool _install = true;
    public List<Choice> Languages { get; }
    bool _stop;

    public QueueViewModel(List<Choice> languages)
    {
        Languages = languages;
        _language = languages[0];
        Load();
        Items.CollectionChanged += (_, _) => Save();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var r in JsonSerializer.Deserialize<List<QueueEntry>>(File.ReadAllText(File_), Headless.Opts) ?? [])
                Items.Add(new QueueItem { Req = r.Req, State = r.State == StepState.Running ? StepState.Pending : r.State, Detail = r.Detail });
        }
        catch { }
    }

    sealed record QueueEntry(MapRequest Req, StepState State, string Detail);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(Items.Select(i => new QueueEntry(i.Req, i.State, i.Detail)).ToList(), Headless.Opts));
        }
        catch { }
    }

    MapRequest Template() => new()
    {
        Language = Language?.Value ?? "auto",
        Dream = Dream == 2 ? DreamMode.Off : DreamMode.Auto,
        Video = Zoktay && Dream != 2 ? VideoMode.Zoktay : VideoMode.None,
        Karaoke = (KaraokeMode)Karaoke,
        Install = Install,
    };

    public void Add(MapRequest r)
    {
        Items.Add(new QueueItem { Req = r });
        Message = string.Format(Loc.T("QAdded"), 1);
    }

    [RelayCommand]
    async Task AddLinks()
    {
        var lines = Links.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("http")).ToList();
        if (lines.Count == 0) { Message = Loc.T("ErrUrl"); return; }
        Busy = true; Message = Loc.T("QReading");
        var added = 0;
        try
        {
            foreach (var l in lines)
            {
                List<VideoHit> hits;
                try { hits = await YouTube.Expand(l); } catch (Exception e) { Message = e.Message; continue; }
                foreach (var h in hits)
                {
                    if (Items.Any(i => i.Req.Url == h.Url)) continue;
                    var (t, a) = YouTube.Guess(h.Title, h.Channel);
                    var r = Template(); r.FromYouTube = true; r.Url = h.Url; r.Title = t; r.Artist = a;
                    Items.Add(new QueueItem { Req = r }); added++;
                }
            }
            Links = "";
            Message = string.Format(Loc.T("QAdded"), added);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    void Remove() { if (Selected != null && Selected.State != StepState.Running) Items.Remove(Selected); }

    [RelayCommand]
    void ClearDone() { foreach (var i in Items.Where(i => i.State is StepState.Done).ToList()) Items.Remove(i); }

    [RelayCommand]
    void Retry() { foreach (var i in Items.Where(i => i.State == StepState.Failed)) { i.State = StepState.Pending; i.Detail = ""; } Save(); }

    [RelayCommand]
    void MoveUp()
    {
        if (Selected == null) return;
        var i = Items.IndexOf(Selected);
        if (i > 0) Items.Move(i, i - 1);
    }

    [RelayCommand]
    void Stop() { _stop = true; Message = Loc.T("QStopping"); }

    [RelayCommand]
    async Task Start()
    {
        if (Running) return;
        if (string.IsNullOrEmpty(AppEnv.Python)) { Message = Loc.T("ErrNoPython"); return; }
        if (!Installer.CorpusReady()) { Message = Loc.T("ErrNoCorpus"); return; }
        Running = true; _stop = false;
        int ok = 0, bad = 0;
        try
        {
            while (!_stop)
            {
                var item = Items.FirstOrDefault(i => i.State == StepState.Pending);
                if (item == null) break;
                item.State = StepState.Running;
                if (string.IsNullOrEmpty(item.Req.Id)) item.Req.Id = SongStore.NextId();   // a retry keeps its number and resumes
                item.Detail = "#" + item.Req.Id;
                Save();
                var steps = MapPipeline.Steps(item.Req);
                var success = await JobService.I.RunAsync($"[{Items.IndexOf(item) + 1}/{Items.Count}]  #{item.Req.Id}  {item.Req.Artist} - {item.Req.Title}", steps);
                item.State = success ? StepState.Done : StepState.Failed;
                item.Detail = "#" + item.Req.Id + "  " + (success ? JobService.I.Status : steps.FirstOrDefault(s => s.State == StepState.Failed)?.Detail ?? JobService.I.Status);
                if (success) ok++; else bad++;
                Save();
                Built?.Invoke();
                if (JobService.I.Status == Loc.T("Cancelled")) break;
            }
        }
        finally
        {
            Running = false;
            Message = string.Format(Loc.T("QDone"), ok, bad);
            Notify.Done(Message, bad == 0);
        }
    }
}

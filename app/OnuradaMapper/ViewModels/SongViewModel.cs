using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public sealed record DiffStat(string Name, int Notes, double Nps, double Njs, int Arcs, int Chains, int Bombs, int Walls, string Sources);

public partial class WordRow : ObservableObject
{
    [ObservableProperty] private double _start;
    [ObservableProperty] private string _word = "";
    public double End { get; set; }
    public double P { get; set; } = 1;
    public JsonObject? Raw { get; set; }
    public bool Edited { get; private set; }
    partial void OnStartChanged(double value) => Edited = true;
    partial void OnWordChanged(string value) => Edited = true;
    public void MarkClean() => Edited = false;
}

/// <summary>One song: overview and tuning, the story timeline, lyrics and the map preview.</summary>
public partial class SongViewModel : ObservableObject, IDisposable
{
    public string Id { get; }
    public AudioPlayer Player { get; } = new();
    public JobService Job => JobService.I;
    JsonObject _cfg = [];
    public StoryModel Story { get; private set; } = new();
    public float[] Peaks { get; private set; } = [];
    public Analysis? Grid { get; private set; }
    public BuiltLevel? Level { get; private set; }
    public double Pad { get; private set; }
    public event Action? Reloaded;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private BitmapImage? _cover;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string _message = "";
    public ObservableCollection<DiffStat> Stats { get; } = [];
    public ObservableCollection<WordRow> Words { get; } = [];
    [ObservableProperty] private WordRow? _selectedWord;
    [ObservableProperty] private bool _wordsDirty;

    // tuning
    [ObservableProperty] private Choice? _env;
    public List<Choice> Envs { get; } = SongStore.Environments().Select(e => new Choice(e, e.Replace("Environment", "") + (SongStore.SixColour.Contains(e) ? "  ★ 6" : ""))).ToList();
    [ObservableProperty] private string _colorA = "";
    [ObservableProperty] private string _colorB = "";
    [ObservableProperty] private double _density = 1.0;
    [ObservableProperty] private double _vocalWeight = 0.85;
    [ObservableProperty] private bool _hearts = true;
    [ObservableProperty] private int _maxHearts = 7;
    [ObservableProperty] private double _lyricsOffset;
    [ObservableProperty] private string _njsE = "", _njsN = "", _njsH = "", _njsX = "", _njsXP = "";
    [ObservableProperty] private int _seed;
    [ObservableProperty] private bool _dust = true;
    [ObservableProperty] private bool _combo = true;

    // preview
    public List<string> DiffNames => Level?.Diffs.Select(d => d.name).ToList() ?? [];
    [ObservableProperty] private int _previewDiff;
    public event Action? PreviewChanged;
    partial void OnPreviewDiffChanged(int value) => PreviewChanged?.Invoke();

    public SongViewModel(string id)
    {
        Id = id;
        Load();
        Player.Open(AppEnv.WS("audio", id + ".wav"));
        Task.Run(() =>
        {
            try { Peaks = Waveform.Load(AppEnv.WS("audio", id + ".wav")); } catch { Peaks = []; }
            Application.Current.Dispatcher.Invoke(() => Reloaded?.Invoke());
        });
    }

    void Load()
    {
        _cfg = SongStore.Load()[Id] as JsonObject ?? [];
        Title = (string?)_cfg["title"] ?? Id; Artist = (string?)_cfg["artist"] ?? ""; Folder = (string?)_cfg["folder"] ?? Id;
        Grid = Analysis.Load(Id);
        Story = StoryModel.From(_cfg, Grid?.Dur ?? 200);
        Story.Changed += () => Dirty = true;
        Level = BuiltLevel.Load(AppEnv.WS("out", Folder));
        OnPropertyChanged(nameof(DiffNames));
        PreviewDiff = Math.Max(0, (Level?.Diffs.Count ?? 1) - 2);
        var cov = File.Exists(AppEnv.WS("out", Folder, "cover.jpg")) ? AppEnv.WS("out", Folder, "cover.jpg") : AppEnv.WS("covers", Id + ".jpg");
        Cover = LoadImage(cov);
        // tuning
        Env = Envs.FirstOrDefault(c => c.Value == (string?)_cfg["env"]) ?? new Choice((string?)_cfg["env"] ?? "", (string?)_cfg["env"] ?? "");
        ColorA = (string?)_cfg["colors"]?["a"] ?? "FF4FA3"; ColorB = (string?)_cfg["colors"]?["b"] ?? "4FC3FF";
        Density = (double?)_cfg["density"] ?? 1.0; VocalWeight = (double?)_cfg["vocal_weight"] ?? 0.85;
        Hearts = (bool?)_cfg["hearts"] ?? true; MaxHearts = (int?)_cfg["max_hearts"] ?? 7;
        LyricsOffset = (double?)_cfg["lyrics"]?["offset"] ?? 0;
        string N(string d) => _cfg["njs"]?[d]?.ToString() ?? "";
        NjsE = N("Easy"); NjsN = N("Normal"); NjsH = N("Hard"); NjsX = N("Expert"); NjsXP = N("ExpertPlus");
        Seed = (int?)_cfg["seed"] ?? 0;
        Dust = _cfg["dust"] != null; Combo = (bool?)_cfg["combo"] ?? false;
        // report
        Stats.Clear();
        var rep = AppEnv.WS("out", "report_" + Id + ".json");
        if (File.Exists(rep))
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(rep))!;
                Pad = (double?)j["pad"] ?? 0;
                foreach (var (name, d) in j["diffs"]!.AsObject())
                    Stats.Add(new DiffStat(name, (int?)d?["notes"] ?? 0, (double?)d?["nps"] ?? 0, (double?)d?["njs"] ?? 0, (int?)d?["arcs"] ?? 0,
                        (int?)d?["chains"] ?? 0, (int?)d?["bombs"] ?? 0, (int?)d?["walls"] ?? 0,
                        string.Join(", ", d?["top_sources"]?.AsArray().Take(4).Select(s => $"{s?[0]} ({s?[1]})") ?? [])));
                Info = $"{(double?)j["bpm"]:0.#} BPM · {TimeSpan.FromSeconds(Grid?.Dur ?? 0):m\\:ss} · {(string?)_cfg["env"]}";
            }
            catch { }
        LoadWords();
        Dirty = false;
        Reloaded?.Invoke();
    }

    static BitmapImage? LoadImage(string p)
    {
        if (!File.Exists(p)) return null;
        var b = new BitmapImage();
        b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(p); b.EndInit(); b.Freeze();
        return b;
    }

    string WordsFile => AppEnv.WS("words", ((string?)_cfg["words_file"] ?? Id) + ".json");

    void LoadWords()
    {
        Words.Clear();
        if (!File.Exists(WordsFile)) return;
        try
        {
            foreach (var w in JsonNode.Parse(File.ReadAllText(WordsFile))?["words"]?.AsArray() ?? [])
                if (w is JsonObject o)
                {
                    var r = new WordRow { Start = (double?)o["start"] ?? 0, Word = (string?)o["word"] ?? "", End = (double?)o["end"] ?? 0, P = (double?)o["p"] ?? 1, Raw = o };
                    r.MarkClean();
                    r.PropertyChanged += (_, _) => WordsDirty = true;
                    Words.Add(r);
                }
        }
        catch { }
        WordsDirty = false;
    }

    public List<(double t, string w)> WordMarks => Words.Select(w => (w.Start, w.Word)).ToList();

    // ---------------- saving ----------------
    void ApplyTuning()
    {
        if (Env != null && !string.IsNullOrEmpty(Env.Value)) _cfg["env"] = Env.Value;
        var colors = _cfg["colors"] as JsonObject ?? [];
        if (Hex(ColorA)) colors["a"] = ColorA.Trim().TrimStart('#').ToUpperInvariant();
        if (Hex(ColorB)) colors["b"] = ColorB.Trim().TrimStart('#').ToUpperInvariant();
        _cfg["colors"] = colors;
        Set("density", Math.Abs(Density - 1) < 0.01 ? null : Math.Round(Density, 2));
        Set("vocal_weight", Math.Abs(VocalWeight - 0.85) < 0.01 ? null : Math.Round(VocalWeight, 2));
        Set("hearts", Hearts ? null : false);
        _cfg["max_hearts"] = MaxHearts;
        if (_cfg["lyrics"] is JsonObject ly) { if (Math.Abs(LyricsOffset) < 0.001) ly.Remove("offset"); else ly["offset"] = Math.Round(LyricsOffset, 2); }
        var njs = new JsonObject();
        foreach (var (k, v) in new[] { ("Easy", NjsE), ("Normal", NjsN), ("Hard", NjsH), ("Expert", NjsX), ("ExpertPlus", NjsXP) })
            if (double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) njs[k] = d;
        Set("njs", njs.Count > 0 ? njs : null);
        Set("seed", Seed == 0 ? null : Seed);
        if (Dust && _cfg["dust"] == null) _cfg["dust"] = new JsonObject { ["cut"] = true, ["text"] = true };
        if (!Dust) _cfg.Remove("dust");
        _cfg["combo"] = Combo;
    }

    void Set(string key, JsonNode? v) { if (v == null) _cfg.Remove(key); else _cfg[key] = v; }
    static bool Hex(string s) => System.Text.RegularExpressions.Regex.IsMatch(s.Trim().TrimStart('#'), "^[0-9a-fA-F]{6}$");

    public void Save()
    {
        ApplyTuning();
        Story.ApplyTo(_cfg);
        var all = SongStore.Load();
        all[Id] = _cfg.DeepClone();
        SongStore.Save(all);
        if (WordsDirty) SaveWords();
        Dirty = false;
        Message = Loc.T("SongSaved");
    }

    void SaveWords()
    {
        if (!File.Exists(WordsFile)) return;
        var bak = WordsFile + ".bak";
        if (!File.Exists(bak)) File.Copy(WordsFile, bak);
        var j = JsonNode.Parse(File.ReadAllText(WordsFile)) as JsonObject ?? [];
        var arr = new JsonArray();
        foreach (var w in Words.OrderBy(w => w.Start))
        {
            var o = w.Raw?.DeepClone() as JsonObject ?? [];
            var origStart = (double?)w.Raw?["start"] ?? w.Start;
            var len = Math.Max(0.05, w.End - origStart);
            o["start"] = Math.Round(w.Start, 3); o["end"] = Math.Round(w.Start + len, 3); o["word"] = w.Word;
            o["p"] = w.Edited || w.Raw == null ? 1.0 : w.P;   // a word you fixed by hand is always shown
            arr.Add(o);
        }
        j["words"] = arr;
        File.WriteAllText(WordsFile, j.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        WordsDirty = false;
    }

    async Task RunAndReload(string title, IEnumerable<JobStep> steps)
    {
        Player.Stop();
        await JobService.I.RunAsync(title, steps);
        Load();
    }

    List<JobStep> BuildSteps()
    {
        var s = new List<JobStep> { MapPipeline.Build(Id) };
        if (AppEnv.Settings.InstallAfterBuild) s.Add(Installer.InstallLevel(Folder));
        return s;
    }

    [RelayCommand] void SaveOnly() => Save();

    [RelayCommand]
    async Task SaveAndBuild()
    {
        Save();
        await RunAndReload(Loc.T("JobRebuild") + " " + Title, BuildSteps());
    }

    /// <summary>Same song, different note choices: a new seed for the beam search and the lightshow.</summary>
    [RelayCommand]
    async Task Reroll()
    {
        Seed = Random.Shared.Next(1, 99999);
        Save();
        await RunAndReload($"{Loc.T("SongReroll")} {Title} (seed {Seed})", BuildSteps());
    }

    [RelayCommand]
    async Task OriginalSeed()
    {
        Seed = 0;
        Save();
        await RunAndReload(Loc.T("JobRebuild") + " " + Title, BuildSteps());
    }

    /// <summary>Runs autostory again (choruses, drops, dream, lights from scratch) but keeps title, colours, lyrics settings.</summary>
    [RelayCommand]
    async Task Restory()
    {
        if (MessageBox.Show(Loc.T("SongRestoryConfirm"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Save();
        var env = (string?)_cfg["env"] ?? "BTSEnvironment";
        var hasVideo = _cfg["video"]?["file"]?.ToString();
        var dream = File.Exists(AppEnv.WS("voice", Id + ".json")) ? "voice+auto" : "auto";
        var steps = new List<JobStep>
        {
            new(Loc.T("StepStory"), c => c.Py(c.Tool("autostory.py"), AppEnv.Workspace, Id, (string?)_cfg["title"] ?? Title, (string?)_cfg["artist"] ?? Artist, env,
                "--apply", "--keep", "--mapper", (string?)_cfg["mapper"] ?? AppEnv.Settings.MapperName, "--dream", dream, "--video", hasVideo ?? "none")),
        };
        steps.AddRange(BuildSteps());
        await RunAndReload(Loc.T("SongRestory") + " " + Title, steps);
    }

    [RelayCommand]
    async Task Install() => await RunAndReload(Loc.T("JobInstall") + " " + Title, [Installer.InstallLevel(Folder)]);

    [RelayCommand]
    void OpenFolder()
    {
        var d = AppEnv.WS("out", Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", Directory.Exists(d) ? d : AppEnv.Workspace) { UseShellExecute = true });
    }

    /// <summary>A zip of the level folder, as BeatSaver and other players expect it.</summary>
    [RelayCommand]
    void ExportZip()
    {
        var src = AppEnv.WS("out", Folder);
        if (!File.Exists(Path.Combine(src, "Info.dat"))) { Message = Loc.T("ErrNotBuilt"); return; }
        var d = new SaveFileDialog { FileName = Folder + ".zip", Filter = "Zip|*.zip" };
        if (d.ShowDialog() != true) return;
        if (File.Exists(d.FileName)) File.Delete(d.FileName);
        ZipFile.CreateFromDirectory(src, d.FileName, CompressionLevel.Optimal, false);
        Message = Loc.T("Exported") + " " + d.FileName;
    }

    /// <summary>New cover from a picture (cropped square) or a generated one; written to covers/ and the built level.</summary>
    [RelayCommand]
    async Task ChangeCover(string mode)
    {
        var cover = AppEnv.WS("covers", Id + ".jpg");
        Directory.CreateDirectory(AppEnv.WS("covers"));
        try
        {
            if (mode == "file")
            {
                var d = new OpenFileDialog { Filter = "Image|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
                if (d.ShowDialog() != true) return;
                var ff = AppEnv.Exe("ffmpeg") ?? throw new InvalidOperationException("ffmpeg");
                var (code, o) = await ProcessRunner.CaptureAsync(ff, ["-y", "-loglevel", "error", "-i", d.FileName, "-vf", "crop='min(iw,ih)':'min(iw,ih)',scale=512:512", "-q:v", "3", cover]);
                if (code != 0) throw new InvalidOperationException(o);
            }
            else
            {
                var (code, o) = await ProcessRunner.CaptureAsync(AppEnv.Python, [Path.Combine(AppEnv.ToolsDir, "makecover.py"), cover, Title, Artist,
                    ColorA.TrimStart('#'), ColorB.TrimStart('#'), (string?)_cfg["mapper"] ?? AppEnv.Settings.MapperName]);
                if (code != 0) throw new InvalidOperationException(o);
            }
            var built = AppEnv.WS("out", Folder);
            if (Directory.Exists(built)) File.Copy(cover, Path.Combine(built, "cover.jpg"), true);
            Cover = LoadImage(File.Exists(Path.Combine(built, "cover.jpg")) ? Path.Combine(built, "cover.jpg") : cover);
            Message = Loc.T("CoverChanged");
        }
        catch (Exception e) { Message = e.Message; }
    }

    [RelayCommand]
    void EditJson()
    {
        ApplyTuning(); Story.ApplyTo(_cfg);
        var w = new Views.JsonEditWindow(Title, SongStore.Pretty(_cfg)) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) };
        if (w.ShowDialog() == true && w.Result != null)
        {
            var all = SongStore.Load(); all[Id] = w.Result; SongStore.Save(all);
            Load();
        }
    }

    public event Action? Deleted;

    [RelayCommand]
    void Delete()
    {
        if (MessageBox.Show(string.Format(Loc.T("SongDeleteConfirm"), Title), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (GameLocator.IsRunning() && Directory.Exists(Path.Combine(AppEnv.CustomLevelsDir, Folder))) { Message = Loc.T("ErrGameRunning"); return; }
        Player.Stop();
        var bak = AppEnv.WS("backups", DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_deleted_" + Folder);
        Directory.CreateDirectory(bak);
        File.WriteAllText(Path.Combine(bak, "songs_entry.json"), SongStore.Pretty(_cfg));
        foreach (var dir in new[] { AppEnv.WS("out", Folder), Path.Combine(AppEnv.CustomLevelsDir, Folder) })
            if (Directory.Exists(dir))
            {
                Installer.CopyDir(dir, Path.Combine(bak, dir.StartsWith(AppEnv.Workspace, StringComparison.OrdinalIgnoreCase) ? "out" : "game"));
                Directory.Delete(dir, true);
            }
        var all = SongStore.Load(); all.Remove(Id); SongStore.Save(all);
        Deleted?.Invoke();
    }

    // ---------------- lyrics ----------------
    [RelayCommand]
    void ShiftWords(string amount)
    {
        var d = double.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        var sel = SelectedWord != null && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
            ? Words.SkipWhile(w => w != SelectedWord).ToList() : SelectedWord != null ? [SelectedWord] : Words.ToList();
        foreach (var w in sel) w.Start = Math.Max(0, Math.Round(w.Start + d, 3));
    }

    [RelayCommand]
    void ShiftAll(string amount)
    {
        var d = double.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var w in Words) w.Start = Math.Max(0, Math.Round(w.Start + d, 3));
    }

    [RelayCommand]
    void PlayWord()
    {
        if (SelectedWord == null) return;
        Player.Seek(SelectedWord.Start - 0.5);
        if (!Player.IsPlaying) Player.Toggle();
    }

    [RelayCommand]
    void SetWordToPlayhead()
    {
        if (SelectedWord != null) SelectedWord.Start = Math.Round(Player.Position, 3);
    }

    [RelayCommand]
    void DeleteWord() { if (SelectedWord != null) { Words.Remove(SelectedWord); WordsDirty = true; } }

    [RelayCommand]
    void AddWord()
    {
        var r = new WordRow { Start = Math.Round(Player.Position, 3), Word = "...", End = Player.Position + 0.3, P = 1 };
        r.PropertyChanged += (_, _) => WordsDirty = true;
        var i = Words.TakeWhile(w => w.Start < r.Start).Count();
        Words.Insert(i, r); SelectedWord = r; WordsDirty = true;
    }

    [RelayCommand]
    void RestoreWords()
    {
        var bak = WordsFile + ".bak";
        if (!File.Exists(bak)) return;
        File.Copy(bak, WordsFile, true);
        LoadWords();
    }

    public void Dispose() => Player.Dispose();
}

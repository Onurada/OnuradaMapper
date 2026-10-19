using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

public partial class NewMapViewModel : ObservableObject
{
    public event Action? Done;

    [ObservableProperty] private bool _fromYouTube = true;
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _audioFile = "";
    [ObservableProperty] private string _coverFile = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _bpm = "";
    [ObservableProperty] private Choice? _language;
    [ObservableProperty] private Choice? _env;
    [ObservableProperty] private bool _autoColors = true;
    [ObservableProperty] private string _colorA = "FF4FA3";
    [ObservableProperty] private string _colorB = "4FC3FF";
    [ObservableProperty] private int _karaoke;     // KaraokeMode
    [ObservableProperty] private int _dream;       // DreamMode
    [ObservableProperty] private Choice? _voiceRef;
    [ObservableProperty] private string _voiceStart = "";
    [ObservableProperty] private string _voiceEnd = "";
    [ObservableProperty] private int _video;       // VideoMode
    [ObservableProperty] private string _videoFile = "";
    [ObservableProperty] private string _videoStart = "0";
    [ObservableProperty] private bool _install = true;
    [ObservableProperty] private string _nextId = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _busy;

    public List<Choice> Languages { get; } =
    [
        new("auto", "Auto"), new("tr", "Türkçe"), new("en", "English"), new("es", "Español"), new("de", "Deutsch"), new("fr", "Français"),
        new("it", "Italiano"), new("pt", "Português"), new("ru", "Русский"), new("ja", "日本語"), new("ko", "한국어"), new("ar", "العربية"),
    ];
    [ObservableProperty] private List<Choice> _envs = [];
    [ObservableProperty] private List<Choice> _voiceRefs = [];
    [ObservableProperty] private bool _zoktayAvailable;

    public NewMapViewModel() { _language = Languages[0]; }

    // ---------------- YouTube search ----------------
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private List<VideoHit> _hits = [];
    [ObservableProperty] private bool _searching;

    [RelayCommand]
    async Task DoSearch()
    {
        if (string.IsNullOrWhiteSpace(Search)) return;
        Searching = true; Error = "";
        try { Hits = await YouTube.Search(Search.Trim(), 10); if (Hits.Count == 0) Error = Loc.T("ErrFetch"); }
        catch (Exception e) { Error = e.Message; }
        finally { Searching = false; }
    }

    [RelayCommand]
    void UseHit(VideoHit? h)
    {
        if (h == null) return;
        FromYouTube = true; Url = h.Url;
        (Title, Artist) = YouTube.Guess(h.Title, h.Channel);
        Hits = [];
    }

    /// <summary>A file or a link dropped on the window.</summary>
    public void Accept(string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http")) { FromYouTube = true; Url = pathOrUrl.Trim(); _ = FetchInfo(); }
        else { FromYouTube = false; AudioFile = pathOrUrl; if (string.IsNullOrWhiteSpace(Title)) Title = Path.GetFileNameWithoutExtension(pathOrUrl); }
    }

    public void Reload()
    {
        try { NextId = SongStore.NextId(); } catch { NextId = "?"; }
        var envs = new List<Choice> { new("auto", Loc.T("EnvAuto")) };
        envs.AddRange(SongStore.Environments().Select(e => new Choice(e, e.Replace("Environment", "") + (SongStore.SixColour.Contains(e) ? "  ★ 6" : ""))));
        Envs = envs;
        Env ??= envs[0];
        if (!envs.Contains(Env)) Env = envs[0];
        var refs = new List<Choice> { new("", Loc.T("VoiceThisSong")) };
        try
        {
            refs.AddRange(SongStore.List().Where(s => File.Exists(AppEnv.WS("stems", "htdemucs", s.Id, "vocals.wav")))
                                  .Select(s => new Choice(s.Id, $"{s.Id}  {s.Title}")));
        }
        catch { }
        VoiceRefs = refs;
        VoiceRef ??= refs[0];
        ZoktayAvailable = File.Exists(AppEnv.WS("vid", "zoktay.bc3"));
    }

    /// <summary>Zoktay preset: song 04 100-117 s is his voice, 49.5-79 s is not (the values the batch scripts used).</summary>
    [RelayCommand]
    void ZoktayPreset()
    {
        Dream = (int)DreamMode.Voice;
        VoiceRef = VoiceRefs.FirstOrDefault(c => c.Value == "04") ?? VoiceRef;
        VoiceStart = "100"; VoiceEnd = "117";
        if (ZoktayAvailable) Video = (int)VideoMode.Zoktay;
    }

    [RelayCommand]
    async Task FetchInfo()
    {
        var yt = AppEnv.Exe("yt-dlp");
        if (yt == null || string.IsNullOrWhiteSpace(Url)) return;
        Busy = true; Error = "";
        try
        {
            var (code, outp) = await ProcessRunner.CaptureAsync(yt, ["--no-warnings", "--no-playlist", "--skip-download", "--print", "%(track,title)s\t%(artist,creator,uploader)s", Url.Trim()]);
            var line = outp.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('\t'));
            if (code != 0 || line == null) { Error = Loc.T("ErrFetch"); return; }
            var parts = line.Split('\t');
            (Title, Artist) = YouTube.Guess(parts[0], parts[1] == "NA" ? "" : parts[1].Replace(" - Topic", ""));
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    void BrowseAudio()
    {
        var d = new OpenFileDialog { Filter = "Audio/Video|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.opus;*.webm;*.mp4;*.mkv|*.*|*.*" };
        if (d.ShowDialog() == true)
        {
            AudioFile = d.FileName;
            if (string.IsNullOrWhiteSpace(Title)) Title = Path.GetFileNameWithoutExtension(d.FileName);
        }
    }

    [RelayCommand]
    void BrowseCover()
    {
        var d = new OpenFileDialog { Filter = "Image|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
        if (d.ShowDialog() == true) CoverFile = d.FileName;
    }

    [RelayCommand]
    void BrowseVideo()
    {
        var d = new OpenFileDialog { Filter = "Video|*.mp4;*.mkv;*.webm;*.mov;*.gif;*.avi" };
        if (d.ShowDialog() == true) { VideoFile = d.FileName; Video = (int)VideoMode.File; }
    }

    static double? Num(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>"1:40" or "100" → seconds.</summary>
    static double? Time(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, @"^(\d+):(\d{1,2}(?:[.,]\d+)?)$");
        return m.Success ? int.Parse(m.Groups[1].Value) * 60 + Num(m.Groups[2].Value) : Num(s);
    }

    static bool Hex(string s) => Regex.IsMatch(s.Trim().TrimStart('#'), "^[0-9a-fA-F]{6}$");

    /// <summary>Validates the form; null (and Error set) when something is missing.</summary>
    MapRequest? BuildRequest(bool forQueue)
    {
        Error = "";
        string? err = null;
        if (FromYouTube && !Regex.IsMatch(Url.Trim(), @"^https?://")) err = Loc.T("ErrUrl");
        else if (!FromYouTube && !File.Exists(AudioFile)) err = Loc.T("ErrAudioFile");
        else if (string.IsNullOrWhiteSpace(Title)) err = Loc.T("ErrTitle");
        else if (!AutoColors && (!Hex(ColorA) || !Hex(ColorB))) err = Loc.T("ErrColor");
        else if (Dream == (int)DreamMode.Voice && (Time(VoiceStart) is not double vs || Time(VoiceEnd) is not double ve || ve - vs < 3)) err = Loc.T("ErrVoiceRange");
        else if (Video == (int)VideoMode.File && !File.Exists(VideoFile)) err = Loc.T("ErrVideoFile");
        else if (string.IsNullOrEmpty(AppEnv.Python)) err = Loc.T("ErrNoPython");
        else if (!Installer.CorpusReady()) err = Loc.T("ErrNoCorpus");
        if (err != null) { Error = err; return null; }

        Reload();
        var r = new MapRequest
        {
            Id = forQueue ? "" : NextId, FromYouTube = FromYouTube, Url = Url.Trim(), AudioFile = AudioFile, CoverFile = CoverFile,
            Title = Title.Trim(), Artist = Artist.Trim(), Language = Language?.Value ?? "auto", Env = Env?.Value ?? "auto",
            Bpm = Num(Bpm), ColorA = AutoColors ? null : ColorA.Trim().TrimStart('#').ToUpperInvariant(),
            ColorB = AutoColors ? null : ColorB.Trim().TrimStart('#').ToUpperInvariant(),
            Karaoke = (KaraokeMode)Karaoke, Dream = (DreamMode)Dream, VoiceRef = VoiceRef?.Value ?? "",
            VoiceStart = Time(VoiceStart) ?? 0, VoiceEnd = Time(VoiceEnd) ?? 0,
            Video = (VideoMode)Video, VideoFile = VideoFile, VideoStart = Time(VideoStart) ?? 0, Install = Install,
        };
        if (r.Dream == DreamMode.Voice && r.VoiceRef == "04" && r.VoiceStart == 100 && r.VoiceEnd == 117) { r.VoiceNegStart = 49.5; r.VoiceNegEnd = 79; }
        if (r.Dream == DreamMode.Off) r.Video = VideoMode.None;
        return r;
    }

    void ClearForm() { Url = ""; AudioFile = ""; CoverFile = ""; Title = ""; Artist = ""; Bpm = ""; VideoFile = ""; Reload(); }

    public event Action<MapRequest>? QueueRequested;

    [RelayCommand]
    void AddToQueue()
    {
        var r = BuildRequest(true);
        if (r == null) return;
        QueueRequested?.Invoke(r);
        ClearForm();
    }

    [RelayCommand]
    async Task Create()
    {
        var r = BuildRequest(false);
        if (r == null) return;
        var ok = await JobService.I.RunAsync($"#{r.Id}  {r.Artist} - {r.Title}", MapPipeline.Steps(r));
        Done?.Invoke();
        if (ok) ClearForm();
    }
}

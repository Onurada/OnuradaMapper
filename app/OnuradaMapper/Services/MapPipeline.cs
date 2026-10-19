using System.IO;
using System.Text.Json.Nodes;

namespace OnuradaMapper.Services;

public enum DreamMode { Auto, Voice, Off }
public enum KaraokeMode { Dream, All, Off }
public enum VideoMode { None, Zoktay, File }

public sealed class MapRequest
{
    public string Id = "";
    public bool FromYouTube = true;
    public string Url = "", AudioFile = "", CoverFile = "";
    public string Title = "", Artist = "", Language = "auto", Env = "auto";
    public double? Bpm;
    public string? ColorA, ColorB;
    public KaraokeMode Karaoke = KaraokeMode.Dream;
    public DreamMode Dream = DreamMode.Auto;
    public string VoiceRef = "";          // song id ("" = this song)
    public double VoiceStart, VoiceEnd;
    public double VoiceNegStart, VoiceNegEnd;   // optional: a passage by someone else, to tell the voices apart
    public VideoMode Video = VideoMode.None;
    public string VideoFile = "";
    public double VideoStart;
    public bool Install = true;
}

/// <summary>download → stems → analysis → lyrics → alignment → (voice id) → (video) → story → build → install.
/// Every step skips itself when its output already exists, so a failed run can simply be started again.</summary>
public static class MapPipeline
{
    static string W(params string[] p) => AppEnv.WS(p);

    public static List<JobStep> Steps(MapRequest r)
    {
        var id = r.Id;
        var wav = W("audio", id + ".wav");
        var stems = W("stems", "htdemucs", id);
        var steps = new List<JobStep>
        {
            new(Loc.T("StepAudio"), async c =>
            {
                if (File.Exists(wav)) { c.Skip(Loc.T("Exists")); return; }
                Directory.CreateDirectory(W("audio"));
                var ff = c.NeedExe("ffmpeg");
                if (r.FromYouTube)
                {
                    var yt = c.NeedExe("yt-dlp");
                    await c.Exec(yt, "--no-warnings", "--no-playlist", "-f", "bestaudio", "-x", "--audio-format", "wav",
                                 "--ffmpeg-location", Path.GetDirectoryName(ff)!, "-o", W("audio", id + ".%(ext)s"), r.Url);
                }
                else await c.Exec(ff, "-y", "-loglevel", "error", "-i", r.AudioFile, "-vn", "-ac", "2", "-ar", "44100", wav);
                if (!File.Exists(wav)) throw new StepFailedException("no audio/" + id + ".wav");
            }),
            new(Loc.T("StepCover"), async c =>
            {
                var cover = W("covers", id + ".jpg");
                if (File.Exists(cover)) { c.Skip(Loc.T("Exists")); return; }
                Directory.CreateDirectory(W("covers"));
                var ff = c.NeedExe("ffmpeg");
                var crop = "crop='min(iw,ih)':'min(iw,ih)',scale=512:512";
                var src = r.CoverFile;
                if (string.IsNullOrEmpty(src) && r.FromYouTube)
                {
                    Directory.CreateDirectory(W("thumbs"));
                    try
                    {
                        await c.Exec(c.NeedExe("yt-dlp"), "--no-warnings", "--no-playlist", "--skip-download", "--write-thumbnail", "--convert-thumbnails", "jpg",
                                     "--ffmpeg-location", Path.GetDirectoryName(ff)!, "-o", W("thumbs", id + ".%(ext)s"), r.Url);
                        if (File.Exists(W("thumbs", id + ".jpg"))) src = W("thumbs", id + ".jpg");
                    }
                    catch (StepFailedException e) { c.Log(e.Message); }
                }
                if (!string.IsNullOrEmpty(src))
                    await c.Exec(ff, "-y", "-loglevel", "error", "-i", src, "-vf", crop, "-q:v", "3", cover);
                else   // no picture: a generated cover in the saber colours
                {
                    var (a, b) = r.ColorA != null && r.ColorB != null ? (r.ColorA, r.ColorB) : DefaultColors(id);
                    await c.Py(c.Tool("makecover.py"), cover, r.Title, r.Artist, a, b, AppEnv.Settings.MapperName);
                }
            }),
            new(Loc.T("StepStems"), async c =>
            {
                if (File.Exists(Path.Combine(stems, "vocals.wav"))) { c.Skip(Loc.T("Exists")); return; }
                await c.Py("-m", "demucs", "-n", "htdemucs", "-d", await Device(), "-o", W("stems"), wav);
            }),
            new(Loc.T("StepAnalyze"), async c =>
            {
                if (File.Exists(W("ana", id + ".json"))) { c.Skip(Loc.T("Exists")); return; }
                Directory.CreateDirectory(W("ana"));
                await c.Py(c.Tool("analyze.py"), wav, stems, W("ana", id), r.Bpm is double b ? b.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-");
            }),
            new(Loc.T("StepTranscribe"), async c =>
            {
                if (File.Exists(W("words", id + ".json"))) { c.Skip(Loc.T("Exists")); return; }
                Directory.CreateDirectory(W("words"));
                var prompt = (r.Artist + " - " + r.Title + ".").Replace("|", "/");
                await c.Py(c.Tool("transcribe2.py"), W("words"), $"{id}|{r.Language}|{Path.Combine(stems, "vocals.wav")}|0|0|0|{prompt}");
            }),
            new(Loc.T("StepAlign"), async c =>
            {
                if (File.Exists(W("words", id + "_al.json"))) { c.Skip(Loc.T("Exists")); return; }
                await c.Py(c.Tool("alignall.py"), AppEnv.Workspace, $"{id}:{id}");
            }),
        };
        if (r.Dream == DreamMode.Voice)
            steps.Add(new(Loc.T("StepVoice"), async c =>
            {
                var refId = string.IsNullOrEmpty(r.VoiceRef) ? id : r.VoiceRef;
                var args = new List<string> { c.Tool("voiceid.py"), AppEnv.Workspace, refId, Inv(r.VoiceStart), Inv(r.VoiceEnd) };
                if (r.VoiceNegEnd > r.VoiceNegStart) args.AddRange([Inv(r.VoiceNegStart), Inv(r.VoiceNegEnd)]);
                await c.Py([.. args, "--", id]);
            }));
        if (r.Video == VideoMode.File)
            steps.Add(new(Loc.T("StepVideo"), async c =>
            {
                await c.Py(c.Tool("makevideo.py"), r.VideoFile, W("vid", id + ".bc3"), "10", "24", "640", Inv(r.VideoStart));
            }));
        steps.Add(new(Loc.T("StepStory"), async c =>
        {
            var env = r.Env == "auto" ? AutoEnv(id) : r.Env;
            var args = new List<string> { c.Tool("autostory.py"), AppEnv.Workspace, id, r.Title, r.Artist, env, "--apply",
                "--mapper", AppEnv.Settings.MapperName,
                "--dream", r.Dream switch { DreamMode.Voice => "voice+auto", DreamMode.Auto => "auto", _ => "none" },
                "--karaoke", r.Karaoke switch { KaraokeMode.All => "all", KaraokeMode.Dream => "dream", _ => "none" },
                "--video", r.Video switch { VideoMode.Zoktay => "zoktay.bc3", VideoMode.File => id + ".bc3", _ => "none" } };
            if (r.ColorA != null && r.ColorB != null) args.AddRange(["--colors", r.ColorA, r.ColorB]);
            var o = await c.Py([.. args]);
            c.Step.Detail = env;
        }));
        steps.Add(Build(id));
        if (r.Install) steps.Add(new(Loc.T("StepInstall"), c => Installer.InstallLevel(Folder(id)).Run(c)));
        return steps;
    }

    // the same rotation autostory.py uses when no colours are given
    static readonly (string, string)[] Sabers = [("FF7EB6", "B48CFF"), ("FF4FA3", "4FC3FF"), ("FF6F91", "FFC75F"), ("C77DFF", "72EFDD"), ("FF5D8F", "7AE582"), ("FF9E00", "B5179E")];
    public static (string a, string b) DefaultColors(string id) => Sabers[(int.TryParse(id, out var n) ? n : 0) % Sabers.Length];

    public static string Folder(string id) => (string?)SongStore.Load()[id]?["folder"] ?? throw new StepFailedException("songs.json: " + id + "?");

    /// <summary>build.py; when no official level has a usable tempo it retries with any_donor_bpm (as the batch scripts did).</summary>
    public static JobStep Build(string id) => new(Loc.T("StepBuild"), async c =>
    {
        try { await c.Py(c.Tool("build.py"), id); }
        catch (StepFailedException e) when (e.Message.Contains("no donors"))
        {
            c.Log("no donors at this tempo → any_donor_bpm");
            SongStore.Set(id, "any_donor_bpm", true);
            await c.Py(c.Tool("build.py"), id);
        }
        var rep = W("out", "report_" + id + ".json");
        if (File.Exists(rep))
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(rep));
                c.Step.Detail = $"{(double?)j?["bpm"]:0.#} BPM";
            }
            catch { }
    });

    static string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static string? _device;
    static async Task<string> Device()
    {
        if (AppEnv.Settings.Device != "auto") return AppEnv.Settings.Device;
        _device ??= (await Installer.CheckPython(AppEnv.Python))?.EndsWith("True") == true ? "cuda" : "cpu";
        return _device;
    }

    /// <summary>Six-colour environments in rotation (like the manifest batch), else the most common one the user owns.</summary>
    static string AutoEnv(string id)
    {
        var have = SongStore.Environments();
        var six = SongStore.SixColour.Where(have.Contains).ToList();
        if (six.Count > 0) return six[(int.TryParse(id, out var n) ? n : 0) % six.Count];
        return have.FirstOrDefault() ?? "DefaultEnvironment";
    }
}

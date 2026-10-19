using System.Text.RegularExpressions;

namespace OnuradaMapper.Services;

public sealed record VideoHit(string Id, string Url, string Title, string Channel, string Duration)
{
    public string Thumb => $"https://i.ytimg.com/vi/{Id}/mqdefault.jpg";
}

/// <summary>yt-dlp lookups: search, playlist expansion, title/artist guessing.</summary>
public static class YouTube
{
    const string Fmt = "%(id)s\t%(webpage_url,url)s\t%(track,title)s\t%(artist,creator,channel,uploader)s\t%(duration_string)s";

    static async Task<List<VideoHit>> Run(params string[] args)
    {
        var yt = AppEnv.Exe("yt-dlp") ?? throw new InvalidOperationException(Loc.T("ErrMissingExe") + " yt-dlp");
        var (_, outp) = await ProcessRunner.CaptureAsync(yt, ["--no-warnings", "--ignore-errors", "--flat-playlist", "--print", Fmt, .. args]);
        var res = new List<VideoHit>();
        foreach (var line in outp.Split('\n'))
        {
            var p = line.Trim().Split('\t');
            if (p.Length < 5 || p[0].Length < 5) continue;
            var url = p[1].StartsWith("http") ? p[1] : "https://www.youtube.com/watch?v=" + p[0];
            res.Add(new VideoHit(p[0], url, p[2], Clean(p[3]), p[4] == "NA" ? "" : p[4]));
        }
        return res;
    }

    public static Task<List<VideoHit>> Search(string query, int n = 10) => Run($"ytsearch{n}:{query}");

    /// <summary>A video link → one hit; a playlist link → all of its videos.</summary>
    public static Task<List<VideoHit>> Expand(string url) => Run(url.Trim());

    static string Clean(string ch) => ch == "NA" ? "" : ch.Replace(" - Topic", "").Replace("VEVO", "").Trim();

    /// <summary>"Artist - Title (Official Video)" → (Title, Artist).</summary>
    public static (string title, string artist) Guess(string title, string channel)
    {
        var a = channel;
        var m = Regex.Match(title, @"^(.+?)\s+[-–—]\s+(.+)$");
        if (m.Success && (a.Length == 0 || title.StartsWith(a, StringComparison.OrdinalIgnoreCase) || !title.Contains(a, StringComparison.OrdinalIgnoreCase)))
        { a = m.Groups[1].Value; title = m.Groups[2].Value; }
        title = Regex.Replace(title, @"\s*[\(\[][^\)\]]*(official|video|audio|lyric|klip|mv|visualizer|music)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*\|.*$", "").Trim().Trim('"', '\'');
        return (title, a.Trim());
    }
}

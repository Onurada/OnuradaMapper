using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnuradaMapper.Services;

public sealed record SongInfo(string Id, string Title, string Artist, string Env, string Folder, bool Built, bool Installed, bool HasDream, bool HasVideo)
{
    public string OutDir => AppEnv.WS("out", Folder);
    public string GameLevelDir => Path.Combine(AppEnv.CustomLevelsDir, Folder);
}

/// <summary>songs.json: one config object per song id, written by autostory.py and read by build.py.</summary>
public static class SongStore
{
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Load()
    {
        var p = AppEnv.SongsJson;
        if (!File.Exists(p)) return [];
        var text = File.ReadAllText(p).TrimStart('﻿');
        return JsonNode.Parse(text) as JsonObject ?? [];
    }

    public static void Save(JsonObject all) => File.WriteAllText(AppEnv.SongsJson, all.ToJsonString(WriteOpts));

    public static List<SongInfo> List()
    {
        var all = Load();
        var res = new List<SongInfo>();
        foreach (var (id, node) in all)
        {
            if (node is not JsonObject o) continue;
            var folder = (string?)o["folder"] ?? id;
            res.Add(new SongInfo(id, (string?)o["title"] ?? "?", (string?)o["artist"] ?? "", (string?)o["env"] ?? "",
                folder, File.Exists(AppEnv.WS("out", folder, "Info.dat")),
                Directory.Exists(Path.Combine(AppEnv.CustomLevelsDir, folder)), o["dream"] != null, o["video"] != null));
        }
        return res.OrderBy(s => int.TryParse(s.Id, out var n) ? n : 9999).ThenBy(s => s.Id).ToList();
    }

    public static string NextId()
    {
        int max = 0;
        foreach (var (id, _) in Load()) if (int.TryParse(id, out var n)) max = Math.Max(max, n);
        // also skip ids that already have audio from an unfinished run
        while (File.Exists(AppEnv.WS("audio", $"{max + 1:D2}.wav"))) max++;
        return $"{max + 1:D2}";
    }

    public static string Pretty(JsonNode n) => n.ToJsonString(WriteOpts);

    public static void Set(string id, string key, JsonNode? value)
    {
        var all = Load();
        if (all[id] is JsonObject o) { o[key] = value; Save(all); }
    }

    /// <summary>Environments found in the user's own official levels (levelenv.json), most used first.</summary>
    public static List<string> Environments()
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(AppEnv.WS("levelenv.json"))) as JsonObject;
            if (j == null) return [];
            return j.SelectMany(kv => kv.Value?["envs"]?.AsArray().Select(e => (string?)e) ?? [])
                    .Where(e => !string.IsNullOrEmpty(e)).GroupBy(e => e!).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        }
        catch { return []; }
    }

    // group-lighting environments give six light colours
    public static readonly string[] SixColour = ["BritneyEnvironment", "GagaEnvironment", "LizzoEnvironment", "TheWeekndEnvironment"];
}

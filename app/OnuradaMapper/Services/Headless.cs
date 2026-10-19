using System.IO;
using System.Text.Json;
using System.Windows;

namespace OnuradaMapper.Services;

/// <summary>OnuradaMapper.exe [--workspace DIR] --make request.json : builds one map without a window.
/// The log goes to request.log next to the request; the exit code is 0 when every step succeeded.</summary>
public static class Headless
{
    public static readonly JsonSerializerOptions Opts = new() { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static async Task MakeAsync(string requestFile)
    {
        var logFile = Path.ChangeExtension(requestFile, ".log");
        int code = 1;
        try
        {
            var reqs = LoadRequests(requestFile);
            using var log = new StreamWriter(logFile, false) { AutoFlush = true };
            code = 0;
            foreach (var r in reqs)
            {
                if (string.IsNullOrEmpty(r.Id)) r.Id = SongStore.NextId();
                log.WriteLine($"=== #{r.Id} {r.Artist} - {r.Title}");
                var steps = MapPipeline.Steps(r);
                var ok = await JobService.I.RunAsync(r.Title, steps, line => { lock (log) log.WriteLine(line); });
                foreach (var s in steps) log.WriteLine($"[{s.State}] {s.Name}  {s.Detail}");
                if (!ok) code = 1;
            }
        }
        catch (Exception e) { File.AppendAllText(logFile, "FATAL " + e + Environment.NewLine); }
        Application.Current.Shutdown(code);
    }

    /// <summary>A single request object or an array of them.</summary>
    public static List<MapRequest> LoadRequests(string file)
    {
        var text = File.ReadAllText(file).TrimStart('﻿').Trim();
        return text.StartsWith('[') ? JsonSerializer.Deserialize<List<MapRequest>>(text, Opts) ?? []
                                    : [JsonSerializer.Deserialize<MapRequest>(text, Opts)!];
    }
}

using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnuradaMapper.Services;

public enum StepState { Pending, Running, Done, Skipped, Failed }

public sealed class StepFailedException(string message) : Exception(message);

public partial class JobStep(string name, Func<JobContext, Task> run) : ObservableObject
{
    public string Name { get; } = name;
    public Func<JobContext, Task> Run { get; } = run;
    [ObservableProperty] private StepState _state;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress = -1;   // -1 = indeterminate
}

public sealed class JobContext(JobStep step, CancellationToken ct, Action<string> log)
{
    public JobStep Step { get; } = step;
    public CancellationToken Ct { get; } = ct;
    public void Log(string line) => log(line);
    public void Skip(string why) { Step.State = StepState.Skipped; Step.Detail = why; }

    static readonly Regex Pct = new(@"(\d{1,3})%\|", RegexOptions.Compiled);

    /// <summary>Runs an exe; a non-zero exit fails the step. tqdm percentages drive the step's progress bar.</summary>
    public async Task<string> Exec(string exe, params string[] args)
    {
        var tail = new Queue<string>();
        var code = await ProcessRunner.RunAsync(exe, args, line =>
        {
            var m = Pct.Match(line);
            if (m.Success) { Step.Progress = Math.Min(100, int.Parse(m.Groups[1].Value)); return; }
            log(line);
            lock (tail) { tail.Enqueue(line); if (tail.Count > 30) tail.Dequeue(); }
        }, Ct);
        string text; lock (tail) text = string.Join("\n", tail);
        if (code != 0) throw new StepFailedException($"{System.IO.Path.GetFileName(exe)} exit {code}\n{text}");
        return text;
    }

    public Task<string> Py(params string[] args)
    {
        var py = AppEnv.Python;
        if (string.IsNullOrEmpty(py)) throw new StepFailedException(Loc.T("ErrNoPython"));
        return Exec(py, ["-u", .. args]);
    }

    public string Tool(string script) => System.IO.Path.Combine(AppEnv.ToolsDir, script);

    public string NeedExe(string name) => AppEnv.Exe(name) ?? throw new StepFailedException(Loc.T("ErrMissingExe") + " " + name);
}

/// <summary>The one background job at a time (map build, setup install...), shown in the job panel.</summary>
public partial class JobService : ObservableObject
{
    public static JobService I { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasJob;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _logText = "";
    public ObservableCollection<JobStep> Steps { get; } = [];

    readonly StringBuilder _log = new();
    readonly DispatcherTimer _flush;
    bool _dirty;
    CancellationTokenSource? _cts;

    JobService()
    {
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _flush.Tick += (_, _) =>
        {
            if (!_dirty) return;
            lock (_log)
            {
                if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
                LogText = _log.ToString(); _dirty = false;
            }
        };
        _flush.Start();
    }

    Action<string>? _tee;
    public void Log(string line) { lock (_log) { _log.AppendLine(line); _dirty = true; } _tee?.Invoke(line); }

    [RelayCommand]
    void Cancel() => _cts?.Cancel();

    [RelayCommand]
    void Close() { if (!IsRunning) HasJob = false; }

    /// <summary>Runs the steps in order; returns true when all finished (or were skipped).</summary>
    public async Task<bool> RunAsync(string title, IEnumerable<JobStep> steps, Action<string>? tee = null)
    {
        if (IsRunning) { MessageBox.Show(Loc.T("ErrJobRunning"), "Onurada Mapper"); return false; }
        _tee = tee;
        Title = title; Steps.Clear();
        foreach (var s in steps) Steps.Add(s);
        lock (_log) { _log.Clear(); _dirty = true; }
        IsRunning = HasJob = true; Progress = 0; Status = "";
        _cts = new CancellationTokenSource();
        var ok = true;
        var started = DateTime.Now;
        for (int i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            s.State = StepState.Running; s.Progress = -1;
            Status = s.Name;
            Log($"── {s.Name}");
            var t0 = DateTime.Now;
            try
            {
                await Task.Run(() => s.Run(new JobContext(s, _cts.Token, Log)));
                if (s.State == StepState.Running) { s.State = StepState.Done; s.Detail = Fmt(DateTime.Now - t0); }
            }
            catch (OperationCanceledException)
            {
                s.State = StepState.Failed; s.Detail = Loc.T("Cancelled"); Status = Loc.T("Cancelled"); ok = false; break;
            }
            catch (Exception e)
            {
                s.State = StepState.Failed; s.Detail = e.Message.Split('\n')[0];
                Log("ERROR: " + e.Message); Status = Loc.T("Failed") + ": " + s.Name; ok = false; break;
            }
            s.Progress = 100;
            Progress = 100.0 * (i + 1) / Steps.Count;
        }
        if (ok) Status = Loc.T("Finished") + " (" + Fmt(DateTime.Now - started) + ")";
        IsRunning = false;
        try
        {
            var dir = AppEnv.WS("logs", "jobs");
            System.IO.Directory.CreateDirectory(dir);
            var name = string.Concat(title.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '#' ? ch : '_')).Trim();
            lock (_log) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{started:yyyy-MM-dd_HHmmss} {name[..Math.Min(60, name.Length)]}.log"), _log.ToString());
        }
        catch { }
        if (_tee == null && (DateTime.Now - started).TotalSeconds > 20) Notify.Done(title + " — " + Status, ok);
        _cts.Dispose(); _cts = null;
        return ok;
    }

    static string Fmt(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.0}s";
}

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnuradaMapper.Services;

/// <summary>Plays audio/NN.wav (song seconds, the same clock as songs.json) and ticks Position every frame.</summary>
public partial class AudioPlayer : ObservableObject, IDisposable
{
    readonly MediaPlayer _mp = new();
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _volume = 0.8;
    public bool Loaded { get; private set; }
    public event Action? Tick;

    public AudioPlayer()
    {
        _mp.MediaOpened += (_, _) => { Duration = _mp.NaturalDuration.HasTimeSpan ? _mp.NaturalDuration.TimeSpan.TotalSeconds : 0; Loaded = true; };
        _mp.MediaEnded += (_, _) => { IsPlaying = false; _mp.Pause(); };
        _mp.Volume = _volume;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Open(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        _mp.Open(new Uri(path));
        _mp.Volume = Volume;
    }

    partial void OnVolumeChanged(double value) => _mp.Volume = value;

    void OnFrame(object? s, EventArgs e)
    {
        if (!IsPlaying) return;
        Position = _mp.Position.TotalSeconds;
        Tick?.Invoke();
    }

    [RelayCommand]
    public void Toggle()
    {
        if (IsPlaying) { _mp.Pause(); IsPlaying = false; }
        else { _mp.Play(); IsPlaying = true; }
    }

    public void Seek(double t)
    {
        t = Math.Max(0, Duration > 0 ? Math.Min(t, Duration) : t);
        _mp.Position = TimeSpan.FromSeconds(t);
        Position = t;
        Tick?.Invoke();
    }

    [RelayCommand] void Back() => Seek(Position - 5);
    [RelayCommand] void Forward() => Seek(Position + 5);

    public void Stop() { _mp.Pause(); IsPlaying = false; }

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnFrame;
        _mp.Close();
    }
}

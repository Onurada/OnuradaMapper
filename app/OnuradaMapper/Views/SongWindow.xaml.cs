using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OnuradaMapper.Services;
using OnuradaMapper.ViewModels;

namespace OnuradaMapper.Views;

public partial class SongWindow : Window
{
    readonly SongViewModel _vm;
    public event Action? Changed;

    public SongWindow(string id)
    {
        InitializeComponent();
        _vm = new SongViewModel(id);
        DataContext = _vm;
        Title = $"#{id}  {_vm.Title}";
        Timeline.Player = _vm.Player;
        Preview.Player = _vm.Player;
        _vm.Reloaded += Wire;
        _vm.PreviewChanged += () => Preview.Diff = _vm.Level?.LoadDiff(_vm.PreviewDiff);
        _vm.Deleted += () => { Changed?.Invoke(); Close(); };
        _vm.Player.Tick += UpdateTime;
        _vm.Player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AudioPlayer.Duration)) UpdateTime(); };
        Wire();
        PreviewKeyDown += OnKey;
        WordGrid.MouseDoubleClick += (_, _) => _vm.PlayWordCommand.Execute(null);
    }

    void Wire()
    {
        Timeline.Model = _vm.Story;
        Timeline.Peaks = _vm.Peaks;
        Timeline.Grid = _vm.Grid;
        Timeline.Words = _vm.WordMarks;
        Timeline.InvalidateVisual();
        Preview.Level = _vm.Level;
        Preview.Story = _vm.Story;
        Preview.Pad = _vm.Pad;
        Preview.Diff = _vm.Level?.LoadDiff(_vm.PreviewDiff);
        Changed?.Invoke();
    }

    /// <summary>--song ID --shots DIR: renders every tab (preview at a few moments) and quits.</summary>
    public async Task CaptureAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        await Task.Delay(3000);
        _vm.Player.Seek(_vm.Story.Chorus.FirstOrDefault()?.A + 6 ?? 40);
        for (int i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            await Task.Delay(1500);
            MainWindow.Shot(this, System.IO.Path.Combine(dir, $"song_tab{i}.png"));
        }
        Tabs.SelectedIndex = 1;
        foreach (var t in new[] { _vm.Story.Dream.FirstOrDefault()?.A + 3 ?? 70, (_vm.Story.Drops.FirstOrDefault() + 0.15) })
        {
            _vm.Player.Seek(t);
            await Task.Delay(800);
            MainWindow.Shot(this, System.IO.Path.Combine(dir, $"song_preview_{t:0}.png"));
        }
        Application.Current.Shutdown();
    }

    void UpdateTime() =>
        TimeText.Text = $"{TimelineControl.Fmt(_vm.Player.Position)} / {TimelineControl.Fmt(_vm.Player.Duration)}";

    void OnKey(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { _vm.SaveOnlyCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) { _vm.SaveAndBuildCommand.Execute(null); e.Handled = true; }
        else if (typing) return;
        else if (e.Key == Key.Space) { _vm.Player.Toggle(); e.Handled = true; }
        else if (e.Key == Key.Left) { _vm.Player.Seek(_vm.Player.Position - (Keyboard.Modifiers == ModifierKeys.Shift ? 0.1 : 2)); e.Handled = true; }
        else if (e.Key == Key.Right) { _vm.Player.Seek(_vm.Player.Position + (Keyboard.Modifiers == ModifierKeys.Shift ? 0.1 : 2)); e.Handled = true; }
        else if (e.Key == Key.T && Tabs.SelectedIndex == 2) { _vm.SetWordToPlayheadCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter && Tabs.SelectedIndex == 2) { _vm.PlayWordCommand.Execute(null); e.Handled = true; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Dirty || _vm.WordsDirty)
        {
            var r = MessageBox.Show(Loc.T("SongSaveChanges"), _vm.Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) _vm.Save();
        }
        _vm.Dispose();
        Changed?.Invoke();
        base.OnClosing(e);
    }
}

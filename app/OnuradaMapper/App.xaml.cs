using System.Windows;
using System.Windows.Threading;
using OnuradaMapper.Services;

namespace OnuradaMapper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnError;
        AppEnv.Load();
        var lang = Array.IndexOf(e.Args, "--lang");
        Loc.Apply(lang >= 0 && lang + 1 < e.Args.Length ? e.Args[lang + 1] : AppEnv.Settings.Language);
        ApplyTheme(AppEnv.Settings.Theme);
        base.OnStartup(e);
        var ws = Array.IndexOf(e.Args, "--workspace");
        if (ws >= 0 && ws + 1 < e.Args.Length) AppEnv.Settings.Workspace = e.Args[ws + 1];
        var mk = Array.IndexOf(e.Args, "--make");
        if (mk >= 0 && mk + 1 < e.Args.Length) { ShutdownMode = ShutdownMode.OnExplicitShutdown; _ = Headless.MakeAsync(e.Args[mk + 1]); return; }
        var w = new MainWindow();
        w.Show();
        var i = Array.IndexOf(e.Args, "--shots");
        var song = Array.IndexOf(e.Args, "--song");
        if (song >= 0 && song + 1 < e.Args.Length)
        {
            var sw = new Views.SongWindow(e.Args[song + 1]) { Owner = w };
            sw.Show();
            if (i >= 0 && i + 1 < e.Args.Length) _ = sw.CaptureAsync(e.Args[i + 1]);
        }
        else if (i >= 0 && i + 1 < e.Args.Length) _ = w.CaptureAsync(e.Args[i + 1]);
    }

    public static void ApplyTheme(string theme) =>
        Current.ThemeMode = theme switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };

    static void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Onurada Mapper", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OnuradaMapper.Services;

/// <summary>"Your map is ready": a tray balloon, a sound and a flashing taskbar button when the window is in the background.</summary>
public static class Notify
{
    static System.Windows.Forms.NotifyIcon? _icon;

    public static void Done(string message, bool ok)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var w = Application.Current.MainWindow;
            if (w == null || w.IsActive) { (ok ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play(); return; }
            (ok ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play();
            Flash(w);
            try
            {
                if (_icon == null)
                {
                    var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                    _icon = new System.Windows.Forms.NotifyIcon { Icon = res != null ? new System.Drawing.Icon(res.Stream) : System.Drawing.SystemIcons.Application, Text = "Onurada Mapper" };
                    _icon.BalloonTipClicked += (_, _) => { w.Show(); w.Activate(); };
                    Application.Current.Exit += (_, _) => _icon.Dispose();
                }
                _icon.Visible = true;
                _icon.ShowBalloonTip(6000, "Onurada Mapper", message, ok ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
            }
            catch { }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    static void Flash(Window w)
    {
        var fi = new FLASHWINFO { hwnd = new WindowInteropHelper(w).Handle, dwFlags = 3 | 12, uCount = 5 };   // all, until focused
        fi.cbSize = (uint)Marshal.SizeOf(fi);
        FlashWindowEx(ref fi);
    }
}

using System.Windows.Controls;

namespace OnuradaMapper.Views;

public partial class JobPanel : UserControl
{
    public JobPanel() => InitializeComponent();

    void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}

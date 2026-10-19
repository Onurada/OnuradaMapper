using System.Windows.Controls;
using System.Windows.Input;
using OnuradaMapper.ViewModels;

namespace OnuradaMapper.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && vm.OpenCommand.CanExecute(null)) vm.OpenCommand.Execute(null);
    }
}

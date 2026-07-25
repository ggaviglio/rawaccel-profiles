using Avalonia.Controls;
using MouseApp.ViewModels;

namespace MouseApp.Views;

public partial class TargetManagerWindow : Window
{
    public TargetManagerWindow()
    {
        InitializeComponent();
    }

    public TargetManagerWindow(TargetManagerViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
    }
}

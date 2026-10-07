using Avalonia.Controls;
using RawAccelProfiles.ViewModels;

namespace RawAccelProfiles.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
    }
}

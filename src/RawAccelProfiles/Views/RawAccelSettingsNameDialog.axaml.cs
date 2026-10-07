using Avalonia.Controls;
using RawAccelProfiles.ViewModels;

namespace RawAccelProfiles.Views;

public partial class RawAccelSettingsNameDialog : Window
{
    public RawAccelSettingsNameDialog()
    {
        InitializeComponent();
    }

    public RawAccelSettingsNameDialog(RawAccelSettingsNameDialogViewModel vm) : this()
    {
        DataContext = vm;

        Opened += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }
}

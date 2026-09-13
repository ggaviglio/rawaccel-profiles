using Avalonia.Controls;
using MouseApp.ViewModels;

namespace MouseApp.Views;

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

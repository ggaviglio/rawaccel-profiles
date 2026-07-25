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
            var box = this.FindControl<TextBox>("NameTextBox");
            box?.Focus();
            box?.SelectAll();
        };
    }
}

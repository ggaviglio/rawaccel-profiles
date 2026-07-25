using Avalonia.Controls;
using MouseApp.ViewModels;

namespace MouseApp.Views;

public partial class TemplateNameDialog : Window
{
    public TemplateNameDialog()
    {
        InitializeComponent();
    }

    public TemplateNameDialog(TemplateNameDialogViewModel vm) : this()
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

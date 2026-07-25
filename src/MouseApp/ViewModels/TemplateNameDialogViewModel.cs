using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MouseApp.ViewModels;

/// <summary>
/// Backing ViewModel for the small "enter a template name" dialog used by
/// Import Current Settings and Duplicate Template.
/// </summary>
public partial class TemplateNameDialogViewModel : ObservableObject
{
    [ObservableProperty] private string title = string.Empty;
    [ObservableProperty] private string templateName = string.Empty;

    /// <summary>Raised when the dialog should close. Payload is the confirmed name, or null if cancelled.</summary>
    public event Action<string?>? RequestClose;

    public IRelayCommand OkCommand     { get; }
    public IRelayCommand CancelCommand { get; }

    public TemplateNameDialogViewModel(string title, string defaultName)
    {
        OkCommand     = new RelayCommand(() => RequestClose?.Invoke(TemplateName),
                                          () => !string.IsNullOrWhiteSpace(TemplateName));
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(null));

        Title        = title;
        TemplateName = defaultName;
    }

    partial void OnTemplateNameChanged(string value) => OkCommand.NotifyCanExecuteChanged();
}

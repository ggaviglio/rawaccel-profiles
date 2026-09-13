using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MouseApp.ViewModels;

/// <summary>
/// Backing ViewModel for the small "enter a name" dialog used by Import Current Settings,
/// Duplicate, and Rename for Raw Accel settings files.
/// </summary>
public partial class RawAccelSettingsNameDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string title = string.Empty;
    [ObservableProperty] private string name = string.Empty;

    /// <summary>Raised when the dialog should close. Payload is the confirmed name, or null if cancelled.</summary>
    public event Action<string?>? RequestClose;

    public IRelayCommand OkCommand     { get; }
    public IRelayCommand CancelCommand { get; }

    public RawAccelSettingsNameDialogViewModel(string title, string defaultName)
    {
        OkCommand     = new RelayCommand(() => RequestClose?.Invoke(Name),
                                          () => !string.IsNullOrWhiteSpace(Name));
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(null));

        Title = title;
        Name  = defaultName;
    }

    partial void OnNameChanged(string value) => OkCommand.NotifyCanExecuteChanged();
}

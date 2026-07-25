using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MouseApp.Models;
using MouseApp.ViewModels;
using MouseApp.Views;

namespace MouseApp.Services;

/// <summary>
/// Avalonia implementation of <see cref="IWindowService"/>.
/// Creates and shows Avalonia windows from ViewModel requests.
/// </summary>
public sealed class AvaloniaWindowService : IWindowService
{
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public Task ShowTargetManagerAsync(MouseProfile profile, ProfileService profileService)
    {
        var vm  = new TargetManagerViewModel(profile, profileService);
        var win = new TargetManagerWindow(vm);

        var tcs = new TaskCompletionSource();
        win.Closed += (_, _) => tcs.TrySetResult();

        var owner = MainWindow;
        if (owner is not null)
            win.ShowDialog(owner);
        else
            win.Show();

        return tcs.Task;
    }

    public Task<string?> ShowTemplateNameDialogAsync(string title, string defaultName)
    {
        var vm  = new TemplateNameDialogViewModel(title, defaultName);
        var win = new TemplateNameDialog(vm);

        var tcs = new TaskCompletionSource<string?>();
        vm.RequestClose += result =>
        {
            tcs.TrySetResult(result);
            win.Close();
        };
        win.Closed += (_, _) => tcs.TrySetResult(null);

        var owner = MainWindow;
        if (owner is not null)
            win.ShowDialog(owner);
        else
            win.Show();

        return tcs.Task;
    }

    public Task ShowSettingsAsync(SettingsViewModel settingsViewModel)
    {
        var win = new SettingsWindow(settingsViewModel);

        var tcs = new TaskCompletionSource();
        win.Closed += (_, _) => tcs.TrySetResult();

        var owner = MainWindow;
        if (owner is not null)
            win.ShowDialog(owner);
        else
            win.Show();

        return tcs.Task;
    }
}

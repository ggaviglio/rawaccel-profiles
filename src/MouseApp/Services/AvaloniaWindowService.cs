using System.Threading.Tasks;
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
    public Task ShowTargetManagerAsync(MouseProfile profile, ProfileService profileService)
    {
        var vm  = new TargetManagerViewModel(profile, profileService);
        var win = new TargetManagerWindow(vm);

        var tcs = new TaskCompletionSource();
        win.Closed += (_, _) => tcs.TrySetResult();
        win.Show();

        return tcs.Task;
    }
}

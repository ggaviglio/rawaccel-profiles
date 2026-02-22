using System.Threading.Tasks;
using MouseApp.Models;

namespace MouseApp.Services;

/// <summary>
/// Abstraction for displaying utility windows so ViewModels stay UI-agnostic.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Opens the Target Manager window for the given profile.
    /// The returned Task completes when the window is closed.
    /// </summary>
    Task ShowTargetManagerAsync(MouseProfile profile, ProfileService profileService);
}

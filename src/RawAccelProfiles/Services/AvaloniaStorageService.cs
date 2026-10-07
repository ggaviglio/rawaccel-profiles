using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace RawAccelProfiles.Services;

public class AvaloniaStorageService : IStorageService
{
    public async Task<string?> PickFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        var window = desktop.MainWindow;
        var provider = window?.StorageProvider;
        if (provider == null)
            return null;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder",
            AllowMultiple = false
        }).ConfigureAwait(false);

        if (result != null && result.Count > 0)
        {
            var localPath = result.First().TryGetLocalPath();
            return localPath ?? result.First().Name;
        }

        return null;
    }
}

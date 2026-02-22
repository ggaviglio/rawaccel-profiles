using System.Threading.Tasks;

namespace MouseApp.Services;

public interface IStorageService
{
    /// <summary>
    /// Shows a folder picker and returns the selected folder path, or null if cancelled.
    /// </summary>
    Task<string?> PickFolderAsync();

    /// <summary>
    /// Shows a file picker and returns the selected file path, or null if cancelled.
    /// </summary>
    Task<string?> PickFileAsync(string? title = null);
}

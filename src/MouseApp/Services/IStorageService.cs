using System.Threading.Tasks;

namespace MouseApp.Services;

public interface IStorageService
{
    /// <summary>
    /// Shows a folder picker and returns the selected folder path, or null if cancelled.
    /// </summary>
    Task<string?> PickFolderAsync();
}

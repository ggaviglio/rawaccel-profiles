using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseApp.Models;

namespace MouseApp.Services;

/// <summary>
/// Manages the library of Raw Accel settings files (.json curves) — import, duplicate,
/// rename, delete, and list. A pure file-persistence concern (no business logic beyond
/// blacklisting protected filenames), distinct from RawAccelService which applies a
/// settings file live to hardware.
/// </summary>
public class RawAccelSettingsRepository
{
    /// <summary>
    /// Files that should never be treated as settings entries to prevent accidental overwriting.
    /// Blacklist includes: app data files (profiles.json), Raw Accel defaults (settings.json),
    /// and platform-specific binaries/metadata.
    /// </summary>
    private static readonly HashSet<string> BlacklistedNames = new(
        new[]
        {
            "profiles.json",                    // MouseApp profile database
            "settings.json",                    // Raw Accel default settings
            "MouseApp.deps.json",               // .NET dependency metadata
            "MouseApp.runtimeconfig.json",      // .NET runtime configuration
        },
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ProfileService _profileService;

    public event EventHandler? FilesChanged;

    public List<string> Files { get; private set; } = new();

    public RawAccelSettingsRepository(ProfileService profileService)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _profileService.SettingsChanged += (_, __) => Refresh();
        Refresh();
    }

    /// <summary>
    /// Returns the effective settings directory: the user-configured path if set,
    /// otherwise the app executable folder (portable default).
    /// </summary>
    public string ResolveDirectory()
    {
        var configured = _profileService.Settings?.RawAccelSettingsDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return AppContext.BaseDirectory;
    }

    public void Refresh()
    {
        var dir = ResolveDirectory();
        if (!Directory.Exists(dir))
        {
            Files = new List<string>();
            FilesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            Files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(x => x != null && !BlacklistedNames.Contains(x))
                .Select(x => x!)
                .OrderBy(x => x)
                .ToList();
        }
        catch
        {
            Files = new List<string>();
        }

        FilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> GetAll() => Files.AsReadOnly();

    /// <summary>
    /// Returns true if the given filename is blacklisted (cannot be used as a settings file name).
    /// Comparison is case-insensitive.
    /// </summary>
    private static bool IsBlacklisted(string filename)
    {
        return BlacklistedNames.Contains(filename);
    }

    public string? GetFilePath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        return Path.Combine(ResolveDirectory(), fileName);
    }

    /// <summary>
    /// Imports a .json file into the settings directory, creating the folder if needed.
    /// Returns the name of the imported file, or null on failure.
    /// Rejects blacklisted filenames (protected app/system files) to prevent overwrites.
    /// </summary>
    public string? Import(string sourcePath, string fileName)
    {
        try
        {
            // Normalize to .json extension for consistency
            var finalName = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName + ".json";

            // Reject blacklisted names
            if (IsBlacklisted(finalName))
                return null;

            var dir = ResolveDirectory();
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, finalName);
            File.Copy(sourcePath, dest, overwrite: true);
            Refresh();
            return Path.GetFileName(dest);
        }
        catch { return null; }
    }

    /// <summary>
    /// Duplicates an existing settings file under a new name.
    /// Returns the new file name, or null on failure.
    /// </summary>
    public string? Duplicate(string existingName, string newName)
    {
        var src = GetFilePath(existingName);
        if (src == null || !File.Exists(src)) return null;
        return Import(src, newName);
    }

    /// <summary>Deletes a settings file from the library.</summary>
    public bool Delete(string fileName)
    {
        var path = GetFilePath(fileName);
        if (path == null || !File.Exists(path)) return false;
        try { File.Delete(path); Refresh(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Renames an existing settings file in place.
    /// Returns the new file name, or null on failure (missing source, blacklisted target, or name collision).
    /// Does not update any MouseProfile references — callers must handle that separately.
    /// </summary>
    public string? Rename(string existingName, string newName)
    {
        var src = GetFilePath(existingName);
        if (src == null || !File.Exists(src)) return null;

        var finalName = newName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? newName
            : newName + ".json";

        if (IsBlacklisted(finalName)) return null;

        var dir  = ResolveDirectory();
        var dest = Path.Combine(dir, finalName);

        if (File.Exists(dest) && !string.Equals(dest, src, StringComparison.OrdinalIgnoreCase))
            return null; // avoid silently overwriting a different existing file

        try
        {
            File.Move(src, dest, overwrite: false);
            Refresh();
            return Path.GetFileName(dest);
        }
        catch { return null; }
    }
}

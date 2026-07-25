using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseApp.Models;

namespace MouseApp.Services;

public class TemplateService
{
    /// <summary>
    /// Files that should never be treated as templates to prevent accidental overwriting.
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

    public event EventHandler? TemplatesChanged;

    public List<string> Templates { get; private set; } = new();

    public TemplateService(ProfileService profileService)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _profileService.SettingsChanged += (_, __) => Refresh();
        Refresh();
    }

    /// <summary>
    /// Returns the effective templates directory: the user-configured path if set,
    /// otherwise the app executable folder (portable default).
    /// </summary>
    public string ResolveTemplatesDirectory()
    {
        var configured = _profileService.Settings?.TemplatesDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return AppContext.BaseDirectory;
    }

    public void Refresh()
    {
        var dir = ResolveTemplatesDirectory();
        if (!Directory.Exists(dir))
        {
            Templates = new List<string>();
            TemplatesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            Templates = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(x => x != null && !BlacklistedNames.Contains(x))
                .Select(x => x!)
                .OrderBy(x => x)
                .ToList();
        }
        catch
        {
            Templates = new List<string>();
        }

        TemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> GetTemplates() => Templates.AsReadOnly();

    /// <summary>
    /// Returns true if the given filename is blacklisted (cannot be used as a template name).
    /// Comparison is case-insensitive.
    /// </summary>
    private static bool IsBlacklisted(string filename)
    {
        return BlacklistedNames.Contains(filename);
    }

    public string? GetTemplatePath(string templateName)
    {
        if (string.IsNullOrEmpty(templateName)) return null;
        return Path.Combine(ResolveTemplatesDirectory(), templateName);
    }

    /// <summary>
    /// Imports a .json file into the templates directory, creating the folder if needed.
    /// Returns the name of the imported template, or null on failure.
    /// Rejects blacklisted filenames (protected app/system files) to prevent overwrites.
    /// </summary>
    public string? ImportTemplate(string sourcePath, string templateName)
    {
        try
        {
            // Normalize to .json extension for consistency
            var finalName = templateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? templateName
                : templateName + ".json";

            // Reject blacklisted names
            if (IsBlacklisted(finalName))
                return null;

            var dir = ResolveTemplatesDirectory();
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, finalName);
            File.Copy(sourcePath, dest, overwrite: true);
            Refresh();
            return Path.GetFileName(dest);
        }
        catch { return null; }
    }

    /// <summary>
    /// Duplicates an existing template under a new name.
    /// Returns the new template name, or null on failure.
    /// </summary>
    public string? DuplicateTemplate(string existingName, string newName)
    {
        var src = GetTemplatePath(existingName);
        if (src == null || !File.Exists(src)) return null;
        return ImportTemplate(src, newName);
    }

    /// <summary>Deletes a template from the library.</summary>
    public bool DeleteTemplate(string templateName)
    {
        var path = GetTemplatePath(templateName);
        if (path == null || !File.Exists(path)) return false;
        try { File.Delete(path); Refresh(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Renames an existing template file in place.
    /// Returns the new template name, or null on failure (missing source, blacklisted target, or name collision).
    /// Does not update any MouseProfile references — callers must handle that separately.
    /// </summary>
    public string? RenameTemplate(string existingName, string newName)
    {
        var src = GetTemplatePath(existingName);
        if (src == null || !File.Exists(src)) return null;

        var finalName = newName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? newName
            : newName + ".json";

        if (IsBlacklisted(finalName)) return null;

        var dir  = ResolveTemplatesDirectory();
        var dest = Path.Combine(dir, finalName);

        if (File.Exists(dest) && !string.Equals(dest, src, StringComparison.OrdinalIgnoreCase))
            return null; // avoid silently overwriting a different existing template

        try
        {
            File.Move(src, dest, overwrite: false);
            Refresh();
            return Path.GetFileName(dest);
        }
        catch { return null; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseApp.Models;

namespace MouseApp.Services;

public class TemplateService
{
    private readonly SettingsService _settingsService;

    public event EventHandler? TemplatesChanged;

    public List<string> Templates { get; private set; } = new();

    public TemplateService(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settingsService.SettingsChanged += (_, __) => Refresh();
        Refresh();
    }

    /// <summary>
    /// Returns the effective templates directory: the user-configured path if set,
    /// otherwise a "curves" folder next to the app executable (portable default).
    /// </summary>
    public string ResolveTemplatesDirectory()
    {
        var configured = _settingsService.Settings?.TemplatesDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return Path.Combine(AppContext.BaseDirectory, "curves");
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
                .Where(x => x != null)
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

    public string? GetTemplatePath(string templateName)
    {
        if (string.IsNullOrEmpty(templateName)) return null;
        return Path.Combine(ResolveTemplatesDirectory(), templateName);
    }

    /// <summary>
    /// Imports a .json file into the templates directory, creating the folder if needed.
    /// Returns the name of the imported template, or null on failure.
    /// </summary>
    public string? ImportTemplate(string sourcePath, string templateName)
    {
        try
        {
            var dir = ResolveTemplatesDirectory();
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, templateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? templateName : templateName + ".json");
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
}

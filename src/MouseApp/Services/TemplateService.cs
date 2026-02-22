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

    public void Refresh()
    {
        var dir = _settingsService.Settings?.RawAccelDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
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
        var dir = _settingsService.Settings?.RawAccelDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, templateName);
    }
}

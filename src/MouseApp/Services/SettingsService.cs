using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MouseApp.Models;

namespace MouseApp.Services;

public class SettingsService
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public AppSettings Settings { get; private set; }

    /// <summary>
    /// Raised when settings are saved/updated so other services can react.
    /// </summary>
    public event EventHandler? SettingsChanged;

    public SettingsService()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        // Ensure a default exists so first-run doesn't crash
        try
        {
            if (!File.Exists(_filePath))
            {
                Settings = new AppSettings();
                Save();
            }
            else
            {
                var json = File.ReadAllText(_filePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public Task SaveAsync()
    {
        return Task.Run(() => Save());
    }

    public void Save()
    {
        EventHandler? handler;
        lock (_sync)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Settings ?? new AppSettings(), options);
            File.WriteAllText(_filePath, json);
            handler = SettingsChanged;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // swallow listener exceptions
        }
    }
}

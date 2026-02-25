using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MouseApp.Services;

public class RawAccelDriverProvider
{
    private readonly SettingsService _settingsService;

    public RawAccelDriverProvider(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// Replaces the Raw Accel settings.json with the provided template file (if given) and
    /// optionally updates its sensitivity multiplier entry before invoking writer.exe.
    /// This method is a best-effort no-UI operation and will not throw on non-Windows platforms.
    /// </summary>
    public async Task ApplyTemplateAsync(string? templatePath, double? sensMultiplier)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var rawAccelDir = _settingsService.Settings.RawAccelDirectory ?? string.Empty;
            // writer.exe is always derived from the Raw Accel install directory.
            var writerExePath = string.IsNullOrEmpty(rawAccelDir)
                ? string.Empty
                : Path.Combine(rawAccelDir, "writer.exe");

            if (!Directory.Exists(rawAccelDir))
                Directory.CreateDirectory(rawAccelDir);

            var dest = Path.Combine(rawAccelDir, "settings.json");

            if (!string.IsNullOrEmpty(templatePath) && File.Exists(templatePath))
            {
                // Copy template to destination
                File.Copy(templatePath, dest, true);

                if (sensMultiplier.HasValue)
                {
                    // Target profiles[0].sensitivity — the path Raw Accel actually reads
                    try
                    {
                        var json = await File.ReadAllTextAsync(dest).ConfigureAwait(false);
                        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
                        var profiles = root?["profiles"]?.AsArray();
                        if (profiles != null && profiles.Count > 0)
                        {
                            profiles[0]!["sensitivity"] = System.Text.Json.Nodes.JsonValue.Create(sensMultiplier.Value);
                        }
                        var outJson = root?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? json;
                        await File.WriteAllTextAsync(dest, outJson).ConfigureAwait(false);
                    }
                    catch
                    {
                        // swallow any patch errors and continue
                    }
                }
            }

            // Execute writer.exe silently to apply settings
            if (File.Exists(writerExePath))
            {
                var psi = new ProcessStartInfo(writerExePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(writerExePath) ?? rawAccelDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "settings.json"
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // best-effort: do not crash the app
        }
    }
}

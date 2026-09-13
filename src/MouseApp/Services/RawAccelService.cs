using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MouseApp.Services;

public class RawAccelService
{
    private readonly ProfileService _profileService;

    public RawAccelService(ProfileService profileService)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
    }

    /// <summary>
    /// Replaces the Raw Accel settings.json with the provided settings file (if given) and
    /// optionally updates its sensitivity multiplier entry before invoking writer.exe.
    /// This method is a best-effort no-UI operation and will not throw on non-Windows platforms.
    /// </summary>
    public async Task ApplySettingsFileAsync(string? settingsFilePath, double? sensMultiplier)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var rawAccelDir = ResolveRawAccelDirectory();
            // writer.exe is always derived from the Raw Accel install directory.
            var writerExePath = Path.Combine(rawAccelDir, "writer.exe");

            if (!Directory.Exists(rawAccelDir))
                Directory.CreateDirectory(rawAccelDir);

            var dest = Path.Combine(rawAccelDir, "settings.json");

            // Resolve bare filename → full path under the Raw Accel settings directory.
            // profile.RawAccelSettingsFile stores only the filename, never a rooted path.
            string? resolvedSettingsFilePath = null;
            if (!string.IsNullOrEmpty(settingsFilePath))
            {
                resolvedSettingsFilePath = Path.IsPathRooted(settingsFilePath)
                    ? settingsFilePath
                    : Path.Combine(ResolveSettingsDirectory(), settingsFilePath);
            }

            if (!string.IsNullOrEmpty(resolvedSettingsFilePath) && File.Exists(resolvedSettingsFilePath))
            {
                // Copy the settings file to destination
                File.Copy(resolvedSettingsFilePath, dest, overwrite: true);

                if (sensMultiplier.HasValue)
                {
                    // Target profiles[0]."Sensitivity multiplier" — the path Raw Accel actually reads
                    try
                    {
                        var json = await File.ReadAllTextAsync(dest).ConfigureAwait(false);
                        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
                        var profiles = root?["profiles"]?.AsArray();
                        if (profiles != null && profiles.Count > 0)
                        {
                            profiles[0]!["Sensitivity multiplier"] = System.Text.Json.Nodes.JsonValue.Create(sensMultiplier.Value);
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
            else if (sensMultiplier.HasValue && File.Exists(dest))
            {
                // No settings-file swap — just patch sensitivity into the existing settings.json.
                try
                {
                    var json = await File.ReadAllTextAsync(dest).ConfigureAwait(false);
                    var root = System.Text.Json.Nodes.JsonNode.Parse(json);
                    var profiles = root?["profiles"]?.AsArray();
                    if (profiles != null && profiles.Count > 0)
                    {
                        profiles[0]!["Sensitivity multiplier"] = System.Text.Json.Nodes.JsonValue.Create(sensMultiplier.Value);
                    }
                    var outJson = root?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? json;
                    await File.WriteAllTextAsync(dest, outJson).ConfigureAwait(false);
                }
                catch
                {
                    // swallow any patch errors and continue
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

    /// <summary>
    /// Re-invokes writer.exe against whichever settings.json is already sitting in the Raw
    /// Accel directory, without copying a different file or patching sensitivity first.
    /// <see cref="ApplySettingsFileAsync"/> only copies/patches when given a non-null argument
    /// and always runs writer.exe unconditionally at the end, so passing (null, null) is exactly
    /// "re-apply what's already there" -- used to restore Raw Accel's driver configuration after
    /// a PC restart wipes it (see AppSettings.ApplyRawAccelSettingsOnStartup).
    /// </summary>
    public Task ReapplyCurrentSettingsAsync() => ApplySettingsFileAsync(null, null);

    /// <summary>
    /// Mirrors <c>RawAccelSettingsRepository.ResolveDirectory()</c>.
    /// Returns the configured settings path or the portable default (app folder).
    /// Kept local so <see cref="RawAccelService"/> has no dependency on RawAccelSettingsRepository.
    /// </summary>
    private string ResolveSettingsDirectory()
    {
        var configured = _profileService.Settings?.RawAccelSettingsDirectory;
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : AppContext.BaseDirectory;
    }

    /// <summary>
    /// Returns the configured Raw Accel install directory, or the portable default (app folder)
    /// if unset -- e.g. when Raw Accel's writer.exe is bundled alongside MouseApp itself.
    /// </summary>
    private string ResolveRawAccelDirectory()
    {
        var configured = _profileService.Settings?.RawAccelDirectory;
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : AppContext.BaseDirectory;
    }
}

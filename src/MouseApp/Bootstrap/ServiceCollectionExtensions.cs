using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MouseApp.Services;

namespace MouseApp.Bootstrap;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMouseAppServices(
        this IServiceCollection services,
        Action<MouseServiceOptions>? configure = null)
    {
        var options = new MouseServiceOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Core services
        services.AddSingleton<ProfileService>();

        // Settings service (singleton) provides application source-of-truth for paths and toggles
        services.AddSingleton<SettingsService>();

        // Storage service for folder/file picking
        services.AddSingleton<IStorageService, AvaloniaStorageService>();

        // RawAccelDriverProvider receives SettingsService directly so paths are read dynamically
        services.AddSingleton<RawAccelDriverProvider>(sp =>
        {
            var settingsSvc = sp.GetRequiredService<SettingsService>();
            return new RawAccelDriverProvider(settingsSvc);
        });

        services.AddSingleton<IMouseService, MouseService>();

        // Register IProcessWatcher — the Win32 hook lives in its constructor;
        // call WarmUpMouseAppServices() after building the container to activate it immediately.
        services.AddSingleton<IProcessWatcher>(sp =>
        {
            var profileService = sp.GetRequiredService<ProfileService>();
            var mouseService   = sp.GetRequiredService<IMouseService>();
            return new ProcessWatcher(profileService, profile =>
            {
                Debug.WriteLine($"[MouseApp] applyProfile lambda invoked for '{profile?.ProfileName}'.");
                try
                {
                    mouseService.ApplyProfileAsync(profile)
                        .ContinueWith(
                            t => Debug.WriteLine($"[MouseApp] ApplyProfileAsync faulted: {t.Exception?.Flatten().Message}"),
                            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MouseApp] ApplyProfileAsync threw synchronously: {ex}");
                }
            });
        });

        return services;
    }

    /// <summary>
    /// Eagerly resolves <see cref="IProcessWatcher"/> so the Win32 event hook is
    /// active immediately after the DI container is built. Call this from startup.
    /// </summary>
    public static IServiceProvider WarmUpMouseAppServices(this IServiceProvider sp)
    {
        sp.GetRequiredService<IProcessWatcher>();
        return sp;
    }
}

public class MouseServiceOptions
{
    public string RawAccelDirectory { get; set; } = string.Empty;
    public string WriterExePath      { get; set; } = string.Empty;
}

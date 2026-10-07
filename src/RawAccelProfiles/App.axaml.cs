using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RawAccelProfiles.Services;
using RawAccelProfiles.ViewModels;
using RawAccelProfiles.Views;

namespace RawAccelProfiles;

public partial class App : Application
{
    // Held for app lifetime — prevents the hook delegate from being garbage-collected.
    private IProcessWatcher? _processWatcher;
    private Services.IMouseService? _mouseService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            // Initialize core services
            var profileService    = new Services.ProfileService();

            // Populate profiles + settings from disk before any service/ViewModel touches them.
            profileService.LoadAsync().GetAwaiter().GetResult();

            var storageService    = new Services.AvaloniaStorageService();
            var rawAccelSettingsRepository = new Services.RawAccelSettingsRepository(profileService);
            var windowService     = new Services.AvaloniaWindowService();

            // Create feature view-models
            var profileEditorVm = new ViewModels.ProfileEditorViewModel(rawAccelSettingsRepository, profileService, windowService);

            // Main VM — Settings is opened via windowService as a modal popup (fresh view-model
            // per open), not in-place navigation.
            var mainVm = new ViewModels.MainWindowViewModel(profileEditorVm, profileService, windowService, storageService);

            // Hardware apply service
            var rawAccelProvider = new Services.RawAccelService(profileService);
            _mouseService = new Services.MouseService(profileService, rawAccelProvider);
            var mouseService = _mouseService;

            // Raw Accel's kernel driver holds its config in memory only, so a PC restart wipes
            // it even though Windows Pointer Speed / the registry curve (persisted in the
            // registry) survive. Best-effort fire-and-forget: ReapplyCurrentSettingsAsync already
            // swallows its own errors and is a no-op on non-Windows platforms.
            if (profileService.Settings?.ApplyRawAccelSettingsOnStartup == true)
                _ = rawAccelProvider.ReapplyCurrentSettingsAsync();

            // Self-heal a stale HKCU Run-key entry: this app is meant to be portable (see
            // CLAUDE.md), but the Run key stores an absolute exe path, so moving the app's
            // folder leaves Run at Startup silently pointing at a path that no longer exists.
            // Re-writing it here, unconditionally, on every launch keeps it pointed at wherever
            // the app is actually running from right now, without requiring the user to notice
            // and revisit Settings. ApplyRunAtStartup already no-ops off-Windows and swallows
            // its own registry-access errors.
            if (profileService.Settings?.RunAtStartup == true)
                ViewModels.SettingsViewModel.ApplyRunAtStartup(true);

            // Give the ProfileEditor direct access to the hardware service for manual Apply.
            profileEditorVm.WireMouseService(mouseService);

            // Give Settings access too, for the "revert to Windows default curve" action.
            mainVm.WireMouseService(mouseService);

            // Start the foreground-window watcher.  The same profileService instance is used
            // (singleton by construction) so profile collections are always in sync.
            _processWatcher = new ProcessWatcher(profileService, profile =>
            {
                // Run entirely on the UI thread so ProfileService.Profiles access in
                // CalculateCompensations is thread-safe (ObservableCollection is UI-thread-owned).
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        await mouseService.ApplyProfileAsync(profile).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RawAccelProfiles] ApplyProfileAsync faulted: {ex}");
                    }

                    // Sync sidebar selection — OnExternalProfileActivated already handles UI-thread posting.
                    mainVm.OnExternalProfileActivated(profile);
                });
            });

            // Suppress Last-Man-Standing revert during system logoff/shutdown.
            // ShutdownRequested fires on WM_QUERYENDSESSION — before any other apps are killed.
            var watcher = _processWatcher;
            desktop.ShutdownRequested += (_, _) => watcher.NotifySessionEnding();

            // Always use OnExplicitShutdown so the app stays alive when the window is hidden.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new Views.MainWindow { DataContext = mainVm };

            // Restore the last remembered window size. Position is intentionally not part of
            // this -- it's what goes stale after a monitor is unplugged/reconfigured, whereas
            // size just needs clamping to whatever screen is actually available now. Applied
            // before Show() so there's no visible jump from the XAML default to the saved size.
            var savedWidth = profileService.Settings?.WindowWidth;
            var savedHeight = profileService.Settings?.WindowHeight;
            if (savedWidth is > 0 && savedHeight is > 0)
            {
                var screen = mainWindow.Screens?.Primary;
                var maxWidth = screen != null ? screen.WorkingArea.Width / screen.Scaling : double.MaxValue;
                var maxHeight = screen != null ? screen.WorkingArea.Height / screen.Scaling : double.MaxValue;
                mainWindow.Width = Math.Clamp(savedWidth.Value, mainWindow.MinWidth, maxWidth);
                mainWindow.Height = Math.Clamp(savedHeight.Value, mainWindow.MinHeight, maxHeight);
            }

            // Restore maximized state. Applied after the size above so the underlying Normal
            // bounds are already correct for whenever the window is later un-maximized.
            if (profileService.Settings?.WindowMaximized == true)
                mainWindow.WindowState = WindowState.Maximized;

            // Track Normal <-> Maximized transitions so they persist alongside size. Minimized
            // is deliberately ignored -- never saved, never restored -- so exiting (or clicking
            // Open from tray) while minimized doesn't reopen the window minimized next launch.
            // Maximize/restore is a discrete, infrequent action (not a continuous drag like
            // resize), so this saves immediately rather than going through the debounce timer
            // below; ProfileService.SaveAsync is safe to call concurrently with it either way.
            mainWindow.PropertyChanged += (_, e) =>
            {
                if (e.Property != Window.WindowStateProperty)
                    return;

                if (mainWindow.WindowState == WindowState.Maximized)
                {
                    profileService.Settings!.WindowMaximized = true;
                    _ = profileService.SaveAsync();
                }
                else if (mainWindow.WindowState == WindowState.Normal)
                {
                    profileService.Settings!.WindowMaximized = false;
                    _ = profileService.SaveAsync();
                }
            };

            // Persist window size on resize instead of at exit -- this app can be exited via
            // the tray "Exit" menu item, which calls desktop.Shutdown() directly and never
            // fires mainWindow.Closing (see exitItem.Click below), so an exit-only hook would
            // silently miss that path. Debounced so a drag-resize doesn't write to disk on
            // every intermediate frame; gated to WindowState.Normal (checked both when the
            // resize starts and when the timer fires) so a maximize/restore is never saved as
            // the remembered size -- only the timer-fire check matters in practice, but the
            // early check avoids arming the timer at all for the common maximize case.
            var windowSizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            windowSizeSaveTimer.Tick += (_, _) =>
            {
                windowSizeSaveTimer.Stop();
                if (mainWindow.WindowState != WindowState.Normal)
                    return;

                profileService.Settings!.WindowWidth = mainWindow.Width;
                profileService.Settings!.WindowHeight = mainWindow.Height;
                _ = profileService.SaveAsync();
            };
            mainWindow.Resized += (_, _) =>
            {
                if (mainWindow.WindowState != WindowState.Normal)
                    return;

                windowSizeSaveTimer.Stop();
                windowSizeSaveTimer.Start();
            };

            TrayIcon? tray = null;
            bool shuttingDown = false;

            // desktop.MainWindow is deliberately NOT assigned here. Avalonia's own
            // ClassicDesktopStyleApplicationLifetime.StartCore() unconditionally calls
            // .Show() on whatever is assigned to desktop.MainWindow once Start() runs
            // (after this method returns) -- independent of any show/hide decision this
            // app makes. Assigning it eagerly silently defeats StartMinimized regardless
            // of what the check below decides. It's assigned lazily below, only at the
            // point the window is actually meant to become visible.
            void ShowMainWindow()
            {
                if (desktop.MainWindow != mainWindow)
                    desktop.MainWindow = mainWindow;
                mainWindow.Show();
                mainWindow.Activate();
                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;
            }

            // Hide to tray instead of closing when the user clicks X (if CloseToTray is enabled).
            // When CloseToTray is disabled, closing the window shuts the whole app down.
            mainWindow.Closing += (_, e) =>
            {
                if (shuttingDown) return;

                if (profileService.Settings?.CloseToTray == true)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                }
                else
                {
                    shuttingDown = true;
                    tray!.IsVisible = false;
                    desktop.Shutdown();
                }
            };

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("Open");
            openItem.Click += (_, _) => ShowMainWindow();

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) =>
            {
                shuttingDown = true;
                tray!.IsVisible = false;
                desktop.Shutdown();
            };

            menu.Add(openItem);
            menu.Add(exitItem);

            tray = new TrayIcon
            {
                ToolTipText = "Raw Accel Profiles",
                Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://RawAccelProfiles/Assets/avalonia-logo.ico"))),
                Menu = menu,
                IsVisible = true,
            };

            // Double-click tray icon to restore window.
            tray.Clicked += (_, _) => ShowMainWindow();

            // Dispose tray cleanly when the process exits.
            desktop.Exit += (_, _) => tray.IsVisible = false;

            // StartMinimized: don't show the window on launch.
            if (profileService.Settings?.StartMinimized != true)
                ShowMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
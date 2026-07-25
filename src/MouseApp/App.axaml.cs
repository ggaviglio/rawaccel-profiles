using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MouseApp.Services;
using MouseApp.ViewModels;
using MouseApp.Views;

namespace MouseApp;

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
            var settingsService   = new Services.SettingsService();
            var storageService    = new Services.AvaloniaStorageService();
            var templateService   = new Services.TemplateService(settingsService);
            var navigationService = new Services.NavigationService();
            var profileService    = new Services.ProfileService();
            var windowService     = new Services.AvaloniaWindowService();

            // Populate profiles from disk before any ViewModel touches the collection.
            profileService.LoadAsync().GetAwaiter().GetResult();

            // Create feature view-models
            var settingsVm      = new ViewModels.SettingsViewModel(settingsService, storageService);
            var profileEditorVm = new ViewModels.ProfileEditorViewModel(settingsService, templateService, profileService, windowService);

            // Main VM wired to navigation
            var mainVm = new ViewModels.MainWindowViewModel(navigationService, profileEditorVm, settingsVm, profileService);

            // Hardware apply service
            var rawAccelProvider = new Services.RawAccelService(settingsService);
            _mouseService = new Services.MouseService(profileService, rawAccelProvider);
            var mouseService = _mouseService;

            // Give the ProfileEditor direct access to the hardware service for manual Apply.
            profileEditorVm.WireMouseService(mouseService);

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
                        System.Diagnostics.Debug.WriteLine($"[MouseApp] ApplyProfileAsync faulted: {ex}");
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
            desktop.MainWindow = mainWindow;

            TrayIcon? tray = null;
            bool shuttingDown = false;

            void ShowMainWindow()
            {
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

                if (settingsService.Settings?.CloseToTray == true)
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
                ToolTipText = "MouseApp",
                Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MouseApp/Assets/avalonia-logo.ico"))),
                Menu = menu,
                IsVisible = true,
            };

            // Double-click tray icon to restore window.
            tray.Clicked += (_, _) => ShowMainWindow();

            // Dispose tray cleanly when the process exits.
            desktop.Exit += (_, _) => tray.IsVisible = false;

            // StartMinimized: don't show the window on launch.
            if (settingsService.Settings?.StartMinimized != true)
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
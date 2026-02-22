using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using MouseApp.ViewModels;
using MouseApp.Views;

namespace MouseApp;

public partial class App : Application
{
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
            var settingsService = new Services.SettingsService();
            var storageService = new Services.AvaloniaStorageService();
            var templateService = new Services.TemplateService(settingsService);
            var navigationService = new Services.NavigationService();
            var profileService = new Services.ProfileService();

            // Create feature view-models
            var settingsVm = new ViewModels.SettingsViewModel(settingsService, storageService);
            var profileEditorVm = new ViewModels.ProfileEditorViewModel(templateService, profileService);

            // Main VM wired to navigation
            var mainVm = new ViewModels.MainWindowViewModel(navigationService, profileEditorVm, settingsVm);

            if (settingsService.Settings?.StartMinimized == true)
            {
                // Keep app alive independently of window state so the tray icon works.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                MainWindow? mainWindow = null;
                TrayIcon? tray = null;
                bool shuttingDown = false;

                void ShowMainWindow()
                {
                    if (mainWindow == null)
                    {
                        mainWindow = new Views.MainWindow { DataContext = mainVm };
                        desktop.MainWindow = mainWindow;

                        // Hide to tray instead of closing when the user clicks X.
                        mainWindow.Closing += (_, e) =>
                        {
                            if (!shuttingDown)
                            {
                                e.Cancel = true;
                                mainWindow.Hide();
                            }
                        };
                    }

                    mainWindow.Show();
                    mainWindow.Activate();
                    if (mainWindow.WindowState == WindowState.Minimized)
                        mainWindow.WindowState = WindowState.Normal;
                }

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
                    Icon = new WindowIcon("Assets/avalonia-logo.ico"),
                    Menu = menu,
                    IsVisible = true,
                };

                // Dispose tray cleanly when the process exits.
                desktop.Exit += (_, _) =>
                {
                    tray.IsVisible = false;
                };
            }
            else
            {
                desktop.MainWindow = new Views.MainWindow
                {
                    DataContext = mainVm,
                };
            }
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
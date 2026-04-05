using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XTransferTool.ViewModels;
using XTransferTool.Views;

namespace XTransferTool;

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

            var win = new MainWindow
            {
                // DataContext will be set after services are ready.
            };
            desktop.MainWindow = win;

            _ = StartServicesAndBindAsync(win);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartServicesAndBindAsync(MainWindow win)
    {
        try
        {
            await AppServices.StartAsync();
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"[app] AppServices.StartAsync failed: {ex}");
        }

        Dispatcher.UIThread.Post(() =>
        {
            win.DataContext = new MainWindowViewModel(AppServices.PeerDirectory, AppServices.InboxRepository, AppServices.Settings);
        });
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
using Anp.Atmel.SamBa.FirmwareUpdater.Services;
using Anp.Atmel.SamBa.FirmwareUpdater.Utilities;
using Anp.Atmel.SamBa.FirmwareUpdater.ViewModels;
using System.Reflection;
using System.Windows;


namespace Anp.Atmel.SamBa.FirmwareUpdater
{
    public partial class App : Application
    {
        private MainViewModel _mainViewModel;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var version = GetInformationalVersionOrNull(Assembly.GetEntryAssembly()) ?? "0.0.0";
            var libraryVersion = GetInformationalVersionOrNull(typeof(SamBaDevice).Assembly) ?? "0.0.0";

            // Services (injected into ViewModels)
            var dispatcher = new DispatcherService(Dispatcher);
            var settings = new SettingsService();
            var dialogs = new UserDialogService();
            var toolWindows = new ToolWindowService(dispatcher);
            var process = new ProcessService();
            var clipboard = new ClipboardService();

            _mainViewModel = new MainViewModel(
                appVersion: version,
                libraryVersion: libraryVersion,
                isDesignMode: false,
                settingsService: settings,
                dialogService: dialogs,
                toolWindowService: toolWindows,
                dispatcher: dispatcher,
                processService: process,
                clipboardService: clipboard);

            var window = new MainWindow
            {
                DataContext = _mainViewModel
            };

            window.Closing += (_, __) =>
            {
                try { _mainViewModel?.OnAppClosing(); } catch { /* best-effort */ }
            };

            MainWindow = window;
            window.Show();

            //TitleBarHelper.SetCaptionColor(window, 0x00, 0x78, 0xd4); // Windows blue accent
            TitleBarHelper.SetCaptionColor(window, 0x00, 0x78, 0xa4);

            _mainViewModel.OnAppStarted();
        }

        // Gets the informational version (e.g. "1.2.3-beta") instead of the assembly version
        // that is stable across builds.
        private static string GetInformationalVersionOrNull(Assembly assembly)
        {
            return assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mainViewModel?.Dispose(); } catch { /* best-effort */ }
            _mainViewModel = null;
            base.OnExit(e);
        }
    }
}

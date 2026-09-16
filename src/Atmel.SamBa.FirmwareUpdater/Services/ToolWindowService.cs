using Anp.Atmel.SamBa.FirmwareUpdater.Views;
using System;
using System.Windows;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Services
{
    public sealed class ToolWindowService : IToolWindowService
    {
        private readonly IDispatcherService _dispatcher;
        private TextToolWindow _window;

        public ToolWindowService(IDispatcherService dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public void ShowText(string title, string text)
        {
            _dispatcher.Invoke(() =>
            {
                if (_window == null)
                {
                    _window = new TextToolWindow();
                    _window.Closed += (_, __) => _window = null;
                }

                // Keep owner current in case MainWindow changed/recreated (rare, but can happen in WPF apps).
                _window.Owner = Application.Current?.MainWindow;

                _window.Title = title ?? "Details";
                _window.Text = text ?? string.Empty;

                if (!_window.IsVisible)
                    _window.Show();

                if (_window.WindowState == WindowState.Minimized)
                    _window.WindowState = WindowState.Normal;

                _window.Activate();
            });
        }
    }
}
using System;
using System.Windows;
using System.Windows.Threading;


namespace Anp.Atmel.SamBa.Lite.Services
{
    public sealed class DispatcherService : IDispatcherService
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherService()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public DispatcherService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public void Invoke(Action action)
        {
            if (action == null)
                return;

            if (IsShutdown())
                return;

            try
            {
                // If already on the dispatcher thread, run inline.
                if (_dispatcher.CheckAccess())
                    action();
                else
                    _dispatcher.Invoke(action);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shut down mid-call; ignore best-effort updates (log/progress).
            }
        }

        public void BeginInvoke(Action action)
        {
            if (action == null)
                return;

            if (IsShutdown())
                return;

            try
            {
                // Always use deferred execution, BeginInvoke implies deferred.
                _dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shut down; ignore best-effort updates (log/progress).
            }
        }

        private bool IsShutdown()
        {
            return _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished;
        }
    }
}

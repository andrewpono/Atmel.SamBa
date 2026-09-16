using System;

namespace Anp.Atmel.SamBa.FirmwareUpdater.Services
{
    public interface IDispatcherService
    {
        void Invoke(Action action);

        void BeginInvoke(Action action);
    }
}

using System;

namespace Anp.Atmel.SamBa.Lite.Services
{
    public interface IDispatcherService
    {
        void Invoke(Action action);

        void BeginInvoke(Action action);
    }
}

using System;


namespace Anp.Atmel.SamBa.Events
{
    /// <summary>
    /// Event arguments for a SAM-BA device arrival notification.
    /// </summary>
    public sealed class SamBaDeviceArrivedEventArgs : EventArgs
    {
        /// <summary>
        /// The device that arrived, not opened. Call <see cref="SamBaDevice.Open"/> to use it.
        /// <para>
        /// One instance is handed to every subscriber of <c>SamBaDeviceWatcher.DeviceArrived</c>, so
        /// it cannot be owned by each of them independently: with a single subscriber, dispose it
        /// when done with it; with several, settle on one owner rather than disposing from each
        /// handler. A device that is never opened holds no OS handle, so dropping one undisposed
        /// costs managed memory and nothing else.
        /// </para>
        /// </summary>
        public SamBaDevice Device { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaDeviceArrivedEventArgs"/> class.
        /// </summary>
        /// <param name="device">The unopened device that arrived.</param>
        internal SamBaDeviceArrivedEventArgs(SamBaDevice device)
        {
            Device = device;
        }
    }
}

using System;


namespace Anp.Atmel.SamBa.Events
{
    /// <summary>
    /// Event arguments for a SAM-BA device removal notification.
    /// </summary>
    public sealed class SamBaDeviceRemovedEventArgs : EventArgs
    {
        /// <summary>
        /// System path that uniquely identified the removed device. Never null or empty: a removal is
        /// only raised for a path the OS reported.
        /// </summary>
        public string DevicePath { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaDeviceRemovedEventArgs"/> class.
        /// </summary>
        /// <param name="devicePath">Path of the removed device; never null or empty.</param>
        internal SamBaDeviceRemovedEventArgs(string devicePath)
        {
            DevicePath = devicePath;
        }
    }
}

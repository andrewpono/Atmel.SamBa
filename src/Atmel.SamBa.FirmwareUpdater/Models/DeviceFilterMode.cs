namespace Anp.Atmel.SamBa.FirmwareUpdater.Models
{
    /// <summary>
    /// Which USB serial devices the discovery/watcher should list.
    /// </summary>
    public enum DeviceFilterMode
    {
        /// <summary>Only the Atmel SAM-BA USB CDC device (VID 0x03EB / PID 0x6124). Default.</summary>
        AtmelSamBa = 0,

        /// <summary>Every serial port (no VID/PID filter).</summary>
        AnyDevice,

        /// <summary>A user-supplied VID/PID (either field blank = match any).</summary>
        Custom,
    }
}

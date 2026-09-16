using Anp.Atmel.SamBa.Configuration;
using System;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Models
{
    [Serializable]
    public class AppSettings
    {
        public string FirmwareFilePath { get; set; }

        public bool IsAdvancedMode { get; set; } = true;

        public bool IsWatcherEnabled { get; set; } = true;

        public DeviceFilterMode DeviceFilterMode { get; set; } = DeviceFilterMode.AtmelSamBa;

        public string CustomVendorIdHex { get; set; } = string.Empty;

        public string CustomProductIdHex { get; set; } = string.Empty;

        public bool IsSafeModeEnabled { get; set; } = false;

        public bool AutoUnlock { get; set; } = false;

        public SamBaChipIdentificationMode ChipIdentificationMode { get; set; } =
            SamBaChipIdentificationMode.Auto;

        public SamBaGeometryPrecedence GeometryPrecedence { get; set; } =
            SamBaGeometryPrecedence.Table;

        public int MaxLogEntries { get; set; } = 1000;

        public bool EraseAllFlash { get; set; } = false;

        public bool VerifyAfterWrite { get; set; } = true;

        public bool ResetAfterWrite { get; set; } = true;

        public string FwOffsetHex { get; set; } = "00000000";

        public FlashLockScope FwLockScope { get; set; } = FlashLockScope.None;

        // Security is deliberately not persisted here: it's dangerous enough that each session
        // must opt in again.
        
        public bool FwBootToFlash { get; set; } = true;
    }
}

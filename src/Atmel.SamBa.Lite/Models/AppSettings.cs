using Anp.Atmel.SamBa.Configuration;
using System;


namespace Anp.Atmel.SamBa.Lite.Models
{
    [Serializable]
    public class AppSettings
    {
        public string FirmwareFilePath { get; set; }

        public bool IsWatcherEnabled { get; set; } = true;

        public DeviceFilterMode DeviceFilterMode { get; set; } = DeviceFilterMode.AtmelSamBa;

        public string CustomVendorIdHex { get; set; } = string.Empty;

        public string CustomProductIdHex { get; set; } = string.Empty;

        public bool IsSafeModeEnabled { get; set; } = false;

        public bool AutoUnlock { get; set; } = true;

        public SamBaChipIdentificationMode ChipIdentificationMode { get; set; } =
            SamBaChipIdentificationMode.Auto;

        public SamBaGeometryPrecedence GeometryPrecedence { get; set; } =
            SamBaGeometryPrecedence.Table;

        public int MaxLogEntries { get; set; } = 1000;

        // Update FW options. Security is deliberately not persisted: it's dangerous enough
        // that each session must opt in again.
        public bool FwEraseAll { get; set; } = false;

        public bool FwVerify { get; set; } = true;

        public string FwOffsetHex { get; set; } = "00000000";

        public FlashLockScope FwLockScope { get; set; } = FlashLockScope.None;

        public bool FwBootToFlash { get; set; } = true;

        public bool ResetAfterWrite { get; set; } = true;

        public int SelectedTabIndex { get; set; } = 0;

        public string ReadAddressHex { get; set; } = "00000000";

        public int ReadSizeBytes { get; set; } = 1024;

        public string WriteAddressHex { get; set; } = "00000000";

        public string WriteWordHex { get; set; } = string.Empty;

        public string GoAddressHex { get; set; } = "00000000";

        // Hex view display options.
        public int BytesPerLine { get; set; } = 16;

        public bool SaveWithAddress { get; set; } = true;

        public bool ShowAscii { get; set; } = true;

        // Read tab: when set, Read performs a single 32-bit word access instead of a block
        // read, and shows the 4 resulting bytes in the memory view.
        public bool IsWordRead { get; set; } = false;

        // Memory-view/log split, captured from the GridSplitter's two Star rows in
        // device-independent pixels at the moment of drag. Both are Star, not fixed sizes: the
        // splitter itself resizes a pair of Star rows by re-weighting both (1 star == 1 pixel
        // at drag time), so restoring only one row would either drop the other's share of the
        // split or pin one pane to an absolute height regardless of window size. Either being 0
        // means "unset, use the XAML default" (a fresh install or an older settings file).
        public double MemoryViewHeight { get; set; } = 0;

        public double LogViewHeight { get; set; } = 0;
    }
}

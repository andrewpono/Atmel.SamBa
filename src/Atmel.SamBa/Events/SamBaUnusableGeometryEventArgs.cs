using System;


namespace Anp.Atmel.SamBa.Events
{
    /// <summary>
    /// Raised by <see cref="SamBaDevice.UnusableDeviceGeometryDetected"/> when the flash
    /// controller's own account of its geometry — the EEFC flash descriptor, the NVMCTRL PARAM
    /// register — answered, but failed the sanity gate every geometry must pass before anything
    /// acts on it: sane field ranges plus the divisibility the flash layer's integer arithmetic
    /// relies on. The device table's figures stay in force for this chip either way; this event
    /// exists so a device that answered with something implausible is a report instead of an
    /// invisible one.
    /// </summary>
    public sealed class SamBaUnusableGeometryEventArgs : EventArgs
    {
        internal SamBaUnusableGeometryEventArgs(
            string chipName,
            int reportedPageCount, int reportedPageSize,
            int reportedPlaneCount, int reportedLockRegionCount,
            string reason)
        {
            ChipName = chipName;
            ReportedPageCount = reportedPageCount;
            ReportedPageSize = reportedPageSize;
            ReportedPlaneCount = reportedPlaneCount;
            ReportedLockRegionCount = reportedLockRegionCount;
            Reason = reason;
        }

        /// <summary>Name of the chip the device table identified.</summary>
        public string ChipName { get; }

        /// <summary>Flash pages per the device's own report.</summary>
        public int ReportedPageCount { get; }

        /// <summary>Page size in bytes per the device's own report.</summary>
        public int ReportedPageSize { get; }

        /// <summary>Flash planes per the device's own report.</summary>
        public int ReportedPlaneCount { get; }

        /// <summary>Lock regions per the device's own report.</summary>
        public int ReportedLockRegionCount { get; }

        /// <summary>Which sanity check(s) the reported geometry failed, and why.</summary>
        public string Reason { get; }

        /// <summary>The rejection in one line.</summary>
        public override string ToString()
        {
            return $"{ChipName}: device reported {ReportedPageCount} pages x {ReportedPageSize} B, " +
                $"{ReportedPlaneCount} plane(s), {ReportedLockRegionCount} lock regions — rejected: " +
                $"{Reason}. Using the table.";
        }
    }
}

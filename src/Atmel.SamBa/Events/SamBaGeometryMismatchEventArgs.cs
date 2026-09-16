using System;


namespace Anp.Atmel.SamBa.Events
{
    /// <summary>
    /// Raised by <see cref="SamBaDevice.GeometryMismatchDetected"/> when the flash controller's own
    /// account of its geometry — the EEFC flash descriptor, the NVMCTRL PARAM register —
    /// contradicts the device table's row for the identified chip. Which figures end up in force
    /// depends on the <see cref="SamBaGeometryPrecedence"/> the open was given; this event exists so
    /// the disagreement is a report instead of an invisible one either way, because one of the two
    /// sources is wrong and only hardware in hand can say which.
    /// <para>
    /// How much of the device side is genuinely the device's differs by controller. An EEFC describes
    /// all four figures in its flash descriptor. The NVMCTRL's PARAM register carries only the page
    /// count and page size, so on the SAMC21/D21/R21/L21 and SAMD51/E5x parts
    /// <see cref="DevicePlaneCount"/> and <see cref="DeviceLockRegionCount"/> are not readings at all
    /// — see those two — and a mismatch there can only ever be about the pages.
    /// </para>
    /// </summary>
    public sealed class SamBaGeometryMismatchEventArgs : EventArgs
    {
        internal SamBaGeometryMismatchEventArgs(
            string chipName,
            int tablePageCount, int devicePageCount,
            int tablePageSize, int devicePageSize,
            int tablePlaneCount, int devicePlaneCount,
            int tableLockRegionCount, int deviceLockRegionCount)
        {
            ChipName = chipName;
            TablePageCount = tablePageCount;
            DevicePageCount = devicePageCount;
            TablePageSize = tablePageSize;
            DevicePageSize = devicePageSize;
            TablePlaneCount = tablePlaneCount;
            DevicePlaneCount = devicePlaneCount;
            TableLockRegionCount = tableLockRegionCount;
            DeviceLockRegionCount = deviceLockRegionCount;
        }

        /// <summary>Name of the chip the device table identified.</summary>
        public string ChipName { get; }

        /// <summary>
        /// Flash pages per the device table. In force unless the open used
        /// <see cref="SamBaGeometryPrecedence.Device"/>, in which case <see cref="DevicePageCount"/>
        /// is.
        /// </summary>
        public int TablePageCount { get; }

        /// <summary>Flash pages per the device's own report.</summary>
        public int DevicePageCount { get; }

        /// <summary>
        /// Page size in bytes per the device table. In force unless the open used
        /// <see cref="SamBaGeometryPrecedence.Device"/>, in which case <see cref="DevicePageSize"/>
        /// is.
        /// </summary>
        public int TablePageSize { get; }

        /// <summary>Page size in bytes per the device's own report.</summary>
        public int DevicePageSize { get; }

        /// <summary>
        /// Flash planes per the device table. In force unless the open used
        /// <see cref="SamBaGeometryPrecedence.Device"/>, in which case <see cref="DevicePlaneCount"/>
        /// is.
        /// </summary>
        public int TablePlaneCount { get; }

        /// <summary>
        /// Flash planes per the device's own report — on an NVMCTRL part, 1 by construction rather
        /// than a reading: those parts have one flash controller and PARAM carries no such field.
        /// </summary>
        public int DevicePlaneCount { get; }

        /// <summary>
        /// Lock regions per the device table. In force unless the open used
        /// <see cref="SamBaGeometryPrecedence.Device"/>, in which case
        /// <see cref="DeviceLockRegionCount"/> is.
        /// </summary>
        public int TableLockRegionCount { get; }

        /// <summary>
        /// Lock regions per the device's own report — on an NVMCTRL part, a copy of
        /// <see cref="TableLockRegionCount"/> rather than a reading: the count is architectural per
        /// generation (16 on the SAMD21 generation, 32 on the SAMD51) and PARAM does not carry it, so
        /// there is nothing for the two figures to disagree about.
        /// </summary>
        public int DeviceLockRegionCount { get; }

        /// <summary>
        /// The disagreement in one line, table figures first. Silent on which side is in force —
        /// that depends on the open's <see cref="SamBaGeometryPrecedence"/>, which the caller
        /// already states alongside this.
        /// </summary>
        public override string ToString()
        {
            return $"{ChipName}: device table says {TablePageCount} pages x {TablePageSize} B, " +
                $"{TablePlaneCount} plane(s), {TableLockRegionCount} lock regions; the device reports " +
                $"{DevicePageCount} x {DevicePageSize} B, {DevicePlaneCount} plane(s), " +
                $"{DeviceLockRegionCount} lock regions.";
        }
    }
}

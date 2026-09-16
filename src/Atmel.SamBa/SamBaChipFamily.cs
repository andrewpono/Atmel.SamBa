namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Atmel/Microchip SAM chip family, as identified by the SAM-BA chip probe.
    /// </summary>
    /// <remarks>
    /// Grouped by part-number series in flash-controller order, alphabetically within each group, so
    /// that a member's place in the file is predictable and neighbours share a controller. The
    /// numbering is therefore incidental — nothing stores or transmits it, and
    /// <c>SupportedChips.Get()</c> sorts by it only to list a catalog generation by generation.
    /// </remarks>
    public enum SamBaChipFamily
    {
        /// <summary>
        /// Family not in the device table, and not resolvable by family either. Reported for a part
        /// that did identify itself — it answered through CHIPID or the DSU — but matched no row and
        /// no family's CHIPID (ARCH/EPROC) or DSU (DID upper word) identification pattern, so it runs
        /// on the geometry its own flash controller reported: it erases, writes and verifies like any
        /// other. <c>DeviceResetter</c> routes by family and has no family-specific route for this one,
        /// but a part whose initial probe confirmed Cortex-M (it read a CPUID) still resets through
        /// the core's own architectural AIRCR — every Cortex-M carries it at the same fixed address
        /// regardless of family. Only a part identified on the legacy CHIPID-only branch, which never
        /// reads a CPUID, is left with no route at all: <see cref="SamBaDevice.Reset"/> reports that
        /// case, and <c>UpdateFirmware</c> completes without resetting. Also the value of a field no
        /// probe has filled in.
        /// </summary>
        Unknown = 0,

        //
        // SAM7 — legacy EFC, apart from the SAM7L
        //

        /// <summary>
        /// AT91SAM7L series (EEFC flash controller at the legacy 0xFFFFFF60 address; the only SAM7
        /// that is not EFC). Never reported: its ROM SAM-BA answers on the DBGU alone, so no
        /// device-table row matches these parts and identification rejects them as unsupported.
        /// </summary>
        Sam7L,
        /// <summary>AT91SAM7S series (legacy EFC flash controller).</summary>
        Sam7S,
        /// <summary>AT91SAM7SE series (legacy EFC flash controller).</summary>
        Sam7Se,
        /// <summary>AT91SAM7X series (legacy EFC flash controller).</summary>
        Sam7X,
        /// <summary>AT91SAM7XC series (legacy EFC flash controller).</summary>
        Sam7Xc,

        //
        // SAM3 — EEFC
        //

        /// <summary>ATSAM3A series (EEFC flash controller).</summary>
        Sam3A,
        /// <summary>
        /// ATSAM3N series (EEFC flash controller). Never reported: its ROM SAM-BA answers on UART0
        /// alone, so no device-table row matches these parts and identification rejects them as
        /// unsupported.
        /// </summary>
        Sam3N,
        /// <summary>ATSAM3S series (EEFC flash controller).</summary>
        Sam3S,
        /// <summary>ATSAM3U series (EEFC flash controller).</summary>
        Sam3U,
        /// <summary>ATSAM3X series (EEFC flash controller), e.g. Arduino Due.</summary>
        Sam3X,

        //
        // SAM4 / SAM9 — EEFC
        //

        /// <summary>ATSAM4E series (EEFC flash controller).</summary>
        Sam4E,
        /// <summary>ATSAM4S series (EEFC flash controller).</summary>
        Sam4S,
        /// <summary>AT91SAM9XE series (EEFC flash controller).</summary>
        Sam9Xe,

        //
        // SAMx7x — EEFC
        //

        /// <summary>ATSAME70 series (EEFC flash controller).</summary>
        SamE70,
        /// <summary>ATSAMS70 series (EEFC flash controller).</summary>
        SamS70,
        /// <summary>ATSAMV70 series (EEFC flash controller).</summary>
        SamV70,
        /// <summary>ATSAMV71 series (EEFC flash controller).</summary>
        SamV71,

        //
        // NVMCTRL, row erase
        //

        /// <summary>ATSAMC21 series (NVMCTRL flash controller; shares the SAMD21 controller).</summary>
        SamC21,
        /// <summary>ATSAMD21 series (NVMCTRL flash controller), e.g. Arduino Zero.</summary>
        SamD21,
        /// <summary>ATSAML21 series (NVMCTRL flash controller).</summary>
        SamL21,
        /// <summary>ATSAMR21 series (NVMCTRL flash controller).</summary>
        SamR21,

        //
        // NVMCTRL, block erase
        //

        /// <summary>ATSAMD51 series (NVMCTRL flash controller).</summary>
        SamD51,
        /// <summary>ATSAME51 series (NVMCTRL flash controller).</summary>
        SamE51,
        /// <summary>ATSAME53 series (NVMCTRL flash controller).</summary>
        SamE53,
        /// <summary>ATSAME54 series (NVMCTRL flash controller).</summary>
        SamE54
    }
}

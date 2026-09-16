namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Which memory a SAM part fetches its startup code from, as read and written through the SAM-BA
    /// monitor. A property of the chip rather than of the connection to it, so it is named alongside
    /// <see cref="SamBaChipFamily"/> and <see cref="SamBaChipInfo"/> rather than with the
    /// <c>SamBaDevice</c> types.
    /// </summary>
    /// <remarks>
    /// Every part boots flash unless it has a boot-mode GPNVM bit pointing elsewhere — see
    /// <see cref="SamBaChipInfo.CanSelectBootSource"/> for which parts those are. The values match the
    /// bit's own encoding, though nothing in this library relies on that.
    /// </remarks>
    public enum SamBaChipBootSource
    {
        /// <summary>
        /// The on-chip ROM. Selected by clearing the boot-mode GPNVM bit, so it is a boot source only on
        /// a part that has one: the bootable legacy EFC parts (SAM7X/SE/XC) and every EEFC part. On
        /// those the bit is sticky, which is why a firmware update has to point the part back at flash
        /// before the new firmware can run.
        /// <para>
        /// Holding the monitor in ROM and being able to boot it are separate things. A small SAM7S has
        /// SAM-BA in ROM and still cannot start from there — an erase copies it into flash, from where
        /// it relocates itself to RAM to run — so the ROM is not a boot source on those parts even
        /// though the monitor is in it. The NVMCTRL parts have no such ROM at all; their bootloader is
        /// flash-resident.
        /// </para>
        /// </summary>
        Rom = 0,

        /// <summary>
        /// Internal flash. The fixed source on the parts with no boot-mode bit — the small SAM7S
        /// variants, where SAM-BA is copied into flash by an erase and relocates itself to RAM to run,
        /// and every NVMCTRL part (SAMC21/D21/R21/L21, SAMD51/E5x), whose bootloader is flash-resident.
        /// </summary>
        Flash = 1,
    }
}

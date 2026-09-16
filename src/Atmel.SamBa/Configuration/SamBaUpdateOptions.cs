namespace Anp.Atmel.SamBa.Configuration
{
    /// <summary>
    /// Options for <see cref="SamBaDevice.UpdateFirmware(byte[], SamBaUpdateOptions)"/>.
    /// The defaults write (auto-erasing page by page as needed), verify, set boot-from-flash and
    /// reset — the typical firmware-update recipe — without bulk-erasing, unlocking, locking or
    /// touching the security bit, which each need to be requested explicitly.
    /// </summary>
    public sealed class SamBaUpdateOptions
    {
        /// <summary>
        /// Erase the whole of flash (from <see cref="Offset"/> to the end) in one pass before
        /// programming. Default false.
        /// When false, each write block is erased as it is written instead (auto-erase) — a write
        /// block, not a page: on NVMCTRL parts one erase clears 4 or 16 pages.
        /// <para>
        /// False is unusable for most images on EEFC parts (SAM3/SAM4/SAMx7x), where the
        /// erase-and-write-page command only erases within the two 8 KB sectors at the bottom of
        /// flash: anything reaching past 16 KB is rejected with
        /// <see cref="Exceptions.SamBaFlashCommandException"/>. Leave this true there.
        /// </para>
        /// </summary>
        public bool BulkErase { get; set; }

        /// <summary>Verify flash content against the image after programming. Default true.</summary>
        public bool Verify { get; set; } = true;

        /// <summary>
        /// Unlock any locked flash regions before erasing/programming. Default false.
        /// A device may arrive with locked regions (e.g. a protected bootloader area, or a
        /// part locked by a previous run); without this the erase/write would fail with a
        /// lock error. Only regions that are actually locked are touched; the clear is skipped
        /// entirely when nothing is locked.
        /// </summary>
        public bool UnlockBeforeWrite { get; set; }

        /// <summary>
        /// Byte offset into flash where the image starts; must be aligned to the chip's write block
        /// (<see cref="SamBaChipInfo.WriteBlockSize"/> — the page size except on NVMCTRL parts).
        /// Default 0.
        /// Use e.g. 0x2000 on SAMD21 boards to preserve the bootloader region.
        /// <para>
        /// <see cref="BulkErase"/> adds its own, sometimes coarser, requirement, because a partial
        /// erase has to start on an erase boundary: EEFC parts erase eight pages at a time (2 KB
        /// where a page is 256 B), and legacy EFC parts (SAM7) can only erase the whole flash, so they
        /// reject any non-zero offset. NVMCTRL parts erase exactly one write block, so the alignment
        /// above is the only one that applies there.
        /// </para>
        /// </summary>
        public uint Offset { get; set; }

        /// <summary>
        /// Point the boot source at flash after programming, by setting the boot-mode GPNVM bit.
        /// Default true, and worth leaving that way: the bit is sticky and survives the erase, so on a
        /// part that has one a firmware update with this off comes back up in the SAM-BA monitor
        /// instead of running what was just written.
        /// <para>
        /// A step to perform or skip, like <see cref="BulkErase"/> and <see cref="SetSecurity"/> —
        /// false leaves the boot configuration alone rather than pointing the part at the ROM. Use
        /// <see cref="SamBaDevice.SetBootSource"/> for that, which is rarely what anyone wants.
        /// Silently skipped where the source is fixed (every NVMCTRL part, the small SAM7S variants,
        /// and a part identified only by family fallback), since those are fixed at flash already —
        /// <see cref="SamBaChipInfo.CanSelectBootSource"/> says which.
        /// </para>
        /// </summary>
        public bool SetBootToFlash { get; set; } = true;

        /// <summary>
        /// Which lock regions to lock after programming. Default <see cref="FlashLockScope.None"/>,
        /// which leaves the lock state alone — it is not a request to unlock.
        /// <see cref="UnlockBeforeWrite"/> is what clears existing locks, and it runs before
        /// programming rather than after; it is deliberately broader than
        /// <see cref="FlashLockScope.Written"/>, covering every region, because
        /// <see cref="BulkErase"/> blanks the whole flash and cannot leave a locked region standing
        /// anywhere.
        /// <para>
        /// A lock region is the granularity the hardware protects at, so with
        /// <see cref="FlashLockScope.Written"/> an image whose tail lands part-way into a region locks
        /// the rest of that region too — unavoidable, and worth knowing where something else lives
        /// just past the image.
        /// <see cref="SamBaDevice.SetLockRegions(System.Collections.Generic.IReadOnlyList{int}, bool)"/>
        /// is the way to name a region subset that does not follow from what was written.
        /// </para>
        /// </summary>
        public FlashLockScope Lock { get; set; }

        /// <summary>
        /// What to do to the security bit after everything else. Default <see cref="SecurityAction.Leave"/>.
        /// WARNING: <see cref="SecurityAction.SetPermanently"/> is irreversible — the device can only
        /// be recovered with the erase pin, and further SAM-BA access is blocked.
        /// </summary>
        public SecurityAction SetSecurity { get; set; }

        /// <summary>
        /// Reset the device after programming (RSTC/AIRCR write). Default true. Best-effort: a
        /// part with no known reset route is reported through
        /// <see cref="SamBaDevice.ProgressChanged"/> rather than thrown, since the update itself has
        /// already succeeded by this point, and a transport failure while writing the reset command
        /// is the expected symptom of the reset landing rather than a failure. The USB port drops on
        /// reset and the device is closed either way.
        /// </summary>
        public bool Reset { get; set; } = true;

        /// <summary>
        /// Creates an independent copy. Every property is a value type, so the copy shares nothing
        /// with the original — <see cref="SamBaDevice.UpdateFirmware(byte[], SamBaUpdateOptions)"/>
        /// depends on that to snapshot the options it was handed and read them back consistently
        /// while it runs. Keep it true of anything added here.
        /// </summary>
        public SamBaUpdateOptions Clone() => (SamBaUpdateOptions)MemberwiseClone();

        /// <summary>
        /// Every option and its value, for logging an update before it runs. Diagnostic text; the
        /// format is not stable and nothing parses it back.
        /// </summary>
        public override string ToString()
        {
            return $"{nameof(BulkErase)}: {BulkErase}, {nameof(Verify)}: {Verify}, " +
                $"{nameof(UnlockBeforeWrite)}: {UnlockBeforeWrite}, {nameof(Offset)}: 0x{Offset:X}, " +
                $"{nameof(SetBootToFlash)}: {SetBootToFlash}, {nameof(Lock)}: {Lock}, " +
                $"{nameof(SetSecurity)}: {SetSecurity}, {nameof(Reset)}: {Reset}";
        }
    }
}

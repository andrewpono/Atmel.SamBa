using Anp.Atmel.SamBa.Chips;
using System.Collections.Generic;


namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Identification and flash geometry of a SAM chip — either a connected device (resolved
    /// during <see cref="SamBaDevice.Open"/>) or a supported-device table entry returned by
    /// <see cref="SupportedChips.Get"/>.
    /// <para>
    /// The geometry applies to both. The four runtime identification words —
    /// <see cref="ChipId"/>, <see cref="ExtendedChipId"/>, <see cref="DeviceId"/> and
    /// <see cref="CpuId"/> — are all null on a table entry, which no probe produced, and on a
    /// connected device carry a value only for the registers the probe actually read. Null there
    /// never means zero: a word that answered zero reports 0, and one the probe never reached
    /// reports null.
    /// </para>
    /// </summary>
    public sealed class SamBaChipInfo
    {
        /// <summary>
        /// A probed device: geometry from <paramref name="record"/> plus whichever identification
        /// words the probe read. The record is a separate parameter rather than
        /// <c>identity.Record</c> because the two can legitimately differ — a part identified by
        /// family fallback carries a zero-geometry provisional record in its identity, completed
        /// from the device's own account only inside <c>FlashController.Create</c>. Chains to the
        /// table-entry constructor so that geometry is copied in one place only — a property added
        /// to <see cref="ChipRecord"/> and wired into just one of two constructors would otherwise
        /// read correctly from a device and as zero from <see cref="SupportedChips.Get"/>, or the
        /// reverse.
        /// </summary>
        internal SamBaChipInfo(ChipRecord record, ChipIdentity identity)
            : this(record)
        {
            ChipId = identity.ChipId;
            ExtendedChipId = identity.ExtChipId;
            DeviceId = identity.DeviceId;
            CpuId = identity.CpuId;
        }

        /// <summary>
        /// A supported-device table entry: geometry only, with all four identification words left null
        /// because no probe produced it.
        /// </summary>
        internal SamBaChipInfo(ChipRecord record)
        {
            Name = record.Name;
            Family = record.Family;
            FlashAddress = record.FlashAddress;
            PageCount = record.PageCount;
            PageSize = record.PageSize;
            PlaneCount = record.PlaneCount;
            LockRegionCount = record.LockRegionCount;
            WriteBlockSize = record.WriteBlockSize;
            HasUniqueId = record.UniqueIdWords != 0;
            CanSelectBootSource = record.BootGpnvmBitIndex.HasValue;
        }

        /// <summary>Chip name, e.g. "ATSAM3X8". Some table entries cover several package variants.</summary>
        public string Name { get; }

        /// <summary>
        /// Chip family. <see cref="SamBaChipFamily.Unknown"/> on a part that identified itself but
        /// matched no device-table row: it programs from the geometry its own flash controller
        /// reported, and cannot be reset from here. See that member for what it costs.
        /// </summary>
        public SamBaChipFamily Family { get; }

        /// <summary>
        /// CHIPID identification word (CIDR) as read, unmasked; null on a table entry and on the
        /// DSU-identified parts (SAMC21/D21/R21/L21, SAMD51/E5x), which answer through the DSU
        /// instead.
        /// </summary>
        public uint? ChipId { get; }

        /// <summary>
        /// CHIPID extension word (EXID) as read: 0 on the many Cortex-M CHIPID parts that carry no
        /// extension word — only the four SAM4E rows are told apart by it.
        /// <para>
        /// Null on a table entry, on a DSU-identified part, and on the legacy SAM7 and SAM9XE parts,
        /// whose probe reads the CIDR alone because no legacy row needs a tiebreaker — so there
        /// <see cref="ChipId"/> carries a value while this stays null.
        /// </para>
        /// </summary>
        public uint? ExtendedChipId { get; }

        /// <summary>
        /// DSU device identification word (DID) as read, unmasked; null on CHIPID-identified parts and
        /// on a table entry.
        /// </summary>
        public uint? DeviceId { get; }

        /// <summary>
        /// Whole ARM CPUID register as read, including the implementer, variant and revision fields
        /// the probe itself ignores; null on the ARM7/ARM9 parts, which have no such register, and on
        /// a table entry.
        /// </summary>
        public uint? CpuId { get; }

        /// <summary>Flash base address (0 on NVMCTRL parts — flash starts at address 0).</summary>
        public uint FlashAddress { get; }

        /// <summary>Total number of flash pages.</summary>
        public int PageCount { get; }

        /// <summary>Flash page size in bytes.</summary>
        public int PageSize { get; }

        /// <summary>
        /// Number of flash controller instances (1 or 2) — how many register blocks, each numbering
        /// its pages and lock regions from its own base, the flash layer has to drive.
        /// <para>
        /// Not the number of banks a datasheet calls planes. The ATSAM3SD8 is where the two part
        /// company: it has two banks, both behind a single controller and selected by page number, so
        /// this reads 1 while the part's own flash descriptor answers 2.
        /// </para>
        /// </summary>
        public int PlaneCount { get; }

        /// <summary>
        /// Number of lock regions. Each covers <see cref="PageCount"/> / this many pages, which is the
        /// granularity a lock or unlock actually applies at.
        /// </summary>
        public int LockRegionCount { get; }

        /// <summary>
        /// Bytes in the smallest span that can be written without disturbing what surrounds it, and
        /// therefore the granularity a write offset must be aligned to.
        /// <para>
        /// Equal to <see cref="PageSize"/> on the EFC and EEFC parts, where a page is erased and
        /// programmed together. Larger on the NVMCTRL parts (SAMC21/D21/R21/L21, SAMD51/E5x), whose
        /// smallest erase covers several pages: four on the SAMD21 generation, sixteen on the
        /// SAMD51. Writing less than a block there means erasing the whole block, so the library reads
        /// the block back and merges — which costs a read, and is why an aligned write is the cheaper
        /// one.
        /// </para>
        /// </summary>
        public int WriteBlockSize { get; }

        /// <summary>
        /// Number of write blocks in flash (<see cref="FlashSize"/> / <see cref="WriteBlockSize"/>);
        /// 0 when the geometry is empty, so that reading a summary off an unpopulated instance cannot
        /// divide by zero. Nothing published from <see cref="SamBaDevice.Open"/> or
        /// <see cref="SupportedChips.Get"/> is empty: a device's record is completed before it is
        /// handed out, and no table row carries a zero page size.
        /// </summary>
        public long WriteBlockCount => WriteBlockSize == 0 ? 0 : FlashSize / WriteBlockSize;

        /// <summary>Flash size in bytes (<see cref="PageCount"/> × <see cref="PageSize"/>).</summary>
        public long FlashSize => (long)PageCount * PageSize;

        /// <summary>
        /// Whether the part carries a factory-programmed unique identifier for
        /// <see cref="SamBaDevice.GetUniqueId"/> to read. A property of the part rather than of the
        /// silicon in front of you, so it answers on a table entry too and costs nothing to ask —
        /// which is the point: a caller can offer the read, or not, without performing one.
        /// <para>
        /// The identifier itself is deliberately not here. It is the one thing that differs between
        /// two boards carrying the same chip, whereas everything else on this class is the same for
        /// every part of that type; and reading it is not free (see
        /// <see cref="SamBaDevice.GetUniqueId"/>).
        /// </para>
        /// <para>
        /// False on legacy EFC, whose controller has no unique-id command at all, and on the SAM7L and
        /// SAM9XE, whose EEFC omits it. True on the NVMCTRL parts (SAMC21/D21/R21/L21, SAMD51/E5x),
        /// which have no such command either but do not need one: they publish the same 128 bits as a
        /// serial number at fixed read-only addresses, which is a cheaper read than the EEFC's.
        /// </para>
        /// <para>
        /// False on a part identified only by fallback, whatever its controller: no document says what
        /// such a part answers with, and this library does not guess at a value it would then report as
        /// the device's identity.
        /// </para>
        /// </summary>
        public bool HasUniqueId { get; }

        /// <summary>
        /// Whether the part's boot source can be changed — whether it has a boot-mode GPNVM bit. True on
        /// the bootable legacy EFC parts (SAM7X/SE/XC) and every EEFC part; false where the source is
        /// fixed, and every part here whose source is fixed is fixed at
        /// <see cref="SamBaChipBootSource.Flash"/>. So this says whether the source can be *moved*, never
        /// whether flash is reachable.
        /// <para>
        /// A property of the part rather than of the silicon in front of you, answered on a table entry
        /// too and free to ask, for the reason <see cref="HasUniqueId"/> is: a UI can offer the control,
        /// or leave it out, without touching the device. False also on a part identified only by family
        /// fallback, whose provisional record carries no bit index —
        /// <see cref="SamBaDevice.SetBootSource"/> on such a part therefore accepts
        /// <see cref="SamBaChipBootSource.Flash"/> and refuses the ROM, both without issuing a command.
        /// </para>
        /// </summary>
        public bool CanSelectBootSource { get; }

        /// <summary>
        /// Human-readable summary of the chip and its flash geometry, followed by whichever
        /// identification words apply. A table entry from <see cref="SupportedChips.Get"/> has none,
        /// so it summarises geometry alone; a probed device appends the registers that answered, which
        /// is what makes this worth pasting into a report about a part identified wrongly.
        /// </summary>
        public override string ToString()
        {
            string block = WriteBlockSize == PageSize ? "" : $" ({WriteBlockSize} B write block)";
            string text = $"{Name} ({Family}): {FlashSize / 1024} KB flash at 0x{FlashAddress:X8}, " +
                $"{PageCount} pages x {PageSize} B{block}, {PlaneCount} plane(s), {LockRegionCount} lock regions";

            string ids = string.Join(", ", IdentificationWords());
            return ids.Length == 0 ? text : $"{text} [{ids}]";
        }

        /// <summary>
        /// The identification words that were read, formatted for <see cref="ToString"/>. Absent ones
        /// are skipped rather than printed as zero, since on any given part most of them are absent:
        /// a chip answers through CHIPID or through the DSU, not both.
        /// </summary>
        private IEnumerable<string> IdentificationWords()
        {
            if (ChipId.HasValue)
                yield return $"CHIPID=0x{ChipId.Value:X8}";
            if (ExtendedChipId.HasValue)
                yield return $"EXID=0x{ExtendedChipId.Value:X8}";
            if (DeviceId.HasValue)
                yield return $"DSU DID=0x{DeviceId.Value:X8}";
            if (CpuId.HasValue)
                yield return $"CPUID=0x{CpuId.Value:X8}";
        }
    }
}

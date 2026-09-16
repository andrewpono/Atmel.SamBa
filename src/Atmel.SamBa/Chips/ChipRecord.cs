using System;


namespace Anp.Atmel.SamBa.Chips
{
    /// <summary>
    /// Which identification register a <see cref="ChipRecord.Key"/> matches against. The probe
    /// reads exactly one of these registers (chosen from the CPU type), so this also selects
    /// which subset of table rows is even eligible to match.
    /// </summary>
    internal enum ChipKeyKind : byte
    {
        /// <summary>
        /// CHIPID CIDR value masked with <see cref="ChipTable.ChipIdKeyMask"/>
        /// (SAM7/SAM3/SAM4/SAM9/SAMx7x).
        /// </summary>
        ChipId,

        /// <summary>
        /// DSU DID value masked with <see cref="ChipTable.DeviceIdKeyMask"/>
        /// (SAMD/SAMR/SAML/SAMC21/SAME5x).
        /// </summary>
        DeviceId,
    }

    /// <summary>Flash controller wired to a chip's embedded flash.</summary>
    internal enum FlashControllerKind : byte
    {
        /// <summary>Legacy MC/EFC controller at 0xFFFFFF60 (SAM7S/SE/X/XC).</summary>
        Efc,

        /// <summary>Enhanced EFC; register base per chip (SAM3/SAM4/SAM9XE/SAM7L/SAMx7x).</summary>
        Eefc,

        /// <summary>NVMCTRL, row-erase generation (SAMD21/R21/L21).</summary>
        D2xNvm,

        /// <summary>NVMCTRL, block-erase generation (SAMD51/E5x).</summary>
        D5xNvm,
    }

    /// <summary>
    /// One row of the supported-device table: identification key plus flash geometry.
    /// There are no applet-staging SRAM addresses (a <c>stack</c> / <c>user</c> region) —
    /// this library uploads no applet and stages nothing through SRAM.
    /// </summary>
    internal readonly struct ChipRecord
    {
        /// <summary>
        /// Primary identification match value, pre-masked to drop variant/revision bits that
        /// don't affect flash geometry — with <see cref="ChipTable.ChipIdKeyMask"/> or
        /// <see cref="ChipTable.DeviceIdKeyMask"/>, per <see cref="KeyKind"/>. A row matches when
        /// its <see cref="KeyKind"/> equals the probed register kind and this equals the
        /// identically-masked probe value.
        /// </summary>
        public readonly uint Key;

        /// <summary>
        /// Secondary tiebreaker (full CHIPID EXID) for the one case where a single masked
        /// <see cref="Key"/> covers more than one part — the SAM4E rows, which share CHIPID
        /// 0x23CC0CE0 and are told apart by EXID. <c>0</c> means "don't care" (all other rows);
        /// when non-zero it must also equal the probed EXID for the row to match.
        /// </summary>
        public readonly uint ExtendedKey;

        /// <summary>Which identification register <see cref="Key"/> is compared against.</summary>
        public readonly ChipKeyKind KeyKind;

        /// <summary>Chip family.</summary>
        public readonly SamBaChipFamily Family;

        /// <summary>Chip name, e.g. "ATSAM3X8".</summary>
        public readonly string Name;

        /// <summary>Flash controller implementation for this chip.</summary>
        public readonly FlashControllerKind ControllerKind;

        /// <summary>Flash base address (0 for NVMCTRL parts — flash starts at address 0).</summary>
        public readonly uint FlashAddress;

        /// <summary>Number of flash pages.</summary>
        public readonly int PageCount;

        /// <summary>Flash page size in bytes.</summary>
        public readonly int PageSize;

        /// <summary>
        /// Number of flash controller instances (1 or 2) — how many register blocks, each numbering
        /// pages and lock regions from its own base, the flash layer must drive.
        /// <para>
        /// Not the same thing as the number of banks a datasheet calls planes, and not the same
        /// thing as the descriptor's FL_NB_PLANE. The ATSAM3SD8 is where the two part company: it is
        /// a dual-bank part with a single EEFC, whose second bank is addressed by page number rather
        /// than through a second register block, so it is 1 here while its descriptor answers 2. A
        /// row is 2 only when the part really has a second controller at
        /// <see cref="FlashControllerBaseAddress"/> plus the family's plane stride — SAM7x512, SAM3U4, SAM3X8,
        /// SAM3A8, SAM3X4, SAM3A4, SAM4SD16 and SAM4SD32.
        /// </para>
        /// </summary>
        public readonly int PlaneCount;

        /// <summary>Number of lock regions.</summary>
        public readonly int LockRegionCount;

        /// <summary>EEFC register-block base address (e.g. 0x400E0A00); 0 for non-EEFC controllers.</summary>
        public readonly uint FlashControllerBaseAddress;

        /// <summary>
        /// Index of the boot-mode GPNVM bit — the general-purpose NVM bit that selects boot
        /// from flash (set) versus the SAM-BA ROM (clear) — or <c>null</c> when the part's boot
        /// source is fixed and cannot be switched this way. Standard EEFC parts use GPNVM1
        /// (GPNVM0 is the security bit); the SAM9XE uses GPNVM3 because its lower GPNVM bits
        /// configure the brown-out detector; legacy EFC parts that can boot from flash use
        /// GPNVM2. <c>null</c> on fixed-source parts (small SAM7S, all NVMCTRL parts) — which is
        /// exactly what makes <see cref="Flash.FlashController.CanSelectBootSource"/> false for them,
        /// and on every one of those the fixed source is flash.
        /// (0 is a real index — GPNVM0, the security bit — so it can't double as "none".)
        /// </summary>
        public readonly int? BootGpnvmBitIndex;

        /// <summary>
        /// Number of 32-bit words in the readable factory unique id — 4 on every EEFC part whose
        /// controller implements the command and on every NVMCTRL part, which publishes the same
        /// 128 bits at fixed read-only addresses instead. 0 where there is nothing to read: legacy
        /// EFC, the SAM7L and SAM9XE EEFCs that omit the command, and a provisional record for a
        /// part no row matches, whose id layout no document settles.
        /// <para>
        /// A count, not a location: where the words are read from is the flash controller's business
        /// (the flash window under STUI on the EEFC, the serial-number addresses on the NVMCTRL).
        /// It is here because <see cref="SamBaChipInfo.HasUniqueId"/> answers off a table row, with
        /// no device in front of it.
        /// </para>
        /// </summary>
        public readonly int UniqueIdWords;

        internal ChipRecord(
            uint key,
            uint extendedKey,
            ChipKeyKind keyKind,
            SamBaChipFamily family,
            string name,
            FlashControllerKind controllerKind,
            uint flashAddress,
            int pageCount,
            int pageSize,
            int planeCount,
            int lockRegionCount,
            uint flashControllerBaseAddress,
            int? bootGpnvmBitIndex,
            int uniqueIdWords)
        {
            Key = key;
            ExtendedKey = extendedKey;
            KeyKind = keyKind;
            Family = family;
            Name = name;
            ControllerKind = controllerKind;
            FlashAddress = flashAddress;
            PageCount = pageCount;
            PageSize = pageSize;
            PlaneCount = planeCount;
            LockRegionCount = lockRegionCount;
            FlashControllerBaseAddress = flashControllerBaseAddress;
            BootGpnvmBitIndex = bootGpnvmBitIndex;
            UniqueIdWords = uniqueIdWords;
        }

        /// <summary>
        /// A copy of this record with the four geometry fields replaced — how a provisional record
        /// (a part identified by family fallback, geometry all zero) is completed from what the
        /// flash controller reported about itself. Everything else, identification and addressing
        /// included, carries over unchanged.
        /// </summary>
        internal ChipRecord WithGeometry(int pageCount, int pageSize, int planeCount, int lockRegionCount)
        {
            return new ChipRecord(
                Key, ExtendedKey, KeyKind, Family, Name, ControllerKind, FlashAddress,
                pageCount, pageSize, planeCount, lockRegionCount,
                FlashControllerBaseAddress, BootGpnvmBitIndex, UniqueIdWords);
        }

        /// <summary>Flash size in bytes (<see cref="PageCount"/> × <see cref="PageSize"/>).</summary>
        public long FlashSize => (long)PageCount * PageSize;

        /// <summary>
        /// Hardware pages in one write block — the span that cannot be written without erasing its
        /// neighbours, and so the unit the flash layer programs in.
        /// <para>
        /// A property of the controller generation rather than of the part, which is why it is
        /// switched on <see cref="ControllerKind"/> instead of carried per row. The EFC families erase a
        /// page at a time, so a block is one page; NVMCTRL cannot erase less than a row (4 pages) on
        /// the D2x generation or a block (16) on the D5x. Kept here rather than in the controllers so
        /// the value is reachable from a <see cref="ChipRecord"/> alone — the supported-chip listing
        /// reports it without opening a device.
        /// </para>
        /// <para>
        /// Every kind is named and an unknown one throws, rather than one page being the default a
        /// controller falls back to. A new generation that erases in some larger unit would otherwise
        /// be reported here as erasing a page at a time, and a wrong write-block size is not a benign
        /// wrong answer: it is the alignment public callers hand offsets in and the span the
        /// read-modify-write path preserves, so understating it corrupts the neighbours it was meant
        /// to protect. Failing here is the same choice <c>FlashController.Create</c> makes for the
        /// same reason.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <see cref="ControllerKind"/> holds a kind this property has not been taught — a gap in the
        /// library, not something a caller can cause.
        /// </exception>
        public int PagesPerWriteBlock
        {
            get
            {
                switch (ControllerKind)
                {
                    // A page is its own write block: both EFC generations erase as they program.
                    case FlashControllerKind.Efc:
                    case FlashControllerKind.Eefc:
                        return 1;
                    case FlashControllerKind.D2xNvm: return 4;
                    case FlashControllerKind.D5xNvm: return 16;
                    default:
                        throw new InvalidOperationException($"Unknown flash controller kind: {ControllerKind}.");
                }
            }
        }

        /// <summary>Bytes in one write block (<see cref="PagesPerWriteBlock"/> × <see cref="PageSize"/>).</summary>
        public int WriteBlockSize => PagesPerWriteBlock * PageSize;

        /// <summary>Write blocks in the whole flash.</summary>
        public int WriteBlockCount => PageCount / PagesPerWriteBlock;
    }
}

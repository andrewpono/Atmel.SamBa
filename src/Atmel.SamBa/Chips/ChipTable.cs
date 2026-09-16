using System;
using System.Collections.Generic;


namespace Anp.Atmel.SamBa.Chips
{
    /// <summary>
    /// Supported-device table: one row per SAM part, keyed by the value read during
    /// identification. A part with several variants that share flash geometry contributes
    /// several rows with the same geometry. Keys are pre-masked by hand — with
    /// <see cref="ChipIdKeyMask"/> or <see cref="DeviceIdKeyMask"/>, per the row's
    /// <see cref="ChipRecord.KeyKind"/> — so variant/revision bits don't affect the match.
    /// <para>
    /// Hard-coded rather than decoded from the identification word, which is a fair question to ask
    /// of a CHIPID, since a CIDR does encode its own flash size in NVPSIZ. It agrees with the
    /// geometry here on 82 of the 84 CHIPID rows, and <c>Rows_FlashSizeAgreesWithItsChipId</c>
    /// checks that. It is not enough to derive from, for four reasons. Page size, lock-region count
    /// and plane count are not encoded at all — there is no NVPSIZ-equivalent field for any of them,
    /// and NVPSIZ2 reads 0 on every two-plane row here. NVPSIZ changes meaning with NVPTYP: on the
    /// SAM9XE, which carries ROM beside flash, it describes the ROM and NVPSIZ2 the flash. One CIDR
    /// can cover parts of different sizes — 0x23CC0CE0 is both the ATSAM4E16 and the ATSAM4E8, and
    /// reports 1024 KB, so a decoder would claim twice the flash a real ATSAM4E8 has. And a
    /// bootloader that emulates a SAM part answers with whatever it likes: the one such id this table
    /// ever carried, disabled further down, described no flash at all against a claimed 8 MB.
    /// </para>
    /// <para>
    /// A DSU DID encodes no memory size whatsoever, so none of this even arises for the 101
    /// <see cref="ChipKeyKind.DeviceId"/> rows. The parts do report their geometry at runtime — EEFC
    /// through its GETD descriptor command, NVMCTRL through its PARAM register — but that is a
    /// different thing from decoding an id, and it cannot bootstrap itself, since the register base
    /// to ask through is table data too.
    /// </para>
    /// </summary>
    internal static class ChipTable
    {
        /// <summary>
        /// Bits of a CHIPID CIDR that identify the part: everything but the top bit and the low
        /// five, which carry variant and revision detail that has no bearing on flash geometry.
        /// <para>
        /// Applied twice over: to every <see cref="ChipKeyKind.ChipId"/> row's
        /// <see cref="ChipRecord.Key"/> when it was written down, and to the probe value in
        /// <see cref="TryFind"/>, which is what makes the comparison there a plain equality. A row
        /// whose key was entered without the mask applied would simply never match — silently, since
        /// no probe value can carry the bits it left in — so
        /// <c>Rows_HaveKeysAlreadyMasked</c> checks all of them.
        /// </para>
        /// </summary>
        internal const uint ChipIdKeyMask = 0x7FFFFFE0;

        /// <summary>
        /// Bits of a DSU DID that identify the part. Keeps the family/series fields and drops the
        /// die-revision byte; see <see cref="ChipIdKeyMask"/> for how the mask is used.
        /// </summary>
        internal const uint DeviceIdKeyMask = 0xFFFF00FF;

        /// <summary>
        /// A CHIPID-keyed row, matched on its masked CIDR alone. Controller kind, EEFC base (where
        /// applicable), flash address, boot GPNVM bit and unique-id word count all come from the
        /// family's descriptor — see <see cref="Families"/> — so a row cannot disagree with its own
        /// family on any of them; only geometry (page count/size, planes, lock regions) is per-row,
        /// since that genuinely varies within a family.
        /// </summary>
        private static ChipRecord ChipIdRow(
            uint chipId, SamBaChipFamily family, string name,
            int pageCount, int pageSize, int planeCount, int lockRegionCount)
            => ChipIdRow(chipId, 0, family, name, pageCount, pageSize, planeCount, lockRegionCount);

        /// <summary>
        /// A CHIPID-keyed row that shares its CIDR with another part and is told apart by the CHIPID
        /// extension word as well — the four SAM4E rows, and nothing else in the table.
        /// </summary>
        private static ChipRecord ChipIdRow(
            uint chipId, uint extendedChipId, SamBaChipFamily family, string name,
            int pageCount, int pageSize, int planeCount, int lockRegionCount)
        {
            FamilyDescriptor d = Families.Of(family);
            return new ChipRecord(
                chipId, extendedChipId, ChipKeyKind.ChipId, family, name, d.ControllerKind, d.FlashAddress,
                pageCount, pageSize, planeCount, lockRegionCount,
                d.FlashControllerBaseAddress, d.BootGpnvmBitIndex, d.UniqueIdWords);
        }

        /// <summary>
        /// A DSU-DID-keyed row, matched on its masked DID. Controller kind, flash address, boot GPNVM bit
        /// and unique-id word count come from the family's descriptor exactly as in
        /// <see cref="ChipIdRow(uint, SamBaChipFamily, string, int, int, int, int)"/>; lock-region
        /// count comes from the controller generation the descriptor names (16 on
        /// <see cref="FlashControllerKind.D2xNvm"/>, 32 on <see cref="FlashControllerKind.D5xNvm"/>)
        /// rather than being repeated as a literal at every call site.
        /// </summary>
        private static ChipRecord DeviceIdRow(
            uint deviceId, SamBaChipFamily family, string name, int pageCount, int pageSize)
        {
            FamilyDescriptor d = Families.Of(family);
            int lockRegionCount = d.ControllerKind == FlashControllerKind.D2xNvm ? 16 : 32;
            return new ChipRecord(
                deviceId, 0, ChipKeyKind.DeviceId, family, name, d.ControllerKind, d.FlashAddress,
                pageCount, pageSize, 1, lockRegionCount,
                d.FlashControllerBaseAddress, d.BootGpnvmBitIndex, d.UniqueIdWords);
        }

        /// <summary>
        /// Backing store for <see cref="Rows"/> — the table itself. Private so that nothing can assign
        /// into the one lookup every identification and the whole supported-chip listing depend on.
        /// <see cref="TryFind"/> walks this array rather than the view, keeping its loop over 185
        /// structs free of interface dispatch.
        /// <para>
        /// Every row reads: id, family, name, page count, page size, plane count, lock regions — with
        /// the CHIPID extension word second on the four rows that need one. Flash address, EEFC base,
        /// boot GPNVM bit and unique-id word count are not row arguments any more: they come from the
        /// family, via <see cref="Families"/>.
        /// </para>
        /// </summary>
        private static readonly ChipRecord[] _rows =
        {
            //
            // SAM7SE
            //
            ChipIdRow(0x272A0A40, SamBaChipFamily.Sam7Se, "AT91SAM7SE512", 2048, 256, 2, 32),
            ChipIdRow(0x272A0940, SamBaChipFamily.Sam7Se, "AT91SAM7SE256", 1024, 256, 1, 16),
            ChipIdRow(0x27280340, SamBaChipFamily.Sam7Se, "AT91SAM7SE32", 256, 128, 1, 8),
            //
            // SAM7S
            //
            ChipIdRow(0x270B0A40, SamBaChipFamily.Sam7S, "AT91SAM7S512", 2048, 256, 2, 32),
            ChipIdRow(0x270D0940, SamBaChipFamily.Sam7S, "AT91SAM7S256", 1024, 256, 1, 16), // A
            ChipIdRow(0x270B0940, SamBaChipFamily.Sam7S, "AT91SAM7S256", 1024, 256, 1, 16), // B/C
            ChipIdRow(0x270C0740, SamBaChipFamily.Sam7S, "AT91SAM7S128", 512, 256, 1, 8), // A
            ChipIdRow(0x270A0740, SamBaChipFamily.Sam7S, "AT91SAM7S128", 512, 256, 1, 8), // B/C
            ChipIdRow(0x27090540, SamBaChipFamily.Sam7S, "AT91SAM7S64", 512, 128, 1, 16),
            // Named for the USB-equipped half of each pair, because that is the only part the row can
            // actually be reporting. The AT91SAM7S32 and AT91SAM7S16 have no USB device port, so
            // their ROM monitor answers on the DBGU alone — beyond this library's reach even when a
            // port is opened past the VID/PID filter, since the transport applies no line parameters
            // at all (a deliberate USB CDC choice; see the transports' Open methods), leaving a real
            // UART behind a bridge to be driven at whatever baud the bridge came up in. The siblings
            // AT91SAM7S321 (CIDR 0x27080342) and AT91SAM7S161 (0x27050241) do have USB, share the
            // geometry, and differ only in bits the key mask drops — so one masked key covers both
            // members of a pair, and whatever answers over USB is always the sibling. The keys stay
            // as written: they are already the masked form both ids reduce to.
            // That is also why these rows stay enabled while the serial-only SAM3N and SAM7L rows
            // below are disabled — there, no variant of the family has USB at all, so disabling them
            // costs no reachable part.
            ChipIdRow(0x27080340, SamBaChipFamily.Sam7S, "AT91SAM7S321", 256, 128, 1, 8),
            ChipIdRow(0x27050240, SamBaChipFamily.Sam7S, "AT91SAM7S161", 256, 64, 1, 8),
            //
            // SAM7XC
            //
            ChipIdRow(0x271C0A40, SamBaChipFamily.Sam7Xc, "AT91SAM7XC512", 2048, 256, 2, 32),
            ChipIdRow(0x271B0940, SamBaChipFamily.Sam7Xc, "AT91SAM7XC256", 1024, 256, 1, 16),
            ChipIdRow(0x271A0740, SamBaChipFamily.Sam7Xc, "AT91SAM7XC128", 512, 256, 1, 8),
            //
            // SAM7X
            //
            ChipIdRow(0x275C0A40, SamBaChipFamily.Sam7X, "AT91SAM7X512", 2048, 256, 2, 32),
            ChipIdRow(0x275B0940, SamBaChipFamily.Sam7X, "AT91SAM7X256", 1024, 256, 1, 16),
            ChipIdRow(0x275A0740, SamBaChipFamily.Sam7X, "AT91SAM7X128", 512, 256, 1, 8),
            //
            // SAM4S. Lock-region counts all follow the datasheet's 8 KB region size, per Table 8-2
            // of revision 11100K: SD32 256, SD16 128, SA16 128, S16 128, S8 64, S4 32, S2 16. Three
            // were wrong here until the datasheet pass — SD16 and SA16 carried 256, which is only
            // the SD32's, and S4 carried 16, which is only the S2's — and the count is not
            // cosmetic: it sets the region-to-page arithmetic every lock and unlock uses. A wrong
            // one is at least loud on hardware, since GETD reports FL_NB_LOCK and a mismatch with
            // the row raises the geometry event at open.
            //
            // SA16 is single-plane despite its 1 MB, which took some settling because the datasheet
            // contradicts itself twice in the same chapter, in both revisions read (11100B and
            // 11100K). Its flash-size bullet list groups "SAM4SD16/SA16" as 2 x 512 KB with two
            // bank addresses, and calls the S16 512 KB; Table 8-1, the Configuration Summary and
            // Table 8-2 all say SA16 is 1024 KB in one plane and the S16 1024 KB. The CIDR agrees
            // with the tables — SA16's ARCH nibble is the non-D series' — so the bullet list is
            // simply wrong, and the tables are what these rows follow.
            //
            // The 48-pin ('A') rows for SD32, SD16, SA16, S16 and S8 are ids no product carries:
            // the CHIPID table lists those five in 64-pin ('B') and 100-pin ('C') only, while the
            // S4 and S2 do have real 48-pin variants. They cost nothing (a key no part reports never
            // matches) and are kept for the same reason as the SAM3S8A/SD8A rows below — see the
            // note there.
            //
            ChipIdRow(0x29870EE0, SamBaChipFamily.Sam4S, "ATSAM4SD32", 4096, 512, 2, 256), // A
            ChipIdRow(0x29970EE0, SamBaChipFamily.Sam4S, "ATSAM4SD32", 4096, 512, 2, 256), // B
            ChipIdRow(0x29A70EE0, SamBaChipFamily.Sam4S, "ATSAM4SD32", 4096, 512, 2, 256), // C
            ChipIdRow(0x29870CE0, SamBaChipFamily.Sam4S, "ATSAM4SD16", 2048, 512, 2, 128), // A
            ChipIdRow(0x29970CE0, SamBaChipFamily.Sam4S, "ATSAM4SD16", 2048, 512, 2, 128), // B
            ChipIdRow(0x29A70CE0, SamBaChipFamily.Sam4S, "ATSAM4SD16", 2048, 512, 2, 128), // C
            ChipIdRow(0x28870CE0, SamBaChipFamily.Sam4S, "ATSAM4SA16", 2048, 512, 1, 128), // A
            ChipIdRow(0x28970CE0, SamBaChipFamily.Sam4S, "ATSAM4SA16", 2048, 512, 1, 128), // B
            ChipIdRow(0x28A70CE0, SamBaChipFamily.Sam4S, "ATSAM4SA16", 2048, 512, 1, 128), // C
            ChipIdRow(0x288C0CE0, SamBaChipFamily.Sam4S, "ATSAM4S16", 2048, 512, 1, 128), // A
            ChipIdRow(0x289C0CE0, SamBaChipFamily.Sam4S, "ATSAM4S16", 2048, 512, 1, 128), // B
            ChipIdRow(0x28AC0CE0, SamBaChipFamily.Sam4S, "ATSAM4S16", 2048, 512, 1, 128), // C
            ChipIdRow(0x288C0AE0, SamBaChipFamily.Sam4S, "ATSAM4S8", 1024, 512, 1, 64), // A
            ChipIdRow(0x289C0AE0, SamBaChipFamily.Sam4S, "ATSAM4S8", 1024, 512, 1, 64), // B
            ChipIdRow(0x28AC0AE0, SamBaChipFamily.Sam4S, "ATSAM4S8", 1024, 512, 1, 64), // C
            ChipIdRow(0x288B09E0, SamBaChipFamily.Sam4S, "ATSAM4S4", 512, 512, 1, 32), // A
            ChipIdRow(0x289B09E0, SamBaChipFamily.Sam4S, "ATSAM4S4", 512, 512, 1, 32), // B
            ChipIdRow(0x28AB09E0, SamBaChipFamily.Sam4S, "ATSAM4S4", 512, 512, 1, 32), // C
            ChipIdRow(0x288B07E0, SamBaChipFamily.Sam4S, "ATSAM4S2", 256, 512, 1, 16), // A
            ChipIdRow(0x289B07E0, SamBaChipFamily.Sam4S, "ATSAM4S2", 256, 512, 1, 16), // B
            ChipIdRow(0x28AB07E0, SamBaChipFamily.Sam4S, "ATSAM4S2", 256, 512, 1, 16), // C
            //
            // SAM3N — UNSUPPORTED, rows disabled. UART only: no variant has a USB device port, and
            // the boot program drives UART0 alone and waits solely on characters arriving there, so
            // the ROM monitor cannot be reached over the USB CDC transport this library implements.
            // Identifying one would only let the library fail later, deeper, and less clearly than
            // SamBaUnsupportedDeviceException does. The parts are also end-of-life (no authorised
            // distributor stock; aftermarket only). Kept commented rather than deleted so the
            // geometry is ready if a DBGU/UART transport is ever added. (Families.cs still carries
            // this family's identification pattern, recovered from these same ids, for the same
            // reason — see its own remarks.)
            //
            // ChipIdRow(0x29340960, SamBaChipFamily.Sam3N, "ATSAM3N4", 1024, 256, 1, 16), // A
            // ChipIdRow(0x29440960, SamBaChipFamily.Sam3N, "ATSAM3N4", 1024, 256, 1, 16), // B
            // ChipIdRow(0x29540960, SamBaChipFamily.Sam3N, "ATSAM3N4", 1024, 256, 1, 16), // C
            // ChipIdRow(0x29390760, SamBaChipFamily.Sam3N, "ATSAM3N2", 512, 256, 1, 8), // A
            // ChipIdRow(0x29490760, SamBaChipFamily.Sam3N, "ATSAM3N2", 512, 256, 1, 8), // B
            // ChipIdRow(0x29590760, SamBaChipFamily.Sam3N, "ATSAM3N2", 512, 256, 1, 8), // C
            // ChipIdRow(0x29380560, SamBaChipFamily.Sam3N, "ATSAM3N1", 256, 256, 1, 4), // A
            // ChipIdRow(0x29480560, SamBaChipFamily.Sam3N, "ATSAM3N1", 256, 256, 1, 4), // B
            // ChipIdRow(0x29580560, SamBaChipFamily.Sam3N, "ATSAM3N1", 256, 256, 1, 4), // C
            // ChipIdRow(0x29380360, SamBaChipFamily.Sam3N, "ATSAM3N0", 128, 256, 1, 2), // A
            // ChipIdRow(0x29480360, SamBaChipFamily.Sam3N, "ATSAM3N0", 128, 256, 1, 2), // B
            // ChipIdRow(0x29580360, SamBaChipFamily.Sam3N, "ATSAM3N0", 128, 256, 1, 2), // C
            // ChipIdRow(0x29350260, SamBaChipFamily.Sam3N, "ATSAM3N00", 64, 256, 1, 1), // A
            // ChipIdRow(0x29450260, SamBaChipFamily.Sam3N, "ATSAM3N00", 64, 256, 1, 1), // B
            //
            // SAM3S
            //
            // SAM3S8/SD8: 512 KB either way, but organised differently — the S8 as one bank of 2048
            // pages, the SD8 as two banks of 1024. PlaneCount stays 1 for both, because it counts
            // EEFC register blocks and the SD8 has exactly one (peripheral ID 6, FMR/FCR/FSR/FRR at
            // 0x400E0A00): its second bank is reached by page number through the extra Erase-plane
            // command, not by a second controller, and its 16 lock bits span the whole 512 KB in
            // 32 KB regions. Do not read the datasheet's "dual plane" as PlaneCount 2 — that would
            // address a second controller at 0x400E0C00, which on this part is not an EEFC. GETD
            // does report FL_NB_PLANE 2 here; ReadDeviceGeometry sums the planes and reports the
            // controllers it queried, so the descriptor still agrees with these rows.
            //
            // The 'A' rows carry ids the SAM3S family CHIPID table allocates for a 48-pin variant
            // that the SAM3S8/SD8 datasheet does not offer: its own CHIPID table and its ordering
            // information both stop at 64-pin ('B') and 100-pin ('C'). Kept because the ids are
            // documented and a key no part reports simply never matches, so listing them costs
            // nothing and covers the variant if one ever ships.
            ChipIdRow(0x298B0A60, SamBaChipFamily.Sam3S, "ATSAM3SD8", 2048, 256, 1, 16), // A
            ChipIdRow(0x299B0A60, SamBaChipFamily.Sam3S, "ATSAM3SD8", 2048, 256, 1, 16), // B
            ChipIdRow(0x29AB0A60, SamBaChipFamily.Sam3S, "ATSAM3SD8", 2048, 256, 1, 16), // C
            ChipIdRow(0x288B0A60, SamBaChipFamily.Sam3S, "ATSAM3S8", 2048, 256, 1, 16), // A
            ChipIdRow(0x289B0A60, SamBaChipFamily.Sam3S, "ATSAM3S8", 2048, 256, 1, 16), // B
            ChipIdRow(0x28AB0A60, SamBaChipFamily.Sam3S, "ATSAM3S8", 2048, 256, 1, 16), // C
            ChipIdRow(0x28800960, SamBaChipFamily.Sam3S, "ATSAM3S4", 1024, 256, 1, 16), // A
            ChipIdRow(0x28900960, SamBaChipFamily.Sam3S, "ATSAM3S4", 1024, 256, 1, 16), // B
            ChipIdRow(0x28A00960, SamBaChipFamily.Sam3S, "ATSAM3S4", 1024, 256, 1, 16), // C
            ChipIdRow(0x288A0760, SamBaChipFamily.Sam3S, "ATSAM3S2", 512, 256, 1, 8), // A
            ChipIdRow(0x289A0760, SamBaChipFamily.Sam3S, "ATSAM3S2", 512, 256, 1, 8), // B
            ChipIdRow(0x28AA0760, SamBaChipFamily.Sam3S, "ATSAM3S2", 512, 256, 1, 8), // C
            ChipIdRow(0x28890560, SamBaChipFamily.Sam3S, "ATSAM3S1", 256, 256, 1, 4), // A
            ChipIdRow(0x28990560, SamBaChipFamily.Sam3S, "ATSAM3S1", 256, 256, 1, 4), // B
            ChipIdRow(0x28A90560, SamBaChipFamily.Sam3S, "ATSAM3S1", 256, 256, 1, 4), // C
            //
            // SAM3U
            //
            // The SAM3U4's two 128 KB banks are not adjacent: Flash 0 sits at 0x00080000 and Flash 1
            // at 0x00100000, each behind its own bus-matrix slave and its own EEFC, with nothing
            // between 0xA0000 and 0xFFFFF. One base with contiguous page numbering can still describe
            // the part, because each bank's decoded window is 512 KB wide for a 128 KB array and the
            // array repeats four times across it: 0x80000, 0xA0000, 0xC0000, 0xE0000. Addressing bank
            // 0 through that last alias ends it at 0x100000 — where bank 1's window begins — so pages
            // 0-511 land in bank 0 and 512-1023 in bank 1, contiguously. The page latch has to agree,
            // and the datasheet says it does: the write buffer "is write-only and accessible all along
            // the [...] address space, so that each word can be written to its final address", the
            // page being chosen by FCR.PAGEN rather than by the address written.
            // Read-side aliasing is inferred from the same decode rather than stated, so this is the
            // one row whose base is not the datasheet's physical address. Do not "correct" it to
            // 0x00080000: that would break bank 1, whose latch loads would then reach EEFC0 while the
            // write command goes to EEFC1, to repair a bank 0 that is likely not broken. If the
            // inference is wrong on some die, write-then-verify reads back through this same base and
            // fails naming the address instead of corrupting quietly. Two consequences of the alias:
            // the flash window SamBaDevice recognises here is 0xE0000-0x11FFFF rather than the
            // physical 0x80000, and Eefc.GetUniqueId reads the id through the alias too.
            //
            // SAM3U2/SAM3U1 are single-plane and so have no bank-contiguity requirement of their own
            // — nothing forces them onto an alias the way SAM3U4's second bank does. But the same
            // bus-matrix behavior applies independent of plane count: an array's decoded window
            // repeats at a stride equal to the array's own size, regardless of how many EEFC blocks
            // sit behind it, so 0xE0000 is a valid (if, like the SAM3U4 row above, inferred rather
            // than datasheet-stated) alias for these smaller arrays too. Every SAM3U row is therefore
            // given the same FlashAddress (0xE0000, via Families.Of(SamBaChipFamily.Sam3U)) rather
            // than carrying U2/U1's true physical 0x80000 as a family-internal exception — the same
            // epistemic caveat as above still applies: a write-then-verify failure on one of these two
            // parts would now name 0xE0000 rather than the previously-certain 0x80000.
            //
            ChipIdRow(0x28000960, SamBaChipFamily.Sam3U, "ATSAM3U4", 1024, 256, 2, 32), // C
            ChipIdRow(0x28100960, SamBaChipFamily.Sam3U, "ATSAM3U4", 1024, 256, 2, 32), // E
            ChipIdRow(0x280A0760, SamBaChipFamily.Sam3U, "ATSAM3U2", 512, 256, 1, 16), // C
            ChipIdRow(0x281A0760, SamBaChipFamily.Sam3U, "ATSAM3U2", 512, 256, 1, 16), // E
            ChipIdRow(0x28090560, SamBaChipFamily.Sam3U, "ATSAM3U1", 256, 256, 1, 8), // C
            ChipIdRow(0x28190560, SamBaChipFamily.Sam3U, "ATSAM3U1", 256, 256, 1, 8), // E
            //
            // SAM3X
            //
            ChipIdRow(0x286E0A60, SamBaChipFamily.Sam3X, "ATSAM3X8", 2048, 256, 2, 32), // 8H
            ChipIdRow(0x285E0A60, SamBaChipFamily.Sam3X, "ATSAM3X8", 2048, 256, 2, 32), // 8E - Arduino Due
            ChipIdRow(0x284E0A60, SamBaChipFamily.Sam3X, "ATSAM3X8", 2048, 256, 2, 32), // 8C
            ChipIdRow(0x285B0960, SamBaChipFamily.Sam3X, "ATSAM3X4", 1024, 256, 2, 16), // 4E
            ChipIdRow(0x284B0960, SamBaChipFamily.Sam3X, "ATSAM3X4", 1024, 256, 2, 16), // 4C
            //
            // SAM3A
            //
            ChipIdRow(0x283E0A60, SamBaChipFamily.Sam3A, "ATSAM3A8", 2048, 256, 2, 32), // 8C
            ChipIdRow(0x283B0960, SamBaChipFamily.Sam3A, "ATSAM3A4", 1024, 256, 2, 16), // 4C
            //
            // SAM7L — UNSUPPORTED, rows disabled, for the same reason as SAM3N above: DBGU only.
            // Neither part has a USB device port and the ROM SAM-BA advertises serial communication
            // over the DBGU alone. Both are obsolete (Microchip lists AT91SAM7L128 as no longer
            // manufactured). Kept commented so the geometry survives for a future DBGU transport.
            //
            // ChipIdRow(0x27330740, SamBaChipFamily.Sam7L, "AT91SAM7L128", 512, 256, 1, 16),
            // ChipIdRow(0x27330540, SamBaChipFamily.Sam7L, "AT91SAM7L64", 256, 256, 1, 8),
            //
            // SAM9XE. The 512 KB key was a misprint in the first datasheet revision, which gave two
            // different and individually impossible CIDRs for the part — 0x3299A3A0 in the feature
            // list (16 KB SRAM for a part with 32 KB) and 0x329A73A0 in the CHIPID chapter (128 KB
            // flash for a part with 512 KB). Revision 6254B corrected it to the value below, and
            // its change log says so outright: "Section 8.13 Chip Identification, SAM9XE512 chip ID
            // is 0x329AA3A0". Worth keeping the history, because the wrong values are still in
            // circulation wherever the first revision is.
            //
            // NVPTYP is 3 on these (ROM beside flash), so NVPSIZ describes the 32 KB ROM and
            // NVPSIZ2 the flash — which is why they are the rows the CIDR cross-check reads
            // through the NVPSIZ2 branch, and why TryInferEfcGeometry refuses the family outright.
            //
            ChipIdRow(0x329AA3A0, SamBaChipFamily.Sam9Xe, "AT91SAM9XE512", 1024, 512, 1, 32),
            ChipIdRow(0x329A93A0, SamBaChipFamily.Sam9Xe, "AT91SAM9XE256", 512, 512, 1, 16),
            ChipIdRow(0x329973A0, SamBaChipFamily.Sam9Xe, "AT91SAM9XE128", 256, 512, 1, 8),
            //
            // SAM4E (single CHIPID case 0x23CC0CE0, disambiguated by the EXID inner switch)
            //
            ChipIdRow(0x23CC0CE0, 0x00120200, SamBaChipFamily.Sam4E, "ATSAM4E16", 2048, 512, 1, 128), // E
            ChipIdRow(0x23CC0CE0, 0x00120201, SamBaChipFamily.Sam4E, "ATSAM4E16", 2048, 512, 1, 128), // C
            ChipIdRow(0x23CC0CE0, 0x00120208, SamBaChipFamily.Sam4E, "ATSAM4E8", 1024, 512, 1, 64), // E
            ChipIdRow(0x23CC0CE0, 0x00120209, SamBaChipFamily.Sam4E, "ATSAM4E8", 1024, 512, 1, 64), // C
            //
            // SAME70
            //
            ChipIdRow(0x210D0A00, SamBaChipFamily.SamE70, "ATSAME70x19", 1024, 512, 1, 32),
            ChipIdRow(0x21020C00, SamBaChipFamily.SamE70, "ATSAME70x20", 2048, 512, 1, 64),
            ChipIdRow(0x21020E00, SamBaChipFamily.SamE70, "ATSAME70x21", 4096, 512, 1, 128),
            //
            // SAMS70
            //
            ChipIdRow(0x211D0A00, SamBaChipFamily.SamS70, "ATSAMS70x19", 1024, 512, 1, 32),
            ChipIdRow(0x21120C00, SamBaChipFamily.SamS70, "ATSAMS70x20", 2048, 512, 1, 64),
            ChipIdRow(0x21120E00, SamBaChipFamily.SamS70, "ATSAMS70x21", 4096, 512, 1, 128),
            //
            // SAMV70
            //
            ChipIdRow(0x213D0A00, SamBaChipFamily.SamV70, "ATSAMV70x19", 1024, 512, 1, 32),
            ChipIdRow(0x21320C00, SamBaChipFamily.SamV70, "ATSAMV70x20", 2048, 512, 1, 64),
            //
            // SAMV71
            //
            ChipIdRow(0x212D0A00, SamBaChipFamily.SamV71, "ATSAMV71x19", 1024, 512, 1, 32),
            ChipIdRow(0x21220C00, SamBaChipFamily.SamV71, "ATSAMV71x20", 2048, 512, 1, 64),
            ChipIdRow(0x21220E00, SamBaChipFamily.SamV71, "ATSAMV71x21", 4096, 512, 1, 128),
            //
            // qNimble Quarto: a SAM-BA-compatible bootloader that emulates a legacy EFC part.
            //
            // Disabled, and unlike the rows above not for want of a transport — this one never
            // worked. MC_FSR reports lock state in its top 16 bits, so a one-plane EFC part can hold
            // at most 16 lock regions, and Efc's constructor refuses more rather than let
            // GetLockRegions wrap its shifts silently. The row carries 32 against a single plane, so
            // identifying a Quarto only ever led to an exception out of Open.
            //
            // Which figure is wrong is not something this library can settle: the part is a
            // bootloader emulating hardware, not silicon with a datasheet. 32 across two planes is
            // what the AT91SAM7X512 it imitates carries — 16 per plane, which fits — but halving it
            // on that reasoning would be inventing geometry for a device nobody here can test, and on
            // the one row whose id and sizes were never vendor-documented in the first place: a
            // synthetic CIDR describing no flash at all, against a claimed 8 MB.
            //
            // So a Quarto now identifies as unsupported, which is what it effectively already was.
            // Kept commented rather than deleted so a later attempt starts from the id instead of
            // from nothing; reviving it needs a real device, the bootloader's own account of its lock
            // regions, and a test that opens the row.
            //
            // ChipIdRow(0x714E3000, SamBaChipFamily.Sam7X, "qNimble BOSSAv1", 32768, 256, 1, 32),
            //
            // No CHIPID devices (matched on DSU DID & 0xFFFF00FF).
            //
            // No ROM SAM-BA: not one of the families below carries a SAM-BA monitor in ROM, the
            // way every CHIPID part above does. The same newer line that identifies through the
            // DSU expects its monitor to be a bootloader programmed into the first pages of
            // flash (Arduino, UF2 or similar). Two consequences for this library: a blank or
            // bricked part cannot be reached at all until a bootloader is restored over SWD with
            // a debug probe, and an image for one of these parts normally starts at a
            // SamBaUpdateOptions.Offset past the bootloader rather than at 0.
            //
            // SAMC21 (NVMCTRL, shares the D2x controller with SAMD21)
            //
            DeviceIdRow(0x1101000D, SamBaChipFamily.SamC21, "ATSAMC21x15", 512, 64),  // E15A
            DeviceIdRow(0x11010008, SamBaChipFamily.SamC21, "ATSAMC21x15", 512, 64),  // G15A
            DeviceIdRow(0x11010003, SamBaChipFamily.SamC21, "ATSAMC21x15", 512, 64),  // J15A
            DeviceIdRow(0x1101000C, SamBaChipFamily.SamC21, "ATSAMC21x16", 1024, 64), // E16A
            DeviceIdRow(0x11010007, SamBaChipFamily.SamC21, "ATSAMC21x16", 1024, 64), // G16A
            DeviceIdRow(0x11010002, SamBaChipFamily.SamC21, "ATSAMC21x16", 1024, 64), // J16A
            DeviceIdRow(0x1101000B, SamBaChipFamily.SamC21, "ATSAMC21x17", 2048, 64), // E17A
            DeviceIdRow(0x11010006, SamBaChipFamily.SamC21, "ATSAMC21x17", 2048, 64), // G17A
            DeviceIdRow(0x11010001, SamBaChipFamily.SamC21, "ATSAMC21x17", 2048, 64), // J17A
            DeviceIdRow(0x11010010, SamBaChipFamily.SamC21, "ATSAMC21x17", 2048, 64), // J17AU
            DeviceIdRow(0x11010021, SamBaChipFamily.SamC21, "ATSAMC21x17", 2048, 64), // N17A
            DeviceIdRow(0x1101000A, SamBaChipFamily.SamC21, "ATSAMC21x18", 4096, 64), // E18A
            DeviceIdRow(0x11010005, SamBaChipFamily.SamC21, "ATSAMC21x18", 4096, 64), // G18A
            DeviceIdRow(0x11010000, SamBaChipFamily.SamC21, "ATSAMC21x18", 4096, 64), // J18A
            DeviceIdRow(0x1101000F, SamBaChipFamily.SamC21, "ATSAMC21x18", 4096, 64), // J18AU
            DeviceIdRow(0x11010020, SamBaChipFamily.SamC21, "ATSAMC21x18", 4096, 64), // N18A
            //
            // SAMD21
            //
            DeviceIdRow(0x10010003, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // J15A
            DeviceIdRow(0x10010008, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // G15A
            DeviceIdRow(0x1001000D, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // E15A
            DeviceIdRow(0x10010021, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // J15B
            DeviceIdRow(0x10010024, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // G15B
            DeviceIdRow(0x10010027, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // E15B
            DeviceIdRow(0x10010056, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // E15B WLCSP
            DeviceIdRow(0x10010063, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // E15C WLCSP
            DeviceIdRow(0x1001003F, SamBaChipFamily.SamD21, "ATSAMD21x15", 512, 64),  // E15L
            DeviceIdRow(0x10010002, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // J16A
            DeviceIdRow(0x10010007, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // G16A
            DeviceIdRow(0x1001000C, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // E16A
            DeviceIdRow(0x10010020, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // J16B
            DeviceIdRow(0x10010023, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // G16B
            DeviceIdRow(0x10010026, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // E16B
            DeviceIdRow(0x10010055, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // E16B WLCSP
            DeviceIdRow(0x10010062, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // E16C WLCSP
            DeviceIdRow(0x10010057, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // G16L
            DeviceIdRow(0x1001003E, SamBaChipFamily.SamD21, "ATSAMD21x16", 1024, 64), // E16L
            DeviceIdRow(0x10010001, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // J17A
            DeviceIdRow(0x10010006, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // G17A
            DeviceIdRow(0x1001000B, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // E17A
            DeviceIdRow(0x10010094, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // E17D
            DeviceIdRow(0x10010095, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // E17D WLCSP
            DeviceIdRow(0x10010097, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // E17L
            DeviceIdRow(0x10010010, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // G17A WLCSP
            DeviceIdRow(0x10010093, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // G17D
            DeviceIdRow(0x10010096, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // G17L
            DeviceIdRow(0x10010092, SamBaChipFamily.SamD21, "ATSAMD21x17", 2048, 64), // J17D
            DeviceIdRow(0x10010000, SamBaChipFamily.SamD21, "ATSAMD21x18", 4096, 64), // J18A
            DeviceIdRow(0x10010005, SamBaChipFamily.SamD21, "ATSAMD21x18", 4096, 64), // G18A Arduino XIAO
            DeviceIdRow(0x1001000A, SamBaChipFamily.SamD21, "ATSAMD21x18", 4096, 64), // E18A
            DeviceIdRow(0x1001000F, SamBaChipFamily.SamD21, "ATSAMD21x18", 4096, 64), // G18A WLCSP
            //
            // SAMR21
            //
            DeviceIdRow(0x1001001E, SamBaChipFamily.SamR21, "ATSAMR21x16", 1024, 64), // E16A
            DeviceIdRow(0x1001001B, SamBaChipFamily.SamR21, "ATSAMR21x16", 1024, 64), // G16A
            DeviceIdRow(0x1001001D, SamBaChipFamily.SamR21, "ATSAMR21x17", 2048, 64), // E17A
            DeviceIdRow(0x1001001A, SamBaChipFamily.SamR21, "ATSAMR21x17", 2048, 64), // G17A
            DeviceIdRow(0x1001001C, SamBaChipFamily.SamR21, "ATSAMR21x18", 4096, 64), // E18A
            DeviceIdRow(0x10010019, SamBaChipFamily.SamR21, "ATSAMR21x18", 4096, 64), // G18A
            DeviceIdRow(0x10010018, SamBaChipFamily.SamR21, "ATSAMR21x19", 4096, 64), // E19A
            //
            // SAML21
            //
            DeviceIdRow(0x1081000D, SamBaChipFamily.SamL21, "ATSAML21x15", 512, 64),  // E15A
            DeviceIdRow(0x1081001C, SamBaChipFamily.SamL21, "ATSAML21x15", 512, 64),  // E15B
            DeviceIdRow(0x10810002, SamBaChipFamily.SamL21, "ATSAML21x16", 1024, 64), // J16A
            DeviceIdRow(0x10810007, SamBaChipFamily.SamL21, "ATSAML21x16", 1024, 64), // G16A
            DeviceIdRow(0x1081000C, SamBaChipFamily.SamL21, "ATSAML21x16", 1024, 64), // E16A
            DeviceIdRow(0x10810011, SamBaChipFamily.SamL21, "ATSAML21x16", 1024, 64), // J16B
            DeviceIdRow(0x10810016, SamBaChipFamily.SamL21, "ATSAML21x16", 1024, 64), // G16B
            DeviceIdRow(0x1081001B, SamBaChipFamily.SamL21, "ATSAML21x16", 1024, 64), // E16B
            DeviceIdRow(0x10810001, SamBaChipFamily.SamL21, "ATSAML21x17", 2048, 64), // J17A
            DeviceIdRow(0x10810006, SamBaChipFamily.SamL21, "ATSAML21x17", 2048, 64), // G17A
            DeviceIdRow(0x1081000B, SamBaChipFamily.SamL21, "ATSAML21x17", 2048, 64), // E17A
            DeviceIdRow(0x10810010, SamBaChipFamily.SamL21, "ATSAML21x17", 2048, 64), // J17B
            DeviceIdRow(0x10810015, SamBaChipFamily.SamL21, "ATSAML21x17", 2048, 64), // G17B
            DeviceIdRow(0x1081001A, SamBaChipFamily.SamL21, "ATSAML21x17", 2048, 64), // E17B
            DeviceIdRow(0x10810000, SamBaChipFamily.SamL21, "ATSAML21x18", 4096, 64), // J18A
            DeviceIdRow(0x10810005, SamBaChipFamily.SamL21, "ATSAML21x18", 4096, 64), // G18A
            DeviceIdRow(0x1081000A, SamBaChipFamily.SamL21, "ATSAML21x18", 4096, 64), // E18A
            DeviceIdRow(0x1081000F, SamBaChipFamily.SamL21, "ATSAML21x18", 4096, 64), // J18B
            DeviceIdRow(0x10810014, SamBaChipFamily.SamL21, "ATSAML21x18", 4096, 64), // G18B
            DeviceIdRow(0x10810019, SamBaChipFamily.SamL21, "ATSAML21x18", 4096, 64), // E18B
            //
            // SAMD51
            //
            DeviceIdRow(0x60060006, SamBaChipFamily.SamD51, "ATSAMD51x18", 512, 512),  // J18A
            DeviceIdRow(0x60060008, SamBaChipFamily.SamD51, "ATSAMD51x18", 512, 512),  // G18A
            DeviceIdRow(0x60060001, SamBaChipFamily.SamD51, "ATSAMD51x19", 1024, 512), // P19A
            DeviceIdRow(0x60060003, SamBaChipFamily.SamD51, "ATSAMD51x19", 1024, 512), // N19A
            DeviceIdRow(0x60060005, SamBaChipFamily.SamD51, "ATSAMD51x19", 1024, 512), // J19A
            DeviceIdRow(0x60060007, SamBaChipFamily.SamD51, "ATSAMD51x19", 1024, 512), // G19A
            DeviceIdRow(0x60060000, SamBaChipFamily.SamD51, "ATSAMD51x20", 2048, 512), // P20A
            DeviceIdRow(0x60060002, SamBaChipFamily.SamD51, "ATSAMD51x20", 2048, 512), // N20A
            DeviceIdRow(0x60060004, SamBaChipFamily.SamD51, "ATSAMD51x20", 2048, 512), // J20A
            //
            // SAME51 — DEVSEL runs 0x00-0x04 here in release order, not the 0x02-0x06 the other
            // three series use for the same packages; the family's identification table assigns it
            // per series, so each key is transcribed rather than derived. The two G variants came
            // later and on a different die: the table prints their ids in full, 0x61810306 (G18A)
            // and 0x61810305 (G19A), with DIE 3 where every other row of the family reads 0.
            // DeviceIdKeyMask drops DIE along with REVISION, so one key per part still covers
            // whatever die and revision a given part happens to be.
            //
            DeviceIdRow(0x61810003, SamBaChipFamily.SamE51, "ATSAME51x18", 512, 512),  // J18A
            DeviceIdRow(0x61810006, SamBaChipFamily.SamE51, "ATSAME51x18", 512, 512),  // G18A
            DeviceIdRow(0x61810002, SamBaChipFamily.SamE51, "ATSAME51x19", 1024, 512), // J19A
            DeviceIdRow(0x61810001, SamBaChipFamily.SamE51, "ATSAME51x19", 1024, 512), // N19A
            DeviceIdRow(0x61810005, SamBaChipFamily.SamE51, "ATSAME51x19", 1024, 512), // G19A
            DeviceIdRow(0x61810004, SamBaChipFamily.SamE51, "ATSAME51x20", 2048, 512), // J20A
            DeviceIdRow(0x61810000, SamBaChipFamily.SamE51, "ATSAME51x20", 2048, 512), // N20A
            //
            // SAME53
            //
            DeviceIdRow(0x61830006, SamBaChipFamily.SamE53, "ATSAME53x18", 512, 512),  // J18A
            DeviceIdRow(0x61830005, SamBaChipFamily.SamE53, "ATSAME53x19", 1024, 512), // J19A
            DeviceIdRow(0x61830003, SamBaChipFamily.SamE53, "ATSAME53x19", 1024, 512), // N19A
            DeviceIdRow(0x61830004, SamBaChipFamily.SamE53, "ATSAME53x20", 2048, 512), // J20A
            DeviceIdRow(0x61830002, SamBaChipFamily.SamE53, "ATSAME53x20", 2048, 512), // N20A
            //
            // SAME54
            //
            DeviceIdRow(0x61840001, SamBaChipFamily.SamE54, "ATSAME54x19", 1024, 512), // P19A
            DeviceIdRow(0x61840003, SamBaChipFamily.SamE54, "ATSAME54x19", 1024, 512), // N19A
            DeviceIdRow(0x61840000, SamBaChipFamily.SamE54, "ATSAME54x20", 2048, 512), // P20A
            DeviceIdRow(0x61840002, SamBaChipFamily.SamE54, "ATSAME54x20", 2048, 512), // N20A
        };

        /// <summary>Every supported-device row, in table order.</summary>
        internal static IReadOnlyList<ChipRecord> Rows => _rows;

        /// <summary>
        /// Finds the table row matching a probe result. The probe fills exactly one of
        /// <paramref name="deviceId"/> (DSU parts) or <paramref name="chipId"/> (CHIPID parts),
        /// which selects the <see cref="ChipKeyKind"/> to match against. In both cases the
        /// probe value is masked the same way the row's <see cref="ChipRecord.Key"/> already is
        /// (<see cref="DeviceIdKeyMask"/>; <see cref="ChipIdKeyMask"/>), so match is a plain
        /// equality; CHIPID rows additionally require the row's <see cref="ChipRecord.ExtendedKey"/>
        /// (when non-zero)
        /// to equal <paramref name="extChipId"/>, which disambiguates the SAM4E rows that share
        /// one CHIPID. First matching row wins.
        /// </summary>
        public static bool TryFind(uint chipId, uint extChipId, uint deviceId, out ChipRecord record)
        {
            if (deviceId != 0)
            {
                // DSU-identified parts (SAMC21/D21/R21/L21, SAMD51/E5x): match the masked DID.
                uint key = deviceId & DeviceIdKeyMask;
                foreach (ChipRecord row in _rows)
                {
                    if (row.KeyKind == ChipKeyKind.DeviceId && row.Key == key)
                    {
                        record = row;
                        return true;
                    }
                }
            }
            else if (chipId != 0)
            {
                // CHIPID-identified parts: match the masked CIDR, and the EXID too when the row
                // carries one (ExtendedKey == 0 means the CIDR alone is unambiguous).
                uint key = chipId & ChipIdKeyMask;
                foreach (ChipRecord row in _rows)
                {
                    if (row.KeyKind == ChipKeyKind.ChipId && row.Key == key
                        && (row.ExtendedKey == 0 || row.ExtendedKey == extChipId))
                    {
                        record = row;
                        return true;
                    }
                }
            }

            record = default;
            return false;
        }

        /// <summary>CIDR architecture field — the family identity within a CHIPID.</summary>
        private const int ArchShift = 20;
        private const uint ArchMask = 0xFF;

        /// <summary>
        /// CIDR EPROC field — the core generation. Paired with <see cref="ArchShift"/>/
        /// <see cref="ArchMask"/> to resolve a family in <see cref="Families.TryResolveChipIdFamily"/>,
        /// because ARCH alone is not unique per family (see that method's remarks).
        /// </summary>
        private const int EprocShift = 5;
        private const uint EprocMask = 0x7;

        /// <summary>CIDR NVPTYP field, and the one value meaning flash with no ROM beside it.</summary>
        private const int NonVolatileTypeShift = 28;
        private const uint NonVolatileTypeMask = 0x7;
        private const uint EmbeddedFlashOnly = 2;

        /// <summary>CIDR NVPSIZ field — flash size, as an index into <see cref="NonVolatileSizeKb"/>.</summary>
        private const int NonVolatileSizeShift = 8;
        private const uint NonVolatileSizeMask = 0xF;

        /// <summary>
        /// Chip ID NVPSIZ sizes in KB, indexed by field value; -1 marks the values the datasheet reserves.
        /// <para>
        /// <c>ChipTableTests</c> keeps a second copy of this table on purpose. Its job is to check
        /// every row's geometry against the row's own id, which it cannot do by calling the decoder
        /// here without a bug in the decoder hiding the very disagreement it looks for.
        /// </para>
        /// </summary>
        private static readonly int[] NonVolatileSizeKb =
            { 0, 8, 16, 32, -1, 64, -1, 128, -1, 256, 512, -1, 1024, -1, 2048, -1 };

        /// <summary>
        /// Family-level fallback for a CHIPID part no row matches: a provisional record carrying
        /// everything the family determines. Two tiers, tried in order.
        /// <para>
        /// First, <see cref="Families.TryResolveChipIdFamily"/> tries the CIDR's ARCH and EPROC
        /// fields together against a named family (see that method for why both are needed). A hit
        /// means <see cref="Families"/> supplies controller kind, EEFC base, flash address, boot
        /// GPNVM bit and unique-id word count directly — no pooling or agreement check needed, since
        /// they are that family's own guaranteed facts. Only geometry (page count/size, planes, lock
        /// regions) still varies per exact part: left at zero for
        /// <see cref="FlashControllerKind.Eefc"/> families, for <c>FlashController.Create</c> to
        /// fill from the flash descriptor, or decoded/inferred for
        /// <see cref="FlashControllerKind.Efc"/> families via <see cref="TryInferEfcGeometry"/>.
        /// </para>
        /// <para>
        /// Second, when no family's (ARCH, EPROC) pair matches — a core revision this table has not
        /// been taught, or an ARCH shared by families whose EPROC values disagree with this
        /// particular id — <see cref="TryFindArchOnlyFallback"/> falls back to the coarser,
        /// ARCH-only pooling this method used before family resolution existed: it never claims a
        /// specific name, only <see cref="SamBaChipFamily.Unknown"/> with whatever every same-ARCH
        /// row agrees on, and refuses outright if they do not. This tier is what keeps a genuinely
        /// novel part workable instead of unsupported, exactly as it always has.
        /// </para>
        /// <para>
        /// False only when ARCH itself matches no row at all — nothing left to pool.
        /// </para>
        /// </summary>
        public static bool TryFindChipIdFamilyFallback(uint chipId, out ChipRecord provisional)
        {
            provisional = default;

            byte arch = (byte)((chipId >> ArchShift) & ArchMask);
            byte eproc = (byte)((chipId >> EprocShift) & EprocMask);

            if (Families.TryResolveChipIdFamily(arch, eproc, out SamBaChipFamily family))
            {
                FamilyDescriptor d = Families.Of(family);

                int pageCount = 0, pageSize = 0, planeCount = 0, lockRegionCount = 0;
                switch (d.ControllerKind)
                {
                    case FlashControllerKind.Eefc:
                        break;      // geometry stays zero; the descriptor supplies it
                    case FlashControllerKind.Efc:
                        if (!TryInferEfcGeometry(
                            chipId, row => row.Family == family,
                            out pageCount, out pageSize, out planeCount, out lockRegionCount))
                            return false;
                        break;
                    default:
                        return false;   // no CHIPID family carries an NVMCTRL; nothing to fall back on
                }

                provisional = new ChipRecord(
                    chipId & ChipIdKeyMask, 0, ChipKeyKind.ChipId, family,
                    $"Unknown {family} (CHIPID 0x{chipId:X8})", d.ControllerKind, d.FlashAddress,
                    pageCount, pageSize, planeCount, lockRegionCount,
                    d.FlashControllerBaseAddress, d.BootGpnvmBitIndex, d.UniqueIdWords);
                return true;
            }

            return TryFindArchOnlyFallback(chipId, arch, out provisional);
        }

        /// <summary>
        /// The pre-family-resolution fallback, kept as the second tier of
        /// <see cref="TryFindChipIdFamilyFallback"/>: pools <see cref="ChipRecord.ControllerKind"/>,
        /// <see cref="ChipRecord.FlashControllerBaseAddress"/> and <see cref="ChipRecord.FlashAddress"/>
        /// across every row sharing <paramref name="arch"/> — regardless of family or EPROC — and
        /// refuses rather than guesses when they disagree (which is what protects against ARCH values
        /// two families share without agreeing on these facts, the same hazard EPROC exists to catch
        /// in the first tier). Never claims a specific family: the record this returns is always
        /// <see cref="SamBaChipFamily.Unknown"/>, whichever real family the part eventually turns out
        /// to be.
        /// </summary>
        private static bool TryFindArchOnlyFallback(uint chipId, byte arch, out ChipRecord provisional)
        {
            provisional = default;

            bool found = false;
            FlashControllerKind controllerKind = default;
            uint registerBase = 0;
            uint flashAddress = 0;
            foreach (ChipRecord row in _rows)
            {
                if (row.KeyKind != ChipKeyKind.ChipId || ((row.Key >> ArchShift) & ArchMask) != arch)
                    continue;

                if (!found)
                {
                    found = true;
                    controllerKind = row.ControllerKind;
                    registerBase = row.FlashControllerBaseAddress;
                    flashAddress = row.FlashAddress;
                }
                else if (row.ControllerKind != controllerKind
                    || row.FlashControllerBaseAddress != registerBase
                    || row.FlashAddress != flashAddress)
                {
                    return false;   // ambiguous ARCH — refuse rather than guess
                }
            }

            if (!found)
                return false;

            int pageCount = 0, pageSize = 0, planeCount = 0, lockRegionCount = 0;
            switch (controllerKind)
            {
                case FlashControllerKind.Eefc:
                    break;          // geometry stays zero; the descriptor supplies it
                case FlashControllerKind.Efc:
                    if (!TryInferEfcGeometry(
                        chipId, row => ((row.Key >> ArchShift) & ArchMask) == arch,
                        out pageCount, out pageSize, out planeCount, out lockRegionCount))
                        return false;
                    break;
                default:
                    return false;   // no CHIPID family carries an NVMCTRL; nothing to fall back on
            }

            provisional = new ChipRecord(
                chipId & ChipIdKeyMask, 0, ChipKeyKind.ChipId, SamBaChipFamily.Unknown,
                $"Unknown SAM (CHIPID 0x{chipId:X8})", controllerKind, flashAddress,
                pageCount, pageSize, planeCount, lockRegionCount,
                registerBase, bootGpnvmBitIndex: null, uniqueIdWords: 0);
            return true;
        }

        /// <summary>
        /// Geometry for an unlisted legacy EFC part: flash size decoded from the CIDR's own NVPSIZ
        /// field, and the page size, plane count and lock-region count taken from whichever rows
        /// <paramref name="isSibling"/> selects that carry that same flash size — every row of the
        /// resolved family in the first tier of <see cref="TryFindChipIdFamilyFallback"/>, every row
        /// sharing the probed ARCH in the second.
        /// <para>
        /// Weaker footing than the EEFC path and gated accordingly, because three of the four
        /// fields are inferred from sibling rows rather than measured, and a wrong page size or lock
        /// count damages flash rather than merely failing. NVPTYP must say embedded flash and
        /// nothing else, so a part reporting ROM beside its flash — where NVPSIZ describes the ROM —
        /// is refused, as is the synthetic id of a bootloader emulating an EFC part. The decoded
        /// size must be a real encoding, and at least one sibling row must carry it with exactly one
        /// shape. The result is therefore never invented: it is always a geometry that some listed
        /// part with the same flash size genuinely uses.
        /// </para>
        /// </summary>
        private static bool TryInferEfcGeometry(
            uint chipId, Func<ChipRecord, bool> isSibling,
            out int pageCount, out int pageSize, out int planeCount, out int lockRegionCount)
        {
            pageCount = 0;
            pageSize = 0;
            planeCount = 0;
            lockRegionCount = 0;

            if (((chipId >> NonVolatileTypeShift) & NonVolatileTypeMask) != EmbeddedFlashOnly)
                return false;

            int sizeKb = NonVolatileSizeKb[(chipId >> NonVolatileSizeShift) & NonVolatileSizeMask];
            if (sizeKb <= 0)
                return false;

            long flashSize = sizeKb * 1024L;
            bool found = false;
            foreach (ChipRecord row in _rows)
            {
                if (row.KeyKind != ChipKeyKind.ChipId || !isSibling(row) || row.FlashSize != flashSize)
                    continue;

                if (!found)
                {
                    found = true;
                    pageSize = row.PageSize;
                    planeCount = row.PlaneCount;
                    lockRegionCount = row.LockRegionCount;
                }
                else if (row.PageSize != pageSize || row.PlaneCount != planeCount
                    || row.LockRegionCount != lockRegionCount)
                {
                    return false;   // one size, two shapes — refuse rather than pick
                }
            }

            if (!found)
                return false;

            pageCount = (int)(flashSize / pageSize);
            return true;
        }
    }
}

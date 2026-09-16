using Anp.Atmel.SamBa.Chips;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Everything that is true of a chip because of which <see cref="SamBaChipFamily"/> it
    /// belongs to: flash-controller kind, its register base, flash's own base address, the GPNVM
    /// boot bit, the unique-id word count, how to reset it, and the CHIPID/DSU patterns that
    /// identify a member of the family. One instance per family, held by <see cref="Families"/>.
    /// </summary>
    internal sealed class FamilyDescriptor
    {
        /// <summary>Flash controller implementation every member of the family uses.</summary>
        public FlashControllerKind ControllerKind { get; }

        /// <summary>
        /// Flash controller's own register block (e.g. EEFC's FMR/FCR/FSR/FRR base). Named
        /// generically rather than "Eefc..." since this struct covers every controller kind — but
        /// only <see cref="FlashControllerKind.Eefc"/> families carry a real value here today:
        /// <see cref="FlashControllerKind.Efc"/>'s base is a fixed legacy-address constant
        /// hardcoded in <c>Efc.cs</c>, and NVMCTRL's is a fixed constant in
        /// <c>D2xNvm.cs</c>/<c>D5xNvm.cs</c> — neither sourced from here yet. 0 for both.
        /// </summary>
        public uint FlashControllerBaseAddress { get; }

        /// <summary>
        /// Where flash is memory-mapped; 0 for NVMCTRL families (flash starts at address 0).
        /// </summary>
        public uint FlashAddress { get; }

        /// <summary>Boot-mode GPNVM bit index, or null where the boot source is fixed.</summary>
        public int? BootGpnvmBitIndex { get; }

        /// <summary>Words in the factory unique id, or 0 where there is none to read.</summary>
        public int UniqueIdWords { get; }

        /// <summary>
        /// RSTC_CR address to write a keyed reset command to; null means the family resets through
        /// the Cortex-M core's own architectural AIRCR instead.
        /// </summary>
        public uint? RstcAddress { get; }

        /// <summary>
        /// CIDR ARCH byte values (bits 20-27) that identify a member of this family, for
        /// <see cref="ChipKeyKind.ChipId"/> families only. ARCH alone is not unique per family (it
        /// also varies by package/plane within one family), which is why it is paired with
        /// <see cref="ChipIdEproc"/>. Empty for DeviceId-keyed families.
        /// </summary>
        public IReadOnlyList<byte> ChipIdArchValues { get; }

        /// <summary>
        /// CIDR EPROC field (bits 5-7), constant across every row of a ChipId-keyed family and the
        /// value that disambiguates families sharing an ARCH byte (e.g. Sam3S and Sam4S both use
        /// ARCH 0x88/0x89/0x8A/0x98/0x99/0x9A, but Sam3S is EPROC 3 and Sam4S is EPROC 7). Null for
        /// DeviceId-keyed families.
        /// </summary>
        public byte? ChipIdEproc { get; }

        /// <summary>
        /// DSU DID upper 16 bits that identify a member of this family, for
        /// <see cref="ChipKeyKind.DeviceId"/> families only. Null for ChipId-keyed families, and
        /// also null for <see cref="SamBaChipFamily.SamR21"/>: the SAMR21 is a SAMD21 MCU die
        /// paired with a separate radio die in the same package, not a distinct MCU core, so its
        /// DSU DID carries the identical upper word as <see cref="SamBaChipFamily.SamD21"/>
        /// (<c>0x1001</c>) — resolved to <c>SamD21</c> there; SamR21 is only ever reached by an
        /// exact table-row match.
        /// </summary>
        public uint? DeviceIdUpperWord { get; }

        internal FamilyDescriptor(
            FlashControllerKind controllerKind,
            uint flashControllerBaseAddress,
            uint flashAddress,
            int? bootGpnvmBitIndex,
            int uniqueIdWords,
            uint? rstcAddress,
            IReadOnlyList<byte> chipIdArchValues = null,
            byte? chipIdEproc = null,
            uint? deviceIdUpperWord = null)
        {
            ControllerKind = controllerKind;
            FlashControllerBaseAddress = flashControllerBaseAddress;
            FlashAddress = flashAddress;
            BootGpnvmBitIndex = bootGpnvmBitIndex;
            UniqueIdWords = uniqueIdWords;
            RstcAddress = rstcAddress;
            ChipIdArchValues = chipIdArchValues ?? Array.Empty<byte>();
            ChipIdEproc = chipIdEproc;
            DeviceIdUpperWord = deviceIdUpperWord;
        }
    }

    /// <summary>
    /// The single source of the <see cref="FamilyDescriptor"/> for every non-
    /// <see cref="SamBaChipFamily.Unknown"/> family: flash-controller kind and addresses, boot and
    /// unique-id facts, and reset routing, all keyed by family. Also holds the CHIPID/DSU
    /// identification patterns <c>ChipTable.TryFindChipIdFamilyFallback</c> and
    /// <c>ChipIdentifier.NvmProvisional</c> use to name a family on an otherwise-unlisted part.
    /// </summary>
    internal static class Families
    {
        private static readonly Dictionary<SamBaChipFamily, FamilyDescriptor> _byFamily =
            new Dictionary<SamBaChipFamily, FamilyDescriptor>
            {
                //
                // SAM7 — legacy EFC, apart from the SAM7L. Sam7S has no boot GPNVM bit: it always
                // boots flash (see the family's own remarks in ChipTable.cs). Sam7L and Sam3N are
                // never actually identified (their ROM SAM-BA answers on a UART this library's USB
                // CDC transport cannot reach), but keep their descriptor — including the ARCH/EPROC
                // pattern recovered from ChipTable.cs's disabled rows — for the same reason those
                // rows are kept commented rather than deleted: the geometry and identity are ready
                // if a DBGU/UART transport is ever added.
                //
                [SamBaChipFamily.Sam7L] = new FamilyDescriptor(
                    FlashControllerKind.Efc, 0, 0x100000, 1, 0,
                    DeviceResetter.Reg.RstcSam7And9, new byte[] { 0x73 }, 2),
                [SamBaChipFamily.Sam7S] = new FamilyDescriptor(
                    FlashControllerKind.Efc, 0, 0x100000, null, 0,
                    DeviceResetter.Reg.RstcSam7And9, new byte[] { 0x70 }, 2),
                [SamBaChipFamily.Sam7Se] = new FamilyDescriptor(
                    FlashControllerKind.Efc, 0, 0x100000, 2, 0,
                    DeviceResetter.Reg.RstcSam7And9, new byte[] { 0x72 }, 2),
                [SamBaChipFamily.Sam7X] = new FamilyDescriptor(
                    FlashControllerKind.Efc, 0, 0x100000, 2, 0,
                    DeviceResetter.Reg.RstcSam7And9, new byte[] { 0x75 }, 2),
                [SamBaChipFamily.Sam7Xc] = new FamilyDescriptor(
                    FlashControllerKind.Efc, 0, 0x100000, 2, 0,
                    DeviceResetter.Reg.RstcSam7And9, new byte[] { 0x71 }, 2),

                //
                // SAM3 — EEFC. Sam3U's FlashAddress is the alias 0xE0000 for every row, not the
                // U2/U1 physical 0x80000 — see the SAM3U row comment in ChipTable.cs for why the
                // same bus-matrix aliasing that ATSAM3U4 needs also applies to the single-plane
                // parts.
                //
                [SamBaChipFamily.Sam3A] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0A00, 0x80000, 1, 4,
                    DeviceResetter.Reg.RstcSam3A3S3X, new byte[] { 0x83 }, 3),
                [SamBaChipFamily.Sam3N] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0A00, 0x400000, 1, 4,
                    DeviceResetter.Reg.RstcSam3N4S, new byte[] { 0x93, 0x94, 0x95 }, 3),
                [SamBaChipFamily.Sam3S] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0A00, 0x400000, 1, 4,
                    DeviceResetter.Reg.RstcSam3A3S3X,
                    new byte[] { 0x88, 0x89, 0x8A, 0x98, 0x99, 0x9A }, 3),
                [SamBaChipFamily.Sam3U] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0800, 0xE0000, 1, 4,
                    DeviceResetter.Reg.RstcSam3U, new byte[] { 0x80, 0x81 }, 3),
                [SamBaChipFamily.Sam3X] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0A00, 0x80000, 1, 4,
                    DeviceResetter.Reg.RstcSam3A3S3X, new byte[] { 0x84, 0x85, 0x86 }, 3),

                //
                // SAM4 / SAM9
                //
                [SamBaChipFamily.Sam4E] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0A00, 0x400000, 1, 4,
                    DeviceResetter.Reg.RstcSam4E, new byte[] { 0x3C }, 7),
                [SamBaChipFamily.Sam4S] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0A00, 0x400000, 1, 4,
                    DeviceResetter.Reg.RstcSam3N4S,
                    new byte[] { 0x88, 0x89, 0x8A, 0x98, 0x99, 0x9A }, 7),
                [SamBaChipFamily.Sam9Xe] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0xFFFFFA00, 0x200000, 3, 0,
                    DeviceResetter.Reg.RstcSam7And9, new byte[] { 0x29 }, 5),

                //
                // SAMx7x — resets through AIRCR, not RSTC (RstcAddress null).
                //
                [SamBaChipFamily.SamE70] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0C00, 0x400000, 1, 4,
                    null, new byte[] { 0x10 }, 0),
                [SamBaChipFamily.SamS70] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0C00, 0x400000, 1, 4,
                    null, new byte[] { 0x11 }, 0),
                [SamBaChipFamily.SamV70] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0C00, 0x400000, 1, 4,
                    null, new byte[] { 0x13 }, 0),
                [SamBaChipFamily.SamV71] = new FamilyDescriptor(
                    FlashControllerKind.Eefc, 0x400E0C00, 0x400000, 1, 4,
                    null, new byte[] { 0x12 }, 0),

                //
                // NVMCTRL, row-erase generation. No EEFC base, no GPNVM boot bit, flash starts at
                // 0 — none of that varies by family here, only the identification pattern does.
                //
                [SamBaChipFamily.SamC21] = new FamilyDescriptor(
                    FlashControllerKind.D2xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x1101),
                [SamBaChipFamily.SamD21] = new FamilyDescriptor(
                    FlashControllerKind.D2xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x1001),
                [SamBaChipFamily.SamL21] = new FamilyDescriptor(
                    FlashControllerKind.D2xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x1081),
                [SamBaChipFamily.SamR21] = new FamilyDescriptor(
                    FlashControllerKind.D2xNvm, 0, 0, null, 4, null),   // shares SamD21's DID — see remarks

                //
                // NVMCTRL, block-erase generation.
                //
                [SamBaChipFamily.SamD51] = new FamilyDescriptor(
                    FlashControllerKind.D5xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x6006),
                [SamBaChipFamily.SamE51] = new FamilyDescriptor(
                    FlashControllerKind.D5xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x6181),
                [SamBaChipFamily.SamE53] = new FamilyDescriptor(
                    FlashControllerKind.D5xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x6183),
                [SamBaChipFamily.SamE54] = new FamilyDescriptor(
                    FlashControllerKind.D5xNvm, 0, 0, null, 4, null, deviceIdUpperWord: 0x6184),
            };

        /// <summary>The family's descriptor.</summary>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="family"/> has no descriptor — reached only by <see
        /// cref="SamBaChipFamily.Unknown"/> or a family this table has not been taught, which is a
        /// gap in the table rather than anything a caller can cause.
        /// </exception>
        public static FamilyDescriptor Of(SamBaChipFamily family)
        {
            if (_byFamily.TryGetValue(family, out FamilyDescriptor descriptor))
                return descriptor;
            throw new InvalidOperationException($"No family descriptor recorded for {family}.");
        }

        /// <summary>Non-throwing lookup — false for <see cref="SamBaChipFamily.Unknown"/>.</summary>
        public static bool TryGet(SamBaChipFamily family, out FamilyDescriptor descriptor) =>
            _byFamily.TryGetValue(family, out descriptor);

        /// <summary>
        /// Names the family a CHIPID (ARCH, EPROC) pair belongs to, for a part whose exact CIDR
        /// matches no <c>ChipTable</c> row. False when no family's pattern matches — including on
        /// SAM7L/SAM3N, whose pattern exists here but can never actually be probed (see remarks on
        /// those two entries above).
        /// </summary>
        public static bool TryResolveChipIdFamily(byte arch, byte eproc, out SamBaChipFamily family)
        {
            foreach (KeyValuePair<SamBaChipFamily, FamilyDescriptor> entry in _byFamily)
            {
                if (entry.Value.ChipIdEproc == eproc && entry.Value.ChipIdArchValues.Contains(arch))
                {
                    family = entry.Key;
                    return true;
                }
            }

            family = default;
            return false;
        }

        /// <summary>
        /// Names the family a DSU DID belongs to, for a part whose exact DID matches no
        /// <c>ChipTable</c> row. Resolves a SAMR21-range id to <see cref="SamBaChipFamily.SamD21"/>
        /// (see remarks on that family's entry above) rather than refusing or guessing between the
        /// two. False when no family's pattern matches.
        /// </summary>
        public static bool TryResolveDeviceIdFamily(uint deviceId, out SamBaChipFamily family)
        {
            uint upperWord = deviceId >> 16;
            foreach (KeyValuePair<SamBaChipFamily, FamilyDescriptor> entry in _byFamily)
            {
                if (entry.Value.DeviceIdUpperWord == upperWord)
                {
                    family = entry.Key;
                    return true;
                }
            }

            family = default;
            return false;
        }
    }
}

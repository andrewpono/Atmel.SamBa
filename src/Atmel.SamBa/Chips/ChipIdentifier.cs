using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System.Collections.Generic;


namespace Anp.Atmel.SamBa.Chips
{
    /// <summary>
    /// Result of a successful chip identification probe: the table row that matched, plus every
    /// identification word the probe read on the way there. The words are kept because they are what
    /// a bug report about an unrecognised part needs — the same four
    /// <see cref="SamBaUnsupportedDeviceException"/> carries when nothing matches.
    /// <para>
    /// One rule throughout: null means the probe never read that register, and a value means it did,
    /// zero included. Most of them are null on any given part, because the branches taken reach at
    /// most two — a chip is identified through CHIPID or through the DSU, never both.
    /// </para>
    /// </summary>
    internal readonly struct ChipIdentity
    {
        /// <summary>The device-table row this chip matched.</summary>
        public readonly ChipRecord Record;

        /// <summary>Raw CHIPID CIDR, unmasked; null on DSU-identified parts.</summary>
        public readonly uint? ChipId;

        /// <summary>
        /// Raw CHIPID EXID, read beside a CIDR that answered; null when no CIDR was found, and 0 on
        /// the many CHIPID parts that have no extension word. Only the SAM4E rows need it.
        /// </summary>
        public readonly uint? ExtChipId;

        /// <summary>Raw DSU DID, unmasked; null on CHIPID-identified parts.</summary>
        public readonly uint? DeviceId;

        /// <summary>
        /// Whole ARM CPUID register, not just the PARTNO field the probe branches on — the
        /// implementer, variant and revision fields are the useful half of a "what is this part"
        /// report. Null on the ARM7/ARM9 parts, which have no such register and are told apart by the
        /// branch opcode at their reset vector instead.
        /// </summary>
        public readonly uint? CpuId;

        internal ChipIdentity(ChipRecord record, uint? chipId, uint? extChipId, uint? deviceId, uint? cpuId)
        {
            Record = record;
            ChipId = chipId;
            ExtChipId = extChipId;
            DeviceId = deviceId;
            CpuId = cpuId;
        }
    }

    /// <summary>
    /// Chip identification probe.
    /// The probe order is load-bearing: reading addresses a device does not support
    /// locks up the CPU, so each register is only read once earlier probes rule it in.
    /// <see cref="ReadChipIdPair"/> is the single exception, and documents why the read it makes
    /// on spec is safe. <see cref="Identify"/>'s <c>mode</c> parameter is the other, deliberate
    /// one — see <see cref="SamBaChipIdentificationMode"/> for what it costs to use it wrong.
    /// </summary>
    internal static class ChipIdentifier
    {
        // The two words at the bottom of the vector table, read to tell one core generation from
        // another before any peripheral address is trusted.
        private const uint ResetVectorAddress = 0x0;           // ARM7/9: a branch; Cortex-M: initial SP
        private const uint ResetHandlerAddress = 0x4;          // Cortex-M only: entry point

        private const uint Arm7ResetVectorMask = 0xFF000000;
        private const uint Arm7BranchOpcode = 0xEA000000;

        // Where a Cortex-M reset handler points when the part booted its SAM-BA ROM at 0x008xxxxx.
        // A Cortex-M4 that vectors there is a SAM4; one that does not is a SAMD51/E5x, whose monitor
        // is a bootloader in flash rather than in ROM.
        private const uint ResetHandlerRegionMask = 0xFFF00000;
        private const uint SambaRomRegion = 0x00800000;

        private const uint LegacyChipIdAddress = 0xFFFFF240;   // SAM7/9 DBGU CIDR
        private const uint CpuIdAddress = 0xE000ED00;
        private const uint DsuDidAddress = 0x41002018;         // SAMD/R/L/E5x DSU DID
        private const uint ChipIdAddress = 0x400E0740;         // SAM3/SAM4 CHIPID CIDR
        private const uint ChipIdAddressAlt = 0x400E0940;      // where the SAMx7x parts moved it
        private const uint ExtendedChipIdOffset = 0x4;         // EXID sits one word past the CIDR

        // PARTNO field of the CPUID register — the core identity, with the implementer, variant and
        // revision fields masked off so a part is recognised whatever silicon revision it is. The
        // values below are the datasheet figures (0xC60, 0xC24) as they read through that mask,
        // which sits at bits 4..15 and so leaves them shifted up by one nibble.
        private const uint CpuIdPartNoMask = 0x0000FFF0;
        private const uint CortexM0Plus = 0xC600;
        private const uint CortexM4 = 0xC240;

        /// <summary>
        /// Identifies the connected chip and returns its device-table record.
        /// </summary>
        /// <param name="monitor">Connected monitor to read through.</param>
        /// <param name="mode">
        /// Which probe to take at the legacy-CHIPID-versus-CPUID branch.
        /// <see cref="SamBaChipIdentificationMode.Auto"/> (the default) reads the reset vector and lets
        /// its content decide, as before this parameter existed; <see cref="SamBaChipIdentificationMode.ChipId"/>
        /// and <see cref="SamBaChipIdentificationMode.CpuId"/> skip that read and force the named
        /// branch — see that type's remarks for why this is only safe on a part whose core generation
        /// is already known.
        /// </param>
        /// <exception cref="SamBaUnsupportedDeviceException">No table row matches the probe result.</exception>
        public static ChipIdentity Identify(
            SambaMonitor monitor, SamBaChipIdentificationMode mode = SamBaChipIdentificationMode.Auto)
        {
            // Nullable so that a register the probe never reached stays distinguishable from one that
            // answered zero. The branches below reach at most two of the four, and a zero read is a
            // real answer worth reporting as such — a part that returns 0 from the DSU DID says
            // something quite different about itself than a part whose DSU was never consulted.
            uint? chipId = null;
            uint? extChipId = null;
            uint? deviceId = null;
            uint? cpuId = null;

            // Which NVMCTRL generation a DSU-identified part carries, per the CPUID branch that
            // reached the DSU. Only consulted when no table row matches, to build a provisional
            // record; null on every path that never read a DSU.
            FlashControllerKind? dsuGeneration = null;

            void Report(string message) => monitor.Report(message, ProgressStage.Identifying);

            Report($"Identifying chip, mode: {mode}");

            // All devices support address 0 (ARM reset vector). An ARM7TDMI branch opcode there means
            // an Atmel SAM7/9 with the legacy CHIPID register. A forced mode skips this read entirely
            // rather than only overriding its outcome, since the read itself is what
            // SamBaChipIdentificationMode exists to let a caller avoid.
            bool isLegacyChipId = mode == SamBaChipIdentificationMode.Auto
                ? (monitor.ReadWord(ResetVectorAddress) & Arm7ResetVectorMask) == Arm7BranchOpcode
                : mode == SamBaChipIdentificationMode.ChipId;

            if (isLegacyChipId)
            {
                Report("Reading legacy CHIPID");

                // Only the CIDR, unlike the Cortex-M paths below which read the EXID beside it. The
                // DBGU has one too, one word further on, but no legacy row is disambiguated by it —
                // every SAM7 and SAM9XE row carries ExtendedKey 0, so the CIDR alone decides. A future
                // legacy row that did need a tiebreaker would have to read it here.
                chipId = monitor.ReadWord(LegacyChipIdAddress);
            }
            else
            {
                // All Cortex-M devices support the ARM CPUID register. Kept whole rather than stored
                // masked: only the branches below care about the PARTNO field, whereas everything the
                // mask drops is worth reporting when no row matches.
                uint cpuIdWord = monitor.ReadWord(CpuIdAddress);
                cpuId = cpuIdWord;
                uint corePartNo = cpuIdWord & CpuIdPartNoMask;

                if (corePartNo == CortexM0Plus)
                {
                    Report("Reading device ID");
                    deviceId = monitor.ReadWord(DsuDidAddress);
                    dsuGeneration = FlashControllerKind.D2xNvm;
                }
                else if (corePartNo == CortexM4)
                {
                    // SAM4 processors reset-vector into the SAM-BA ROM;
                    // Cortex-M4 SAMD51/E5x parts do not and expose a DSU instead.
                    if ((monitor.ReadWord(ResetHandlerAddress) & ResetHandlerRegionMask) == SambaRomRegion)
                    {
                        Report("Reading CHIPID");
                        ReadChipIdPair(monitor, out chipId, out extChipId);
                    }
                    else
                    {
                        Report("Reading device ID");
                        deviceId = monitor.ReadWord(DsuDidAddress);
                        dsuGeneration = FlashControllerKind.D5xNvm;
                    }
                }
                else
                {
                    Report("Reading CHIPID");
                    ReadChipIdPair(monitor, out chipId, out extChipId);
                }
            }

            // Joined rather than concatenated so that whichever subset of the four registers this
            // probe read (never more than three, per the branches above) ends up comma-separated
            // instead of running two hex values together with nothing between them.
            var idParts = new List<string>(4);
            if (chipId.HasValue) idParts.Add($"Chip ID: 0x{chipId.Value:X8}");
            if (extChipId.HasValue) idParts.Add($"Extended ID: 0x{extChipId.Value:X8}");
            if (deviceId.HasValue) idParts.Add($"Device ID: 0x{deviceId.Value:X8}");
            if (cpuId.HasValue) idParts.Add($"CPU ID: 0x{cpuId.Value:X8}");
            if (idParts.Count > 0)
                Report(string.Join(", ", idParts));

            // Matching reads an absent word as 0, which is what the table already means by "the probe
            // did not fill this one" — the distinction only has to survive as far as the report.
            if (ChipTable.TryFind(chipId ?? 0, extChipId ?? 0, deviceId ?? 0, out ChipRecord record))
                return new ChipIdentity(record, chipId, extChipId, deviceId, cpuId);

            // No row, but the part may still be placeable by family: the CIDR's ARCH field or the
            // core behind a DSU names one, and the rows already listed for it pin the controller and
            // its addresses. Geometry then comes from the part — an EEFC and an NVMCTRL each report
            // their own, and a legacy EFC carries its flash size in the CIDR itself.
            // FlashController.Create completes the record from that account, or throws the same
            // unsupported exception when there is no usable one, so a part this returns for is not
            // yet a part that will open. One capability can stay behind: DeviceResetter has no
            // family-specific route for SamBaChipFamily.Unknown, so an unlisted part identified on the
            // legacy CHIPID-only branch (cpuId still null here) flashes but does not auto-reset
            // afterwards (a firmware update completes and says so; an explicit Reset() reports the
            // missing route). An unlisted part identified as Cortex-M still resets through AIRCR, since
            // cpuId carries forward below regardless of which branch named the family.
            if (chipId.HasValue && chipId.Value != 0
                && ChipTable.TryFindChipIdFamilyFallback(chipId.Value, out ChipRecord familyFallback))
                return new ChipIdentity(familyFallback, chipId, extChipId, deviceId, cpuId);

            if (deviceId.HasValue && deviceId.Value != 0 && dsuGeneration.HasValue)
                return new ChipIdentity(
                    NvmProvisional(deviceId.Value, dsuGeneration.Value), chipId, extChipId, deviceId, cpuId);

            throw new SamBaUnsupportedDeviceException(chipId, extChipId, deviceId, cpuId);
        }

        /// <summary>
        /// Provisional record for a DSU-identified part no table row matches. The DID's upper 16
        /// bits are tried against <see cref="Families.TryResolveDeviceIdFamily"/> first — if they
        /// name a family whose descriptor agrees with <paramref name="generation"/> (the CPUID
        /// branch's own, independent account of which NVMCTRL generation this is), the record is
        /// built with that family's real flash address, EEFC-equivalent base, boot GPNVM bit and
        /// unique-id word count, all of which are then not a guess: they are family facts fully
        /// determined by the resolved name. Otherwise (no pattern matches, or the two signals
        /// disagree — which would mean something genuinely new, not a currently-listed family) the
        /// record falls back to <see cref="SamBaChipFamily.Unknown"/> with the NVMCTRL's remaining
        /// architectural facts: flash starts at 0, one plane by construction, and the lock-region
        /// count is the generation's own (16 on D2x, 32 on D5x) regardless of which exact family it
        /// turns out to be.
        /// </summary>
        private static ChipRecord NvmProvisional(uint deviceId, FlashControllerKind generation)
        {
            int lockRegions = generation == FlashControllerKind.D2xNvm ? 16 : 32;

            if (Families.TryResolveDeviceIdFamily(deviceId, out SamBaChipFamily family))
            {
                FamilyDescriptor d = Families.Of(family);
                if (d.ControllerKind == generation)
                {
                    return new ChipRecord(
                        deviceId & ChipTable.DeviceIdKeyMask, 0, ChipKeyKind.DeviceId, family,
                        $"Unknown {family} (DSU DID 0x{deviceId:X8})", generation,
                        flashAddress: d.FlashAddress,
                        pageCount: 0, pageSize: 0, planeCount: 1, lockRegionCount: lockRegions,
                        flashControllerBaseAddress: d.FlashControllerBaseAddress,
                        bootGpnvmBitIndex: d.BootGpnvmBitIndex, uniqueIdWords: d.UniqueIdWords);
                }
                // The DID's upper word matched a family whose controller generation disagrees with
                // what the CPUID branch already determined — treat as unresolved rather than trust
                // one signal over the other.
            }

            return new ChipRecord(
                deviceId & ChipTable.DeviceIdKeyMask, 0, ChipKeyKind.DeviceId, SamBaChipFamily.Unknown,
                $"Unknown SAM (DSU DID 0x{deviceId:X8})", generation, flashAddress: 0,
                pageCount: 0, pageSize: 0, planeCount: 1, lockRegionCount: lockRegions,
                flashControllerBaseAddress: 0, bootGpnvmBitIndex: null, uniqueIdWords: 0);
        }

        /// <summary>
        /// Reads the CHIPID CIDR and, when it answers, the EXID beside it. Tries
        /// <see cref="ChipIdAddress"/> first and falls back to <see cref="ChipIdAddressAlt"/> only
        /// when that reads zero, since the SAMx7x parts moved the controller.
        /// <para>
        /// The one read in this class that no earlier probe rules in, so it is worth saying why it is
        /// safe: both addresses lie inside the System Controller block that every CHIPID-bearing part
        /// maps, so the address decodes on all of them and a part with no CHIPID there answers with a
        /// reserved-register zero instead of hanging the bus.
        /// </para>
        /// <para>
        /// Which is also the limitation, because the fallback fires on an exact zero and nothing else.
        /// A part whose primary address decoded to some unrelated register that reads non-zero would
        /// take that value as its CIDR, match no row, and be reported unsupported without the
        /// alternate address ever being tried. No SAMx7x part has been available to confirm the
        /// primary does read zero on one, so the eleven SAMx7x rows in <see cref="ChipTable"/> rest
        /// on that assumption; a SAMx7x that reports itself unsupported is the symptom to expect if
        /// it does not hold.
        /// </para>
        /// </summary>
        /// <param name="monitor">Connected monitor to read through.</param>
        /// <param name="chipId">
        /// Receives the CIDR. Never null — this method always reads at least once — so a 0 here is a
        /// register that answered zero, not one that went unread.
        /// </param>
        /// <param name="extChipId">
        /// Receives the EXID beside whichever address answered, or null when neither did and no EXID
        /// was therefore looked for.
        /// </param>
        private static void ReadChipIdPair(SambaMonitor monitor, out uint? chipId, out uint? extChipId)
        {
            extChipId = null;

            uint cidr = monitor.ReadWord(ChipIdAddress);
            uint cidrAddress = ChipIdAddress;
            if (cidr == 0)
            {
                cidr = monitor.ReadWord(ChipIdAddressAlt);
                cidrAddress = ChipIdAddressAlt;
            }

            chipId = cidr;

            // Beside whichever address answered, never the other one.
            if (cidr != 0)
                extChipId = monitor.ReadWord(cidrAddress + ExtendedChipIdOffset);
        }
    }
}

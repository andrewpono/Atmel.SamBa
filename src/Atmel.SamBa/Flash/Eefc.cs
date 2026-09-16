using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;


namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// Enhanced Embedded Flash Controller (SAM3/SAM4/SAM7L/SAM9XE/SAMx7x).
    /// The page latch is loaded with batched <c>W#</c> writes instead of an
    /// on-target word-copy applet.
    /// </summary>
    internal sealed class Eefc : EfcFamilyController
    {
        // EEFC_FSR error bits.
        private const uint CommandErrorMask = 1u << 1;   // FCMDE
        private const uint LockErrorMask = 1u << 2;      // FLOCKE

        /// <summary>Distance between consecutive plane register blocks.</summary>
        private const uint PlaneStride = 0x200;

        /// <summary>
        /// Flash result register (EEFC_FRR), offset from a plane's register block base. The EFC
        /// has no counterpart, which is why it is not part of
        /// <see cref="EfcFamilyController.PlaneRegisters"/>.
        /// </summary>
        private const uint FrrOffset = 0x0C;

        private const int PagesPerErase = 8;

        /// <summary>
        /// EPA argument bits [1:0] select the erase-group size; 1 means eight pages, so this must
        /// stay in step with <see cref="PagesPerErase"/>.
        /// </summary>
        private const uint EraseEightPages = 0x1;

        /// <summary>Bits per FRR read; lock regions past this need another read to reach.</summary>
        private const int LockBitsPerResultWord = RegisterBits;

        /// <summary>The security bit is GPNVM bit 0.</summary>
        private const int SecurityGpnvmBitIndex = 0;

        /// <summary>Longest plausible FL_NB_PLANE; a descriptor claiming more is garbage.</summary>
        private const uint MaxDescriptorPlanes = 4;

        /// <summary>Longest plausible FL_NB_LOCK; a descriptor claiming more is garbage.</summary>
        private const uint MaxDescriptorLockRegions = 1024;

        // SAM3 errata: the flash wait state (FMR bits 8-11, FWS) must be 6 while the ROM programs
        // flash. Programmed on every EEFC part rather than just SAM3 — surplus wait states cost
        // read speed during programming, never correctness.
        private const int WaitStateShift = 8;
        private const uint FlashWaitStates = 6;

        /// <summary>
        /// EEFC_FCR command opcodes, with their datasheet mnemonics. Consecutive values, not
        /// flags — never combine them. Consts rather than an enum for the reason on
        /// <see cref="EfcFamilyController.WriteFcr"/>.
        /// </summary>
        private static class Cmd
        {
            public const byte GetDescriptor = 0x0;         // GETD
            public const byte WritePage = 0x1;             // WP
            public const byte EraseAndWritePage = 0x3;     // EWP
            public const byte EraseAll = 0x5;              // EA
            public const byte ErasePages = 0x7;            // EPA
            public const byte SetLockBit = 0x8;            // SLB
            public const byte ClearLockBit = 0x9;          // CLB
            public const byte GetLockBit = 0xA;            // GLB
            public const byte SetGpnvmBit = 0xB;           // SGPB
            public const byte ClearGpnvmBit = 0xC;         // CGPB
            public const byte GetGpnvmBit = 0xD;           // GGPB
            public const byte StartUniqueIdRead = 0xE;     // STUI
            public const byte StopUniqueIdRead = 0xF;      // SPUI
        }

        internal Eefc(SambaMonitor monitor, ChipRecord chip)
            : base(monitor, chip, chip.FlashControllerBaseAddress, PlaneStride)
        {
        }

        /// <summary>Address of <paramref name="plane"/>'s result register, which holds command output.</summary>
        private static uint Frr(PlaneRegisters plane) => plane.BaseAddress + FrrOffset;

        /// <summary>
        /// True: the ROM monitors on these parts answer block reads of flash — and of the boot
        /// memory at address 0, which remaps it — with all zeros, so memory is read word-by-word.
        /// Confirmed on hardware; not confined to any one family among them.
        /// </summary>
        internal override bool ReadRequiresWords => true;

        /// <summary>Applies the flash wait-state errata workaround to every plane.</summary>
        protected override void OnInitialize()
        {
            Monitor.WriteWord(Plane0.Fmr, FlashWaitStates << WaitStateShift);
            if (PlaneCount == 2)
                Monitor.WriteWord(Plane1.Fmr, FlashWaitStates << WaitStateShift);
        }

        /// <summary>
        /// Reads the flash descriptor the EEFC reports for itself — the GETD command, answered one
        /// word at a time through FRR. One descriptor per EEFC: a two-controller part carries two,
        /// each describing its own bank, so their sizes and lock counts are summed and their page
        /// sizes must agree. The plane count returned is the number of controllers queried, not the
        /// descriptor's FL_NB_PLANE — a dual-bank part behind one EEFC (ATSAM3SD8) reports 2 there
        /// while its whole flash is described by the single descriptor read here, so summing what
        /// FL_NB_PLANE says would double it. On a part identified by family fallback the count is
        /// unknown, so the second EEFC's existence is inferred from a single FSR read — an absent
        /// controller's address reads as reserved-register zero (the same argument
        /// <c>ChipIdentifier.ReadChipIdPair</c> rests on), while a real idle EEFC has FRDY set.
        /// <para>
        /// Two descriptors must agree on all three figures, not merely sum to something plausible.
        /// The flash layer addresses a second controller by halving the totals
        /// (<see cref="EfcFamilyController.PagesPerPlane"/>,
        /// <see cref="EfcFamilyController.LockRegionsPerPlane"/>), so an asymmetric pair has no
        /// representation here at all: summing it would name the wrong page for every write above the
        /// boundary. No such part is known — this is the garbage answer being refused, not a
        /// configuration being declined.
        /// </para>
        /// <para>
        /// Null on any implausible answer rather than an exception: the caller's response to a part
        /// without a readable descriptor is to fall back to the table, not to fail. A monitor whose
        /// EEFC predates GETD reports FCMDE, which reads here as "no descriptor".
        /// </para>
        /// </summary>
        internal override DeviceFlashGeometry? ReadDeviceGeometry()
        {
            bool provisional = Chip.PageCount == 0;
            bool secondPlane = provisional
                ? (Monitor.ReadWord(Plane1.Fsr) & ReadyMask) != 0
                : PlaneCount == 2;
            int planes = secondPlane ? 2 : 1;

            long totalSize = 0;
            uint pageSize = 0;
            uint firstSize = 0;
            uint firstLocks = 0;
            int lockRegions = 0;
            for (int plane = 0; plane < planes; plane++)
            {
                PlaneRegisters registers = plane == 0 ? Plane0 : Plane1;
                if (!TryReadDescriptor(registers, out uint planeSize, out uint planePageSize, out uint planeLocks))
                    return null;

                if (plane == 0)
                {
                    pageSize = planePageSize;
                    firstSize = planeSize;
                    firstLocks = planeLocks;
                }
                else if (planePageSize != pageSize || planeSize != firstSize || planeLocks != firstLocks)
                {
                    return null;        // the two controllers disagree; there is no even split to take
                }

                totalSize += planeSize;
                lockRegions += (int)planeLocks;
            }

            if (pageSize == 0 || totalSize % pageSize != 0)
                return null;

#if DEBUG
            //pageSize *= 2; // test mismatch
            //lockRegions += 1; // test unusable geometry
#endif

            return new DeviceFlashGeometry((int)(totalSize / pageSize), (int)pageSize, planes, lockRegions);
        }

        /// <summary>
        /// One EEFC's GETD exchange. The FRR word order is the datasheet's, printed as a table in
        /// each EEFC chapter (SAM4E 20.4.3.1, SAM3S/3X/3U/4S and SAM7L alike): FL_ID, FL_SIZE
        /// (bytes), FL_PAGE_SIZE (bytes), FL_NB_PLANE, one size per plane, FL_NB_LOCK, then one size
        /// per lock region. Reading stops at FL_NB_LOCK — the lock-region sizes add nothing the count does
        /// not, and per the datasheet a descriptor needs no draining: FRR reads past the end return
        /// 0 and the next command starts fresh. Waits are per-plane and non-throwing on purpose;
        /// see <see cref="ReadDeviceGeometry"/> for why failure here means "no descriptor".
        /// </summary>
        private bool TryReadDescriptor(
            PlaneRegisters plane, out uint flashSize, out uint pageSize, out uint lockRegions)
        {
            flashSize = 0;
            pageSize = 0;
            lockRegions = 0;

            if (!TryWaitReady(plane, out _))
                return false;
            WriteFcr(plane, Cmd.GetDescriptor);
            if (!TryWaitReady(plane, out uint fsr) || (fsr & CommandErrorMask) != 0)
                return false;

            Monitor.ReadWord(Frr(plane));                       // FL_ID — not used for anything here
            flashSize = Monitor.ReadWord(Frr(plane));
            pageSize = Monitor.ReadWord(Frr(plane));
            uint planesInDescriptor = Monitor.ReadWord(Frr(plane));
            if (planesInDescriptor == 0 || planesInDescriptor > MaxDescriptorPlanes)
                return false;

            long planeSizeSum = 0;
            for (uint i = 0; i < planesInDescriptor; i++)
                planeSizeSum += Monitor.ReadWord(Frr(plane));
            if (planeSizeSum != flashSize)
                return false;                                   // internally inconsistent — garbage

            lockRegions = Monitor.ReadWord(Frr(plane));
            return lockRegions > 0 && lockRegions <= MaxDescriptorLockRegions;
        }

        /// <summary>
        /// Polls one specific plane's FSR until FRDY, handing back the read that saw it — the FSR
        /// error bits clear on read, so the caller must inspect the same word the wait consumed.
        /// The shared <see cref="EfcFamilyController.WaitFsr(string)"/> is no substitute here: it
        /// polls planes according to the record's plane count, which is 0 on a provisional record,
        /// and it throws where the descriptor probe wants a quiet false.
        /// </summary>
        private bool TryWaitReady(PlaneRegisters plane, out uint fsr)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                fsr = Monitor.ReadWord(plane.Fsr);
                if ((fsr & ReadyMask) != 0)
                    return true;
                if (stopwatch.Elapsed >= SambaMonitor.NormalTimeout)
                    return false;
                Thread.Sleep(PollInterval);
            }
        }

        protected override byte WritePageCommand => AutoEraseEnabled ? Cmd.EraseAndWritePage : Cmd.WritePage;

        protected override byte SetGpnvmCommand => Cmd.SetGpnvmBit;

        protected override byte ClearGpnvmCommand => Cmd.ClearGpnvmBit;

        protected override byte SetLockCommand => Cmd.SetLockBit;

        protected override byte ClearLockCommand => Cmd.ClearLockBit;

        /// <summary>
        /// Sets the security bit, which on this family is GPNVM 0 — so it goes through the ordinary
        /// set-GPNVM command with that bit index rather than a dedicated opcode.
        /// </summary>
        protected override void ApplySecurityBit() =>
            Execute(Plane0, Cmd.SetGpnvmBit, (uint)SecurityGpnvmBitIndex, "security-bit set (SGPB)");

        protected override void ThrowIfFsrError(uint fsr, string operation)
        {
            if ((fsr & CommandErrorMask) != 0)
                throw new SamBaFlashCommandException("EEFC command failed.", operation, isCommandError: true);
            if ((fsr & LockErrorMask) != 0)
                throw new SamBaFlashCommandException("EEFC lock error.", operation, isLockError: true);
        }

        public override void EraseAll(uint offset)
        {
            if (offset == 0)
            {
                EraseAllPlanes(Cmd.EraseAll);
            }
            else
            {
                // Partial erase must start on an erase-page-group boundary.
                if (offset % (uint)(PageSize * PagesPerErase) != 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(offset), $"Offset must be a multiple of {PageSize * PagesPerErase} bytes for a partial erase.");
                // Range-checked like the NVMCTRL erase: past the end the loop below would silently
                // erase nothing, leaving the failure to surface later, blamed on the write.
                if (offset > FlashSize)
                    throw new ArgumentOutOfRangeException(
                        nameof(offset), $"Erase offset lies past the end of the {FlashSize}-byte flash.");

                for (uint pageNum = offset / (uint)PageSize; pageNum < (uint)PageCount; pageNum += PagesPerErase)
                {
                    if (PlaneCount == 1 || pageNum < (uint)PagesPerPlane)
                    {
                        Execute(Plane0, Cmd.ErasePages, pageNum | EraseEightPages, nameof(EraseAll));
                    }
                    else
                    {
                        // Plane-local page number, subtracted the same way WriteBlock and
                        // ApplyLockRegions do it.
                        Execute(Plane1, Cmd.ErasePages, (pageNum - (uint)PagesPerPlane) | EraseEightPages, nameof(EraseAll));
                    }
                }
            }
        }

        public override void EnsureWriteSupported(uint offset, int length)
        {
            // The EEFC erase-and-write-page commands (EWP/EWPL) only erase within the small sectors
            // at the bottom of flash. Past those a page must be erased explicitly beforehand, so an
            // auto-erasing write that crosses the boundary fails part-way through on SAM4 / SAMx7x
            // parts with a bare controller error. Reject it up front with actionable guidance.
            const int SmallSectorKb = 8;
            const int SmallSectorCount = 2;
            const int EraseAndWriteLimitKb = SmallSectorCount * SmallSectorKb;
            const long EraseAndWriteLimitBytes = EraseAndWriteLimitKb * 1024L;

            if (AutoEraseEnabled && offset + (long)length > EraseAndWriteLimitBytes)
                throw new SamBaFlashCommandException(
                    $"{Name}: an auto-erasing write past {EraseAndWriteLimitKb} KB (offset " +
                    $"0x{offset:X} + {length} bytes) is unsupported — the EEFC erase-and-write-page " +
                    $"command only erases within the first {SmallSectorCount} flash sectors of " +
                    $"{SmallSectorKb} KB. Erase first (BulkErase = true) so pages are programmed " +
                    "without per-page auto-erase.", "auto-erasing write (EWP)", isUnsupported: true);
        }

        /// <summary>
        /// Reads every lock bit, one GLB command per region — a round trip apiece rather than one per
        /// plane, which the FRR paging in <see cref="ReadLockRegion"/> would otherwise allow.
        /// Deliberate, and expensive: see <c>Known slow paths</c> in <c>docs/DESIGN.md</c> for the
        /// cheaper sequence, what it assumes, and what has to be confirmed on hardware before it can
        /// be taken.
        /// </summary>
        public override bool[] GetLockRegions()
        {
            var regions = new bool[LockRegionCount];

            for (int region = 0; region < LockRegionCount; region++)
                regions[region] = ReadLockRegion(region);

            return regions;
        }

        protected override bool ReadLockRegion(int region)
        {
            bool upperPlane = PlaneCount == 2 && region >= LockRegionsPerPlane;
            PlaneRegisters plane = upperPlane ? Plane1 : Plane0;
            int bit = upperPlane ? region - LockRegionsPerPlane : region;

            Execute(plane, Cmd.GetLockBit, 0, nameof(ReadLockRegion));

            // GLB reports the plane's lock bits in successive FRR reads; page forward to the
            // word holding this region's bit.
            uint frr = Monitor.ReadWord(Frr(plane));
            while (bit >= LockBitsPerResultWord)
            {
                frr = Monitor.ReadWord(Frr(plane));
                bit -= LockBitsPerResultWord;
            }

            return (frr & (1u << bit)) != 0;
        }

        public override bool GetSecurity() => GetGpnvmBit(SecurityGpnvmBitIndex, nameof(GetSecurity));

        /// <summary>
        /// The boot source, from the boot-mode GPNVM bit (GPNVM1 on most EEFC parts, GPNVM3 on the
        /// SAM9XE) — one GGPB command, since the EEFC has no live status bit for it.
        /// <para>
        /// Every listed EEFC family carries a bit index, so the fixed-source branch is reached only by a
        /// part placed here through family fallback, whose provisional record has none. Flash is the
        /// assumption there rather than a reading: it keeps a request for the source a caller actually
        /// wants from throwing on a part this library could not identify, and an update never asks
        /// anyway (<see cref="FlashController.CanSelectBootSource"/> is false, so the step is skipped).
        /// </para>
        /// </summary>
        public override SamBaChipBootSource GetBootSource()
        {
            if (!CanSelectBootSource)
                return SamBaChipBootSource.Flash;

            return GetGpnvmBit(Chip.BootGpnvmBitIndex.Value, nameof(GetBootSource))
                ? SamBaChipBootSource.Flash
                : SamBaChipBootSource.Rom;
        }

        /// <summary>
        /// Runs GGPB and returns GPNVM bit <paramref name="bitIndex"/> from the result register.
        /// Unlike the EFC, the EEFC does not expose GPNVM state as live status bits, so reading one
        /// costs a command. <paramref name="operation"/> names the caller for diagnostics.
        /// </summary>
        private bool GetGpnvmBit(int bitIndex, string operation)
        {
            Execute(Plane0, Cmd.GetGpnvmBit, 0, operation);
            return (Monitor.ReadWord(Frr(Plane0)) & (1u << bitIndex)) != 0;
        }

        public override IReadOnlyList<uint> GetUniqueId()
        {
            int words = Chip.UniqueIdWords;
            if (words == 0)
                return Array.Empty<uint>();

            WaitFsr("unique-id read start (STUI)");

            // Hand-rolled with raw WriteFcr rather than Execute, deliberately: STUI maps the
            // unique id into the flash memory region and drops FRDY; it must NOT be waited on
            // until SPUI, so the bracket here spans the whole STUI..SPUI pair. No other flash
            // read is allowed in between.
            WriteFcr(Plane0, Cmd.StartUniqueIdRead);

            var id = new uint[words];
            for (int w = 0; w < words; w++)
                id[w] = Monitor.ReadWord(FlashAddress + (uint)(w * BytesPerWord));

            WriteFcr(Plane0, Cmd.StopUniqueIdRead);
            WaitFsr("unique-id read end (SPUI)");

            // Wrapped, not returned bare: the caller above holds the result for the whole session,
            // so one instance reaches every reader and a cast back to uint[] would let one of them
            // rewrite what the others see.
            return Array.AsReadOnly(id);
        }
    }
}

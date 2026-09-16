using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System;


namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// Legacy MC/EFC flash controller (AT91SAM7S/SE/X/XC). The page latch is loaded with batched
    /// <c>W#</c> writes instead of an on-target word-copy applet — the register-level approach
    /// AT91 Samba Lite 1.2 used.
    /// </summary>
    internal sealed class Efc : EfcFamilyController
    {
        /// <summary>Base address of the Memory Controller register map.</summary>
        private const uint McBaseAddress = 0xFFFFFF00;

        /// <summary>
        /// Offset of the EFC user interface within that map. The EFC is a block inside the Memory
        /// Controller rather than a peripheral of its own, which is why its register offsets start
        /// here instead of at 0 the way the EEFC's do.
        /// </summary>
        private const uint EfcInterfaceOffset = 0x60;

        /// <summary>Address of the first plane's register block, MC_FMR. Fixed on every EFC part.</summary>
        private const uint Plane0BaseAddress = McBaseAddress + EfcInterfaceOffset;

        /// <summary>
        /// Distance between consecutive plane register blocks, which puts the second plane's
        /// interface at MC+0x70.
        /// </summary>
        private const uint PlaneStride = 0x10;

        // MC_FSR error and status bits (SAM7 datasheet). Unlike the EEFC there is no
        // command-error bit here.
        private const uint LockErrorMask = 1u << 2;      // LOCKE
        private const uint ProgramErrorMask = 1u << 3;   // PROGE
        private const uint SecurityMask = 1u << 4;       // SECURITY

        // MC_FSR reports lock and GPNVM state as runs of single bits: lock region N at bit 16+N,
        // GPNVM<n> at bit 8+n.
        private const int FirstLockStatusBit = 16;
        private const int FirstGpnvmStatusBit = 8;

        /// <summary>
        /// Lock-region status runs from <see cref="FirstLockStatusBit"/> to the top of MC_FSR, so
        /// only that many regions per plane can be reported; past that
        /// <see cref="GetLockRegions"/>'s shifts would silently wrap.
        /// </summary>
        private const int MaxLockRegionsPerPlane = RegisterBits - FirstLockStatusBit;

        /// <summary>
        /// Suppresses the automatic erase ahead of a page program (FMR bit 7, NEBP);
        /// clear = erase before program.
        /// </summary>
        private const uint NoEraseBeforeProgramMask = 1u << 7;

        // Flash-write timing in master-clock cycles (FMR bits 16-23, FMCN).
        private const int FlashCyclesShift = 16;
        private const uint FlashCyclesMask = 0xFF;

        /// <summary>
        /// Timing value programmed when the ROM left FMCN unconfigured.
        /// 0x34 (52) assumes the ~48 MHz MCK the SAM-BA ROM configures.
        /// </summary>
        private const uint DefaultFlashCycles = 0x34;

        /// <summary>
        /// MC_FCR command opcodes, with their datasheet mnemonics. Sparse values, not flags:
        /// <c>WritePage | SetLockBit</c> would be 0x3, which is WPL (write page and lock) — a
        /// different command. Never combine them. Consts rather than an enum for the reason on
        /// <see cref="EfcFamilyController.WriteFcr"/>.
        /// </summary>
        private static class Cmd
        {
            public const byte WritePage = 0x1;       // WP
            public const byte SetLockBit = 0x2;      // SLB
            public const byte ClearLockBit = 0x4;    // CLB
            public const byte EraseAll = 0x8;        // EA
            public const byte SetGpnvmBit = 0xB;     // SGPB
            public const byte ClearGpnvmBit = 0xD;   // CGPB
            public const byte SetSecurityBit = 0xF;  // SSB
        }

        internal Efc(SambaMonitor monitor, ChipRecord chip)
            : base(monitor, chip, Plane0BaseAddress, PlaneStride)
        {
            // NotSupportedException, not InvalidOperationException: this says the record's geometry is
            // one this controller cannot drive, which is a different claim from "the tables and the
            // code disagree" — see the two rules in docs/DESIGN.md. Reachable from a table row rather
            // than only from a code gap, and one row did reach it: the emulated-bootloader row now
            // disabled in ChipTable claimed 32 lock regions on a single plane, so identifying that
            // device threw from here. No live row comes near the limit — the most any carries is 16
            // per plane — so this stands against the next row someone writes.
            if (LockRegionsPerPlane > MaxLockRegionsPerPlane)
                throw new NotSupportedException(
                    $"{Name}: {LockRegionsPerPlane} lock regions per plane exceeds the " +
                    $"{MaxLockRegionsPerPlane} MC_FSR can report.");
        }

        /// <summary>
        /// One FMR pass over the init-time concerns: clears NEBP so the hardware mode matches the
        /// <see cref="FlashController.AutoEraseEnabled"/> default, and programs a safe flash-write
        /// timing if the ROM left FMCN unconfigured, so flash writes are timed correctly (the ROM
        /// does not always configure it; AT91 Samba Lite sets it defensively). The timing is
        /// init-time work: once programmed it is nonzero and every later FMR update preserves it.
        /// </summary>
        protected override void OnInitialize()
        {
            WaitFsr(nameof(OnInitialize));
            uint fmr = Monitor.ReadWord(Plane0.Fmr) & ~NoEraseBeforeProgramMask;
            if (((fmr >> FlashCyclesShift) & FlashCyclesMask) == 0)
                fmr |= DefaultFlashCycles << FlashCyclesShift;

            WriteFmrPlanes(fmr);
        }

        protected override void OnSetAutoErase(bool enable)
        {
            // EFC erase-before-program is a mode bit, not a per-command choice. The FMCN timing
            // sharing the register was programmed at initialization; the RMW preserves it.
            WaitFsr(nameof(SetAutoErase));
            uint fmr = Monitor.ReadWord(Plane0.Fmr);
            if (enable)
                fmr &= ~NoEraseBeforeProgramMask;
            else
                fmr |= NoEraseBeforeProgramMask;

            WriteFmrPlanes(fmr);
        }

        /// <summary>
        /// Writes <paramref name="fmr"/> to every plane's mode register. Plane 0's controller was
        /// waited on by the caller when it read the value; plane 1's gets its own wait here.
        /// </summary>
        private void WriteFmrPlanes(uint fmr)
        {
            Monitor.WriteWord(Plane0.Fmr, fmr);
            if (PlaneCount == 2)
            {
                WaitFsr("FMR write");
                Monitor.WriteWord(Plane1.Fmr, fmr);
            }
        }

        protected override byte WritePageCommand => Cmd.WritePage;

        protected override byte SetGpnvmCommand => Cmd.SetGpnvmBit;

        protected override byte ClearGpnvmCommand => Cmd.ClearGpnvmBit;

        protected override byte SetLockCommand => Cmd.SetLockBit;

        protected override byte ClearLockCommand => Cmd.ClearLockBit;

        /// <summary>Sets the security bit with the EFC's dedicated command, which takes no argument.</summary>
        protected override void ApplySecurityBit() =>
            Execute(Plane0, Cmd.SetSecurityBit, 0, "security-bit set (SSB)");

        protected override void ThrowIfFsrError(uint fsr, string operation)
        {
            if ((fsr & LockErrorMask) != 0)
                throw new SamBaFlashCommandException("EFC lock error.", operation, isLockError: true);
            if ((fsr & ProgramErrorMask) != 0)
                throw new SamBaFlashCommandException("EFC programming error.", operation, isCommandError: true);
        }

        public override void EraseAll(uint offset)
        {
            if (offset != 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "EFC parts only support a full erase (offset 0).");

            EraseAllPlanes(Cmd.EraseAll);
        }

        // The getters below read MC_FSR directly: it exposes lock, security, and GPNVM state as
        // live status bits, so no command is issued and no ready-wait is needed — unlike the
        // EEFC, which must run its get-bit commands and read the result register. Reading MC_FSR
        // also clears LOCKE/PROGE, so a getter would swallow an error left pending by an earlier
        // command; none ever is, because every command goes through the Execute bracket, which
        // waits its own command out and throws before returning.

        public override bool[] GetLockRegions()
        {
            var regions = new bool[LockRegionCount];

            uint fsr0 = Monitor.ReadWord(Plane0.Fsr);
            uint fsr1 = PlaneCount == 2 ? Monitor.ReadWord(Plane1.Fsr) : 0;

            for (int region = 0; region < LockRegionCount; region++)
            {
                bool upperPlane = PlaneCount == 2 && region >= LockRegionsPerPlane;
                uint fsr = upperPlane ? fsr1 : fsr0;
                int bit = FirstLockStatusBit + (upperPlane ? region - LockRegionsPerPlane : region);

                regions[region] = (fsr & (1u << bit)) != 0;
            }

            return regions;
        }

        protected override bool ReadLockRegion(int region)
        {
            bool upperPlane = PlaneCount == 2 && region >= LockRegionsPerPlane;
            uint fsr = Monitor.ReadWord((upperPlane ? Plane1 : Plane0).Fsr);
            int bit = FirstLockStatusBit + (upperPlane ? region - LockRegionsPerPlane : region);

            return (fsr & (1u << bit)) != 0;
        }

        public override bool GetSecurity() => (Monitor.ReadWord(Plane0.Fsr) & SecurityMask) != 0;

        /// <summary>
        /// The boot source, read from the boot-mode GPNVM bit in <c>MC_FSR</c> where there is one.
        /// <para>
        /// Where there is none the answer is flash, not the ROM. That is the small SAM7S variants, and
        /// they have no bit precisely because they always boot flash: an erase copies SAM-BA into flash
        /// and it relocates itself to RAM to run, so there is no ROM boot to select away from. This
        /// method used to answer <c>false</c> — "boots the ROM" — for those parts, which inverted all
        /// three of the boot members on them, and reading <c>CanBootFlash</c> as "can boot flash" is how
        /// that happened; see <see cref="FlashController.CanSelectBootSource"/>.
        /// </para>
        /// </summary>
        public override SamBaChipBootSource GetBootSource()
        {
            if (!CanSelectBootSource)
                return SamBaChipBootSource.Flash;

            // On a typical SAM7 the boot bit is GPNVM2, so this reads MC_FSR bit 10.
            bool bitSet = (Monitor.ReadWord(Plane0.Fsr)
                & (1u << (FirstGpnvmStatusBit + Chip.BootGpnvmBitIndex.Value))) != 0;
            return bitSet ? SamBaChipBootSource.Flash : SamBaChipBootSource.Rom;
        }
    }
}

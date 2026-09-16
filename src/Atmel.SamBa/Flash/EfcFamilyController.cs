using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System;
using System.Diagnostics;
using System.Threading;


namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// Shared skeleton for the EFC-family flash controllers (legacy SAM7 MC/EFC and SAM3/SAM4
    /// EEFC): an FMR/FCR/FSR register trio per plane, 0x5A-keyed FCR commands, and FRDY polling.
    /// NVMCTRL parts (D2x/D5x) use a register-block + CMDEX model and do not belong here.
    /// </summary>
    internal abstract class EfcFamilyController : FlashController
    {
        /// <summary>
        /// Write-protection key that must accompany every flash command (FCR bits 24-31, FKEY);
        /// commands written without it are ignored.
        /// </summary>
        protected const uint CommandKey = 0x5A;

        /// <summary>
        /// Status bit reporting the controller idle and able to accept a command (FSR bit 0, FRDY).
        /// </summary>
        protected const uint ReadyMask = 1u << 0;

        /// <summary>
        /// Width of a controller register in bits — the ceiling for any bit index, and the length
        /// of a run of status bits reported in one register.
        /// </summary>
        protected const int RegisterBits = 32;

        /// <summary>
        /// Initializes the plane register addresses from the first plane's register block base and
        /// the stride between consecutive plane blocks (0x10 on EFC parts, 0x200 on EEFC). Two
        /// planes is the most either family's parts have, and the most this class handles.
        /// </summary>
        protected EfcFamilyController(SambaMonitor monitor, ChipRecord chip, uint plane0BaseAddress, uint planeStride)
            : base(monitor, chip)
        {
            Plane0 = new PlaneRegisters(plane0BaseAddress);
            Plane1 = new PlaneRegisters(plane0BaseAddress + planeStride);
        }

        /// <summary>Register addresses of the first (or only) plane's controller.</summary>
        protected PlaneRegisters Plane0 { get; }

        /// <summary>Register addresses of the second plane's controller (2-plane parts only).</summary>
        protected PlaneRegisters Plane1 { get; }

        /// <summary>
        /// Pages served by one plane's controller. Pages are split evenly across the planes, and a
        /// 1-plane part's single controller serves all of them.
        /// </summary>
        protected int PagesPerPlane => PlaneCount == 2 ? PageCount / 2 : PageCount;

        /// <summary>
        /// Lock regions served by one plane's controller — the counterpart of
        /// <see cref="PagesPerPlane"/> for lock state.
        /// </summary>
        protected int LockRegionsPerPlane => PlaneCount == 2 ? LockRegionCount / 2 : LockRegionCount;

        /// <summary>The FCR command that commits a loaded page latch (plain or auto-erasing write).</summary>
        protected abstract byte WritePageCommand { get; }

        /// <summary>The FCR command that sets a GPNVM bit, which the boot-source change uses.</summary>
        protected abstract byte SetGpnvmCommand { get; }

        /// <summary>The FCR command that clears a GPNVM bit.</summary>
        protected abstract byte ClearGpnvmCommand { get; }

        /// <summary>The FCR command that locks one region, given the region's first page.</summary>
        protected abstract byte SetLockCommand { get; }

        /// <summary>The FCR command that unlocks one region.</summary>
        protected abstract byte ClearLockCommand { get; }

        /// <summary>
        /// Throws <see cref="SamBaFlashCommandException"/> when <paramref name="fsr"/> carries the
        /// family's error bits; <paramref name="operation"/> names the operation being waited on.
        /// </summary>
        protected abstract void ThrowIfFsrError(uint fsr, string operation);

        /// <summary>
        /// Runs one keyed FCR command to completion: waits for FRDY on every plane, writes the
        /// command to <paramref name="plane"/>'s FCR, then waits the command out — the counterpart
        /// of <c>NvmFamilyController.Execute</c>.
        /// <para>
        /// Bracketing both sides is what makes a command self-sufficient. The leading wait
        /// re-establishes idle locally instead of trusting every earlier path to have waited its
        /// own command out; the trailing wait surfaces a failure here, attributed to
        /// <paramref name="operation"/>, instead of leaving it for whoever polls next — or for no
        /// one, since reading a status register clears its error bits on this family.
        /// </para>
        /// </summary>
        protected void Execute(PlaneRegisters plane, byte cmd, uint arg, string operation) =>
            Execute(plane, cmd, arg, operation, SambaMonitor.NormalTimeout);

        /// <summary>
        /// <see cref="Execute(PlaneRegisters, byte, uint, string)"/> with a caller-chosen budget
        /// for the trailing wait — erase-all needs <see cref="SambaMonitor.ChipEraseTimeout"/>
        /// there. The leading wait keeps the normal budget: with every command self-sufficient,
        /// nothing is left in flight when the next one begins, so it has nothing long to wait for.
        /// </summary>
        protected void Execute(PlaneRegisters plane, byte cmd, uint arg, string operation, TimeSpan budget)
        {
            WaitFsr(operation);
            WriteFcr(plane, cmd, arg);
            WaitFsr(operation, budget);
        }

        /// <summary>
        /// Programs one page. A write block is one page on these families — the erase-and-write-page
        /// commands erase exactly what they write — so the block index is the page number.
        /// </summary>
        public override void WriteBlock(int block, byte[] data, int dataOffset)
        {
            ValidateBlock(block);

            // Idle is required before the latch load, not merely before the commit: the latch is
            // flash's memory-mapped face, and loading it over a command still in flight is
            // undefined. Execute then re-checks in a single poll — the same redundancy the NVMCTRL
            // write path accepts between its buffer load and commit.
            WaitFsr(nameof(WriteBlock));
            LoadLatch(FlashAddress + (uint)block * (uint)PageSize, data, dataOffset, PageSize);

            if (PlaneCount == 2 && block >= PagesPerPlane)
                Execute(Plane1, WritePageCommand, (uint)(block - PagesPerPlane), nameof(WriteBlock));
            else
                Execute(Plane0, WritePageCommand, (uint)block, nameof(WriteBlock));
        }

        /// <summary>
        /// Issues <paramref name="eraseAllCommand"/> on every plane, each waited out with the
        /// monitor's <see cref="SambaMonitor.ChipEraseTimeout"/> — a 1 MB plane takes up to 18 s
        /// to erase, which the default budget would abandon after a second.
        /// </summary>
        protected void EraseAllPlanes(byte eraseAllCommand)
        {
            Execute(Plane0, eraseAllCommand, 0, nameof(EraseAll), Monitor.ChipEraseTimeout);

            // Erase the upper plane through its own controller, matching the per-plane model used
            // by WriteBlock. Issuing the second erase-all to the first plane's FCR with a page
            // argument would never reach plane 1 on a 2-plane part.
            if (PlaneCount == 2)
                Execute(Plane1, eraseAllCommand, 0, nameof(EraseAll), Monitor.ChipEraseTimeout);
        }

        /// <summary>
        /// Points the boot source at <paramref name="source"/> by setting or clearing the chip's
        /// boot-mode GPNVM bit; does nothing when that already is the source.
        /// <para>
        /// Which is tested before capability, deliberately, and is the rule every family in this
        /// library follows: a request for the source a part is already using is satisfied by doing
        /// nothing, whether or not the part could have moved. Only an unreachable source throws. On a
        /// part with no boot-mode bit that makes <see cref="FlashController.GetBootSource"/> the
        /// authority on which of the two requests is which — it reports the fixed source, and a
        /// request for the other one is the one that cannot be honoured. Since the fixed source is
        /// always flash, the unreachable request there is always <see cref="SamBaChipBootSource.Rom"/>.
        /// </para>
        /// </summary>
        /// <exception cref="SamBaFlashCommandException">
        /// The part's boot source is fixed (<see cref="FlashController.CanSelectBootSource"/> is false)
        /// and <paramref name="source"/> is not the one it is fixed at.
        /// </exception>
        protected override void ApplyBootSource(SamBaChipBootSource source)
        {
            if (source == GetBootSource())
                return;

            if (!CanSelectBootSource)
                throw new SamBaFlashCommandException(
                    $"{Name} always boots from flash; it cannot be pointed at the SAM-BA ROM.",
                    "boot-source change", isUnsupported: true);

            Execute(Plane0, source == SamBaChipBootSource.Flash ? SetGpnvmCommand : ClearGpnvmCommand,
                (uint)Chip.BootGpnvmBitIndex.Value, "boot-source change");
        }

        /// <summary>
        /// Brings lock regions to <paramref name="locked"/> — every region when
        /// <paramref name="regions"/> is null, otherwise just the listed ones — by issuing the
        /// family's set/clear-lock-bit command for each region that differs from the current state.
        /// The all-regions form reads the whole lock state in one sweep; the subset form reads only
        /// the regions it may change, which matters on the EEFC, where each region read is a
        /// command round trip of its own.
        /// </summary>
        protected override void ApplyLockRegions(bool locked, int[] regions)
        {
            if (regions == null)
            {
                bool[] current = GetLockRegions();

                for (int region = 0; region < LockRegionCount; region++)
                {
                    if (current[region] != locked)
                        IssueLockCommand(region, locked);
                }

                return;
            }

            foreach (int region in regions)
            {
                if (ReadLockRegion(region) != locked)
                    IssueLockCommand(region, locked);
            }
        }

        /// <summary>
        /// Issues the set- or clear-lock-bit command for one region, addressed to the plane that
        /// owns it, with the region's first page — counted within that plane — as the argument.
        /// </summary>
        private void IssueLockCommand(int region, bool locked)
        {
            byte command = locked ? SetLockCommand : ClearLockCommand;
            bool upperPlane = PlaneCount == 2 && region >= LockRegionsPerPlane;
            PlaneRegisters plane = upperPlane ? Plane1 : Plane0;
            uint page = FirstPageOfRegion(upperPlane ? region - LockRegionsPerPlane : region);

            Execute(plane, command, page, nameof(ApplyLockRegions));
        }

        /// <summary>
        /// First page of lock region <paramref name="region"/>, counted within its own plane — the
        /// page number the set/clear-lock-bit commands take as their argument.
        /// </summary>
        protected uint FirstPageOfRegion(int region) => (uint)(region * PageCount / LockRegionCount);

        /// <summary>
        /// Current lock state of one region, read without paying for a scan of all of them — how
        /// the subset path skips regions already in the requested state.
        /// </summary>
        protected abstract bool ReadLockRegion(int region);

        /// <summary>Polls the FSRs until every plane reports FRDY, up to <see cref="SambaMonitor.NormalTimeout"/>.</summary>
        protected void WaitFsr(string operation) => WaitFsr(operation, SambaMonitor.NormalTimeout);

        /// <summary>
        /// Polls the FSRs until every plane reports FRDY or <paramref name="budget"/> of wall-clock
        /// time elapses; error bits are checked (and thrown) before ready on every poll.
        /// <paramref name="operation"/> names the command or operation being waited on, for diagnostics.
        /// </summary>
        protected void WaitFsr(string operation, TimeSpan budget)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                uint fsr0 = Monitor.ReadWord(Plane0.Fsr);
                ThrowIfFsrError(fsr0, operation);

                uint fsr1 = ReadyMask;  // a 1-plane part has no second status word: call it ready
                if (PlaneCount == 2)
                {
                    fsr1 = Monitor.ReadWord(Plane1.Fsr);
                    ThrowIfFsrError(fsr1, operation);
                }

                // Every plane must report FRDY. ANDing the two status words first tests the bit in
                // both at once; on a 1-plane part fsr1 is the seed above, so this reduces to plane 0.
                if ((fsr0 & fsr1 & ReadyMask) != 0)
                    return;
                if (stopwatch.Elapsed >= budget)
                    throw new SamBaFlashTimeoutException(operation, budget);
                Thread.Sleep(PollInterval);
            }
        }

        /// <summary>
        /// Writes a keyed command to <paramref name="plane"/>'s FCR. <paramref name="arg"/> fills
        /// the command-argument field (FARG); commands that take no argument leave it zero.
        /// <para>
        /// Raw and unbracketed: every command wants
        /// <see cref="Execute(PlaneRegisters, byte, uint, string)"/> instead. This seam exists for
        /// Execute itself and for the unique-id sequence, which must not wait between its
        /// STUI and SPUI halves.
        /// </para>
        /// <para>
        /// <paramref name="cmd"/> is a <c>byte</c> rather than an enum because the two families in
        /// this hierarchy number the same operations differently: no one enum could serve both, and
        /// an enum per family would only add a cast at every call here. Each family therefore keeps
        /// its opcodes as consts in its own nested <c>Cmd</c> class.
        /// </para>
        /// </summary>
        protected void WriteFcr(PlaneRegisters plane, byte cmd, uint arg = 0)
            => Monitor.WriteWord(plane.Fcr, (CommandKey << 24) | (arg << 8) | cmd);

        /// <summary>
        /// Register addresses of one plane's controller. Both families lay FMR/FCR/FSR out at the
        /// same offsets from the start of a plane's register block, so a plane is fully described
        /// by where its block begins.
        /// </summary>
        protected readonly struct PlaneRegisters
        {
            private const uint FmrOffset = 0x00;
            private const uint FcrOffset = 0x04;
            private const uint FsrOffset = 0x08;

            /// <summary>Describes the plane whose register block starts at <paramref name="baseAddress"/>.</summary>
            public PlaneRegisters(uint baseAddress)
            {
                BaseAddress = baseAddress;
            }

            /// <summary>
            /// Start of this plane's register block. Derived controllers add their own offsets to
            /// it for registers only their family has (the EEFC result register).
            /// </summary>
            public uint BaseAddress { get; }

            /// <summary>Flash mode register (timing, erase-before-program).</summary>
            public uint Fmr => BaseAddress + FmrOffset;

            /// <summary>Flash command register.</summary>
            public uint Fcr => BaseAddress + FcrOffset;

            /// <summary>Flash status register.</summary>
            public uint Fsr => BaseAddress + FsrOffset;
        }
    }
}

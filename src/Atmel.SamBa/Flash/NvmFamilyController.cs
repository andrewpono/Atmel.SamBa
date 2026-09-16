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
    /// Shared skeleton for the NVMCTRL flash controllers (SAMD21 row-erase and SAMD51/E5x
    /// block-erase generations): a keyed command register, a ready flag polled between commands,
    /// and lock bits kept in an auxiliary NVM page instead of in the controller. EFC-family parts
    /// use an FMR/FCR/FSR trio per plane and do not belong here.
    /// </summary>
    /// <remarks>
    /// The two generations share this programming model but almost none of the registers that
    /// implement it: the command register, the ready flag, the register widths, the units ADDR
    /// takes and the erase unit all differ. Everything width- or address-specific therefore sits
    /// behind the abstract members below, which is what lets the sequences here be written once.
    /// </remarks>
    internal abstract class NvmFamilyController : FlashController
    {
        /// <summary>
        /// Base address of the NVMCTRL register map — the same on both generations, though the
        /// offsets within it are not.
        /// </summary>
        protected const uint NvmctrlBaseAddress = 0x41004000;

        /// <summary>
        /// Execution key that arms a command write (CMDEX); a command written without it is
        /// ignored. Not itself a command, and does not fit in a byte.
        /// </summary>
        protected const uint ExecuteKey = 0xA500;

        /// <summary>
        /// Auxiliary NVM page holding the lock bits and the rest of the user configuration. Both
        /// generations map it here; only the offset of the lock bits within it differs.
        /// </summary>
        protected const uint UserAreaAddress = 0x804000;

        /// <summary>Lock regions reported per user-area byte.</summary>
        private const int LockBitsPerByte = 8;

        /// <summary>
        /// PARAM register offset — the one NVMCTRL register at the same offset with the same layout
        /// on both generations: NVMP in bits 15:0 (page count) and PSZ in bits 18:16 (page size,
        /// encoded as 8 &lt;&lt; PSZ).
        /// </summary>
        private const uint ParamOffset = 0x08;

        private const uint ParamPageCountMask = 0xFFFF;
        private const int ParamPageSizeShift = 16;
        private const uint ParamPageSizeMask = 0x7;

        protected NvmFamilyController(SambaMonitor monitor, ChipRecord chip)
            : base(monitor, chip)
        {
        }

        /// <summary>
        /// Reads the geometry the NVMCTRL reports for itself — the PARAM register, which carries
        /// page count and page size. It carries nothing else: these parts have one plane by
        /// construction, and the lock-region count is architectural per generation (16 on D2x, 32
        /// on D5x), so both come from the chip record — which a family-fallback provisional record
        /// carries too, since they follow from the controller generation rather than the part.
        /// </summary>
        internal override DeviceFlashGeometry? ReadDeviceGeometry()
        {
            uint param = Monitor.ReadWord(NvmctrlBaseAddress + ParamOffset);

            int pageCount = (int)(param & ParamPageCountMask);
            if (pageCount == 0)
                return null;    // a dead or unimplemented register reads 0 — no account to act on

            int pageSize = 8 << (int)((param >> ParamPageSizeShift) & ParamPageSizeMask);
            return new DeviceFlashGeometry(pageCount, pageSize, 1, Chip.LockRegionCount);
        }

        /// <summary>
        /// The command that erases one write block — a row on the D2x generation, a block on the D5x.
        /// How much that spans is <c>FlashController.WriteBlockSize</c>, which comes from the chip
        /// record so the supported-chip listing can report it without opening a device.
        /// </summary>
        protected abstract byte EraseUnitCommand { get; }

        /// <summary>Diagnostic name for that command, e.g. "row erase (ER)".</summary>
        protected abstract string EraseUnitLabel { get; }

        /// <summary>The command that empties the page buffer.</summary>
        protected abstract byte PageBufferClearCommand { get; }

        /// <summary>The command that commits a loaded page buffer to flash.</summary>
        protected abstract byte WritePageCommand { get; }

        /// <summary>Offset of the first lock-bit byte within the user area.</summary>
        protected abstract uint UserAreaLockOffset { get; }

        /// <summary>Bytes read and rewritten as a unit when updating the user area.</summary>
        protected abstract int UserAreaSize { get; }

        /// <summary>The command that sets the security bit.</summary>
        protected abstract byte SetSecurityCommand { get; }

        /// <summary>
        /// Addresses of the words the factory serial number is assembled from, in datasheet order.
        /// Four of them, agreeing with the word count the table carries for these parts — the count
        /// is what <c>SamBaChipInfo.HasUniqueId</c> answers from without a device, this is where
        /// <see cref="GetUniqueId"/> reads.
        /// <para>
        /// An array rather than a base address and a stride because the words are not consecutive on
        /// either generation: word 0 sits apart from the other three, at a different distance on each.
        /// </para>
        /// </summary>
        protected abstract uint[] UniqueIdWordAddresses { get; }

        /// <summary>True when the controller reports itself able to accept a command.</summary>
        protected abstract bool IsReady();

        /// <summary>
        /// Writes <paramref name="cmd"/> to the generation's command register, armed with
        /// <see cref="ExecuteKey"/>.
        /// <para>
        /// <paramref name="cmd"/> is a <c>byte</c> rather than an enum because the two generations
        /// number the same operations differently: no one enum could serve both, and an enum per
        /// generation would only add a cast at every call here. Each generation therefore keeps its
        /// opcodes as consts in its own nested <c>Cmd</c> class.
        /// </para>
        /// </summary>
        protected abstract void WriteCommandRegister(byte cmd);

        /// <summary>
        /// Throws <see cref="SamBaFlashCommandException"/> when the controller flagged an error,
        /// clearing the flag first so a later command cannot be blamed for it.
        /// <paramref name="operation"/> names the command that failed.
        /// </summary>
        protected abstract void ThrowIfCommandError(string operation);

        /// <summary>
        /// Points ADDR at <paramref name="byteAddress"/>, converting to the units the generation's
        /// ADDR register takes.
        /// </summary>
        protected abstract void WriteAddress(uint byteAddress);

        /// <summary>
        /// Puts the controller in manual-write mode — where a loaded page buffer is committed only
        /// by an explicit write command — and disables the read caches.
        /// </summary>
        protected abstract void ConfigureManualWrite();

        /// <summary>
        /// Clears status flags left by an earlier operation, before each erase command. A no-op
        /// unless the generation keeps sticky flags that would be misread afterwards.
        /// <paramref name="operation"/> names the command about to run, for diagnostics.
        /// </summary>
        protected virtual void ClearStaleStatus(string operation) { }

        /// <summary>Writes <paramref name="userArea"/> back over the auxiliary NVM page.</summary>
        protected abstract void WriteUserArea(byte[] userArea);

        /// <summary>
        /// Checks that the lock bits fit inside the user-area buffer this controller reads and
        /// writes; past that <see cref="ApplyLockRegions"/> would index off the end of it.
        /// <para>
        /// Pure arithmetic, yet it belongs here rather than in a constructor: the values it reads
        /// (<see cref="UserAreaLockOffset"/>, <see cref="UserAreaSize"/>) are abstract, so asking for
        /// them during construction would dispatch into a derived object that is not built yet.
        /// </para>
        /// </summary>
        protected override void OnInitialize()
        {
            // NotSupportedException for the reason given in docs/DESIGN.md: a geometry this controller
            // cannot drive, not a disagreement between the table and the code. Since geometry
            // adoption it is reachable from the part's own account rather than only from a row — a D5x
            // whose PARAM claims an 8-byte page leaves 8 bytes of user page where the lock bits need
            // 12, and 8 is a page size the gate accepts. No listed row comes near either limit.
            int lockBytes = (LockRegionCount + LockBitsPerByte - 1) / LockBitsPerByte;
            if ((int)UserAreaLockOffset + lockBytes > UserAreaSize)
                throw new NotSupportedException(
                    $"{Name}: {LockRegionCount} lock regions need {lockBytes} bytes at offset " +
                    $"{UserAreaLockOffset} of a {UserAreaSize}-byte user area.");

            // Manual write mode and the cache-disable bits are configuration, not per-command state:
            // no command in the write sequence clears them, so setting them once here saves a
            // read-modify-write of a control register on every page — four byte transactions per page
            // on the D5x generation, two of them carrying the monitor's flush delay. Disabling the
            // read caches also keeps a verify read-back from being served stale.
            //
            // Both bits are also errata workarounds in their own right, one per generation. On the
            // D5x/E5x, NVM reads can be corrupted when mixed with page-buffer writes, and the
            // family errata's remedy is exactly these cache-disable bits (fixed in revision F and
            // later silicon, still needed for A and D); the condition is unavoidable here, since
            // these parts have no ROM monitor and the bootloader servicing our writes is itself
            // executing from NVM while the page buffer fills. On the D2x, MANW defaults to 0, which
            // the SAM D21 errata records as letting a stray pointer write reach the NVM, and its
            // remedy is to set MANW at start-up — which is what manual write mode does here anyway.
            //
            // The one thing this rules out: the same errata notes that a DSU CRC32 over NVM will not
            // complete while the cache is disabled, on every revision. Verification is a read-back
            // comparison, so nothing here wants CRC32 — but a future CRC32-based verify would have
            // to re-enable the cache around it rather than assume this state.
            ConfigureManualWrite();
        }

        public override void EraseAll(uint offset)
        {
            // Validated here rather than on the way into the loop below: X# hands the offset to the
            // bootloader, which checks nothing, so leaving it to the loop would mean a misaligned
            // offset erasing a partial unit on exactly the parts that carry the extension while
            // throwing on the parts that do not.
            ValidateEraseUnitAligned(offset);
            if (offset > FlashSize)
                throw new ArgumentOutOfRangeException(
                    nameof(offset), $"Erase offset lies past the end of the {FlashSize}-byte flash.");

            // Prefer the extended command when the bootloader advertises it: one command it waits
            // out itself, instead of a unit-erase loop costing several register round-trips per
            // erase unit — a 256-byte row on SAMD21, so ~1000 iterations for a 256 KB part. Both
            // routes erase from the offset to the end of flash; the difference is round-trips, not
            // reach.
            if (Monitor.Capabilities.HasChipEraseCommand)
                Monitor.ChipErase(offset);
            else
                Erase(offset, (uint)(FlashSize - offset));
        }

        /// <summary>
        /// Programs one write block: erases it, then commits its hardware pages one at a time. The
        /// page buffer holds a single hardware page, so a row or block reaches flash as several page
        /// writes — but the erase happens once, up front, which is what makes the block the unit a
        /// caller can safely replace.
        /// </summary>
        public override void WriteBlock(int block, byte[] data, int dataOffset)
        {
            ValidateBlock(block);

            uint blockOffset = (uint)block * (uint)WriteBlockSize;
            if (AutoEraseEnabled)
                Erase(blockOffset, (uint)WriteBlockSize);

            for (int page = 0; page < PagesPerWriteBlock; page++)
            {
                Execute(PageBufferClearCommand, nameof(WriteBlock));

                // The NVM page buffer is memory-mapped at the target flash address.
                uint address = FlashAddress + blockOffset + (uint)(page * PageSize);
                WaitReady(nameof(WriteBlock));
                LoadLatch(address, data, dataOffset + page * PageSize, PageSize);

                WriteAddress(address);
                Execute(WritePageCommand, nameof(WriteBlock));
            }
        }

        /// <summary>
        /// Erases <paramref name="size"/> bytes from <paramref name="offset"/>, one erase unit per
        /// command. <paramref name="offset"/> must start on an erase-unit boundary.
        /// </summary>
        protected void Erase(uint offset, uint size)
        {
            uint eraseSize = (uint)WriteBlockSize;

            ValidateEraseUnitAligned(offset);
            // Widened first: both operands are 32-bit, and a range that wraps would pass the check
            // and then erase from the offset to the end of flash.
            if ((long)offset + size > FlashSize)
                throw new ArgumentOutOfRangeException(nameof(size), "Erase range exceeds flash size.");

            // Unit indices, not addresses: the last unit the range touches, rounded up, exclusive.
            uint endUnitExclusive = (offset + size + eraseSize - 1) / eraseSize;

            for (uint eraseNum = offset / eraseSize; eraseNum < endUnitExclusive; eraseNum++)
            {
                ClearStaleStatus(EraseUnitLabel);
                WriteAddress(eraseNum * eraseSize);
                Execute(EraseUnitCommand, EraseUnitLabel);
            }
        }

        /// <summary>
        /// Throws unless <paramref name="offset"/> starts on an erase-unit boundary. The hardware has
        /// no finer erase, so a misaligned offset cannot mean anything but erasing more than was
        /// asked for.
        /// </summary>
        private void ValidateEraseUnitAligned(uint offset)
        {
            if (offset % (uint)WriteBlockSize != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(offset), $"Erase offset must be a multiple of the {WriteBlockSize}-byte erase unit.");
        }

        // Always boots flash: no ROM SAM-BA on these parts, the bootloader is flash-resident.
        public override SamBaChipBootSource GetBootSource() => SamBaChipBootSource.Flash;

        public override bool[] GetLockRegions()
        {
            var regions = new bool[LockRegionCount];
            uint address = UserAreaAddress + UserAreaLockOffset;
            byte lockBits = 0;

            for (int region = 0; region < LockRegionCount; region++)
            {
                if (region % LockBitsPerByte == 0)
                    lockBits = Monitor.ReadByte(address++);

                // A programmed (clear) bit means locked.
                regions[region] = (lockBits & (1 << (region % LockBitsPerByte))) == 0;
            }

            return regions;
        }

        /// <summary>
        /// Reads the factory serial number from <see cref="UniqueIdWordAddresses"/> — four plain word
        /// reads, no command and no bracket around them. Where the EEFC has to map its id over the
        /// flash window for the span of a command pair, these parts publish theirs in read-only NVM
        /// that is mapped at all times, so the read disturbs nothing and needs nothing sequenced.
        /// <para>
        /// Gated on the record's word count, which is 0 only on a provisional record for a part no
        /// table row matches: reading the listed addresses on an unidentified part would return
        /// whatever happens to be there and report it as that device's identity.
        /// </para>
        /// </summary>
        public override IReadOnlyList<uint> GetUniqueId()
        {
            if (Chip.UniqueIdWords == 0)
                return Array.Empty<uint>();

            uint[] addresses = UniqueIdWordAddresses;
            var id = new uint[addresses.Length];
            for (int w = 0; w < addresses.Length; w++)
                id[w] = Monitor.ReadWord(addresses[w]);

            // Wrapped rather than returned bare, as on the EEFC: the caller above holds the result for
            // the whole session, so a cast back to uint[] would let one reader rewrite the rest's view.
            return Array.AsReadOnly(id);
        }

        /// <summary>
        /// These parts have no boot-mode bit, so the only reachable source is the one they already use:
        /// flash. Asking for that is satisfied by doing nothing; asking for the ROM is a request the
        /// hardware cannot honour — the same rule <c>EfcFamilyController.ApplyBootSource</c> follows,
        /// reached here without a read because <see cref="GetBootSource"/> is a constant on this family.
        /// </summary>
        protected override void ApplyBootSource(SamBaChipBootSource source)
        {
            if (source != SamBaChipBootSource.Flash)
                throw new SamBaFlashCommandException(
                    $"{Name} always boots from flash; it cannot be pointed at a ROM it does not have.",
                    "boot-source change", isUnsupported: true);
        }

        protected override void ApplySecurityBit() => Execute(SetSecurityCommand, "security-bit set (SSB)");

        /// <summary>
        /// Runs one NVMCTRL command to completion: waits for ready, writes the keyed command, waits
        /// for it to finish, then clears and reports any error the controller flagged.
        /// <paramref name="operation"/> names the command being run, for diagnostics.
        /// </summary>
        protected void Execute(byte cmd, string operation)
        {
            WaitReady(operation);

            WriteCommandRegister(cmd);

            WaitReady(operation);

            ThrowIfCommandError(operation);
        }

        /// <summary>
        /// Polls the controller until <see cref="IsReady"/>, up to
        /// <see cref="SambaMonitor.LongTimeout"/> of wall-clock time.
        /// <paramref name="operation"/> names the operation being waited on, for diagnostics.
        /// </summary>
        protected void WaitReady(string operation)
        {
            // A serial-round-trip status poll deserves a bounded budget rather than spinning
            // forever. Some parts stall the monitor while a slow op is in flight (e.g. the SAMC21
            // needs ~6 ms to erase a page), so tolerate a few consecutive failed status reads
            // before giving up rather than aborting on the first.
            TimeSpan budget = SambaMonitor.LongTimeout;
            var stopwatch = Stopwatch.StartNew();
            int consecutiveFailures = 0;
            const int maxFailures = 3;

            while (true)
            {
                try
                {
                    if (IsReady())
                        return;
                    consecutiveFailures = 0;
                }
                catch (SamBaTransportException)
                {
                    if (++consecutiveFailures >= maxFailures)
                        throw;
                }

                if (stopwatch.Elapsed >= budget)
                    throw new SamBaFlashTimeoutException(operation, budget);
                Thread.Sleep(PollInterval);
            }
        }

        /// <summary>
        /// Brings lock regions to <paramref name="locked"/> — every region when
        /// <paramref name="regions"/> is null, otherwise just the listed ones. The bits live in the
        /// user area rather than in the controller, so any change is a read-modify-write of that
        /// whole page instead of a command per region; a subset costs the same single erase and
        /// rewrite as the full set, and untouched regions keep their bits through the rewrite.
        /// </summary>
        protected override void ApplyLockRegions(bool locked, int[] regions)
        {
            bool[] current = GetLockRegions();

            bool dirty = false;
            if (regions == null)
            {
                foreach (bool region in current)
                    dirty |= region != locked;
            }
            else
            {
                foreach (int region in regions)
                    dirty |= current[region] != locked;
            }

            if (!dirty)
                return;

            var userArea = new byte[UserAreaSize];
            Monitor.Read(UserAreaAddress, userArea, 0, userArea.Length);

            int count = regions == null ? LockRegionCount : regions.Length;
            for (int i = 0; i < count; i++)
            {
                int region = regions == null ? i : regions[i];
                int index = (int)UserAreaLockOffset + region / LockBitsPerByte;
                if (locked)
                    userArea[index] &= (byte)~(1 << (region % LockBitsPerByte));
                else
                    userArea[index] |= (byte)(1 << (region % LockBitsPerByte));
            }

            WriteUserArea(userArea);
        }
    }
}

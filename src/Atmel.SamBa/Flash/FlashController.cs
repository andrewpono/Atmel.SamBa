using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System;
using System.Collections.Generic;

namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// Base class for the flash controller implementations. In place of an on-target word-copy
    /// applet, the page latch / NVM page buffer is filled by <see cref="LoadLatch"/> — a batched
    /// stream of <c>W#</c> word writes straight into the memory-mapped latch address range.
    /// </summary>
    /// <remarks>
    /// Register constants in the derived controllers are named after the numeric role they play, so
    /// a use site never has to guess it: <c>*Mask</c> is a value ANDed or ORed with a register (one
    /// bit or many), <c>*Shift</c> is a shift count positioning a multi-bit field, and
    /// <c>*BitIndex</c> / <c>First*Bit</c> is a bit position. Constants that are neither — a field's
    /// contents rather than its placement — read as the quantity they hold (<c>FlashWaitStates</c>,
    /// <c>DefaultFlashCycles</c>).
    /// </remarks>
    internal abstract class FlashController
    {
        /// <summary>Bytes per 32-bit flash word; latch loads and word reads are word-granular.</summary>
        protected const int BytesPerWord = 4;

        /// <summary>Delay between consecutive controller status polls.</summary>
        protected static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);

        protected FlashController(SambaMonitor monitor, ChipRecord chip)
        {
            Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            Chip = chip;
        }

        protected SambaMonitor Monitor { get; }

        /// <summary>
        /// The record this controller runs on. Usually the matched table row as constructed; for a
        /// part identified by family fallback it starts with zero geometry and is completed from the
        /// device's own account by <see cref="ResolveGeometry"/> before <see cref="OnInitialize"/>
        /// runs — nothing may derive from geometry until then, which is why the setter exists.
        /// <para>
        /// That setter is the price of a structural choice rather than an oversight, so it is worth
        /// saying which choice. This class is both the register driver and the authority on the record
        /// it drives, because the geometry probe is itself register access: only a built controller can
        /// issue GETD or read PARAM. Resolving geometry somewhere else and handing a finished
        /// record to the constructor would make this field <c>readonly</c> again and retire the
        /// ordering caveat above — but that resolver still needs a controller to probe through, so it
        /// would buy both by building one, probing, discarding it, and building a second from the
        /// completed record. One private three-step sequence in <see cref="Create"/> was judged
        /// cheaper than two constructions and a duplicated switch. The judgement rests on this being
        /// the only mutable state here; a second such field is the signal to revisit it.
        /// </para>
        /// </summary>
        protected ChipRecord Chip { get; private set; }

        /// <summary>The record this controller runs on, for the device layer and diagnostics.</summary>
        internal ChipRecord Record => Chip;

        /// <summary>
        /// The geometry the device reported for itself when it disagrees with the table row this
        /// controller was built from; null when it agreed, could not be read, or failed the sanity
        /// gate. Whether the table row or this geometry ends up in force depends on the
        /// <see cref="SamBaGeometryPrecedence"/> <see cref="Create"/> was given — either way this
        /// exists so the disagreement can be surfaced as a warning instead of vanishing.
        /// </summary>
        internal DeviceFlashGeometry? MismatchedDeviceGeometry { get; private set; }

        /// <summary>
        /// The table row's own geometry at the moment <see cref="MismatchedDeviceGeometry"/> was
        /// recorded — a snapshot taken before a <see cref="SamBaGeometryPrecedence.Device"/>
        /// resolution can overwrite <see cref="Chip"/>'s geometry with the device's account. Null
        /// exactly when <see cref="MismatchedDeviceGeometry"/> is null. A diagnostic reading "the
        /// table said" must use this, not <see cref="Record"/>, since <see cref="Record"/> may by
        /// then already equal the adopted device geometry.
        /// </summary>
        internal DeviceFlashGeometry? MismatchedTableGeometry { get; private set; }

        /// <summary>
        /// The geometry the device reported for itself when it answered but failed
        /// <see cref="DeviceFlashGeometry.IsUsable"/>'s sanity gate; null when it agreed, could not
        /// be read at all, or passed. Always set together with <see cref="UnusableGeometryReason"/>.
        /// The table row stays in force either way — this exists so the rejection can be surfaced
        /// as a warning instead of vanishing.
        /// </summary>
        internal DeviceFlashGeometry? UnusableDeviceGeometry { get; private set; }

        /// <summary>
        /// Which of <see cref="DeviceFlashGeometry.IsUsable"/>'s checks <see cref="UnusableDeviceGeometry"/>
        /// failed; null exactly when <see cref="UnusableDeviceGeometry"/> is null.
        /// </summary>
        internal string UnusableGeometryReason { get; private set; }

        /// <summary>
        /// True when <see cref="SamBaGeometryPrecedence.Device"/> was requested but the controller
        /// reported no geometry at all to adopt — whether because it has no self-report command
        /// (a legacy EFC predating GETD) or because the probe failed outright. Either way there is
        /// nothing for the setting to adopt, so the table row ran exactly as it would have under
        /// <see cref="SamBaGeometryPrecedence.Table"/>; this exists so that silent no-op is a report
        /// instead of an invisible one. Never set alongside <see cref="UnusableDeviceGeometry"/> —
        /// that property covers the part answering with an implausible reading, not no reading.
        /// </summary>
        internal bool DevicePrecedenceHadNoEffect { get; private set; }

        public string Name => Chip.Name;

        /// <summary>Flash base address (0 on NVMCTRL parts).</summary>
        public uint FlashAddress => Chip.FlashAddress;

        public int PageCount => Chip.PageCount;

        public int PageSize => Chip.PageSize;

        /// <summary>
        /// Bytes in one write block — the span the flash layer programs in, because it is the
        /// smallest that can be written without disturbing what surrounds it. A page on the EFC
        /// families, whose erase granularity is the page; a row or block on NVMCTRL, which cannot
        /// erase less than several pages at once.
        /// </summary>
        public int WriteBlockSize => Chip.WriteBlockSize;

        /// <summary>Write blocks in the whole flash — the range <see cref="WriteBlock"/> indexes.</summary>
        public int WriteBlockCount => Chip.WriteBlockCount;

        /// <summary>Hardware pages in one write block; 1 wherever a page is independently writable.</summary>
        protected int PagesPerWriteBlock => Chip.PagesPerWriteBlock;

        public int PlaneCount => Chip.PlaneCount;

        public int LockRegionCount => Chip.LockRegionCount;

        public long FlashSize => Chip.FlashSize;

        /// <summary>
        /// The lock regions a byte range in flash falls in, ascending — what an update locks when it
        /// was asked to lock what it programmed. A region is the granularity the hardware locks at, so
        /// a range ending mid-region locks that whole region: some flash past the range is protected
        /// too, which is the geometry rather than a defect. An empty or zero-length range covers
        /// nothing.
        /// </summary>
        /// <remarks>
        /// Region index maps linearly onto flash offset across both planes of a two-plane part, the
        /// same arithmetic <c>EfcFamilyController.FirstPageOfRegion</c> runs in the other direction;
        /// only issuing the command needs to know which plane a region sits in. The division is exact
        /// because the geometry gate requires the page count to divide by the region count.
        /// </remarks>
        /// <param name="offset">Byte offset into flash where the range starts.</param>
        /// <param name="length">Length of the range in bytes.</param>
        /// <returns>The covered region indexes, ascending; empty when the range is empty.</returns>
        internal int[] LockRegionsCovering(uint offset, long length)
        {
            if (length <= 0)
                return Array.Empty<int>();

            long bytesPerRegion = FlashSize / LockRegionCount;
            int first = (int)(offset / bytesPerRegion);
            int last = (int)((offset + length - 1) / bytesPerRegion);

            // Clamped rather than trusted: callers validate the image against the flash size first, and
            // a range that still ran past the end must not become a command aimed at a region the part
            // does not have.
            if (last > LockRegionCount - 1)
                last = LockRegionCount - 1;

            var regions = new int[last - first + 1];
            for (int i = 0; i < regions.Length; i++)
                regions[i] = first + i;

            return regions;
        }

        /// <summary>
        /// When true (default), page writes erase their target first (EEFC EWP; NVMCTRL
        /// row/block auto-erase). Disabled after a full-chip erase to avoid double work.
        /// </summary>
        public bool AutoEraseEnabled { get; private set; } = true;

        /// <summary>
        /// True when the boot source is selectable, i.e. the chip has a boot-mode GPNVM bit
        /// (<see cref="ChipRecord.BootGpnvmBitIndex"/> has a value): the bootable legacy EFC parts and
        /// every EEFC part. False where the source is fixed, which on every such part means fixed at
        /// flash — so this says whether the source can be *changed*, never whether flash is reachable.
        /// Named for that after the previous name, <c>CanBootFlash</c>, was read as the latter often
        /// enough to put a wrong answer in <see cref="Efc.GetBootSource"/>.
        /// </summary>
        public bool CanSelectBootSource => Chip.BootGpnvmBitIndex.HasValue;

        /// <summary>
        /// Sets <see cref="AutoEraseEnabled"/>, letting the controller program whatever the change
        /// needs on the device first — so a hardware failure leaves the property reporting the mode
        /// still in force.
        /// </summary>
        public void SetAutoErase(bool enable)
        {
            OnSetAutoErase(enable);
            AutoEraseEnabled = enable;
        }

        /// <summary>Extra controller work when auto-erase changes (EFC toggles an FMR bit).</summary>
        protected virtual void OnSetAutoErase(bool enable) { }

        /// <summary>
        /// One-time hardware setup after construction; invoked by <see cref="Create"/>.
        /// Constructors must not touch the device — any register initialization belongs here.
        /// Runs after <see cref="ResolveGeometry"/>, so overrides may rely on geometry being final.
        /// </summary>
        protected virtual void OnInitialize() { }

        /// <summary>
        /// Reads the flash geometry the part reports for itself — the EEFC's GETD descriptor, the
        /// NVMCTRL's PARAM register — or null where no such account exists. The legacy EFC has
        /// none, so this base returns null: SAM7 parts and the bootloaders that emulate them are
        /// never probed. Called by <see cref="Create"/> between construction and
        /// <see cref="OnInitialize"/>; overrides must be safe to run before any other register
        /// initialization.
        /// </summary>
        internal virtual DeviceFlashGeometry? ReadDeviceGeometry() => null;

        /// <summary>
        /// The adopt-or-verify decision: reads the device's own account of its geometry and holds
        /// it against the table's. The table wins a disagreement by default — the descriptor read is
        /// unverified against real silicon, and geometry decides what gets erased — but
        /// <paramref name="geometryPrecedence"/> lets a caller adopt the device's account instead,
        /// and either way the disagreement is kept in <see cref="MismatchedDeviceGeometry"/> so the
        /// device layer can warn. The one case the device's account is always adopted, regardless of
        /// that parameter, is a provisional record (geometry all zero, from the family fallback for a
        /// part no table row matches): there it is the only account there is, and a part that cannot
        /// supply a usable one is genuinely unsupported.
        /// <para>
        /// <paramref name="identity"/> is carried for that rejection alone — nothing else here reads
        /// the raw identification words. They belong in the exception because it is the report someone
        /// files about a part this library could not place, and the words are what such a report needs;
        /// leaving them out would make them null, which on that type means "the probe never read this
        /// register" and would be untrue of every one of them.
        /// </para>
        /// </summary>
        private void ResolveGeometry(ChipIdentity identity, SamBaGeometryPrecedence geometryPrecedence)
        {
            DeviceFlashGeometry? reported;
            try
            {
                reported = ReadDeviceGeometry();
            }
            catch (SamBaFlashCommandException)
            {
                // The monitor's flash controller rejected the probe (a part whose EEFC predates
                // GETD sets FCMDE). Its absence is an answer, not a failure.
                reported = null;
            }
            catch (SamBaFlashTimeoutException)
            {
                // No controller answered at the probed address — the reserved-register-zero case.
                reported = null;
            }

            bool provisional = Chip.PageCount == 0;

            if (reported == null || !reported.Value.IsUsable(Chip.PagesPerWriteBlock))
            {
                // Distinguishes "nothing answered" (no descriptor register at all — reported == null,
                // never a failure; see the catches above) from "something answered and it was
                // implausible" (reported != null — worth a reason to make sense of it).
                string reason = reported?.UnusableReasons(Chip.PagesPerWriteBlock);

                if (provisional)
                {
                    string reasonSuffix = reason == null
                        ? "the flash controller reported no usable geometry to run on instead"
                        : $"the flash controller reported unusable geometry ({reason}) to run on instead";
                    throw new SamBaUnsupportedDeviceException(
                        $"Device is not supported: no device-table row matches ({Chip.Name}) and " +
                        $"{reasonSuffix}.",
                        identity.ChipId, identity.ExtChipId, identity.DeviceId, identity.CpuId);
                }

                if (reported != null)
                {
                    UnusableDeviceGeometry = reported.Value;
                    UnusableGeometryReason = reason;
                }
                else if (geometryPrecedence == SamBaGeometryPrecedence.Device)
                {
                    // Nothing came back at all, so there is nothing for Device precedence to adopt —
                    // it silently behaves like Table here unless this is recorded for the device layer.
                    DevicePrecedenceHadNoEffect = true;
                }

                return;     // the table stands — a rejection above is recorded for the device layer
            }

            DeviceFlashGeometry geometry = reported.Value;
            if (provisional)
                Chip = Chip.WithGeometry(
                    geometry.PageCount, geometry.PageSize, geometry.PlaneCount, geometry.LockRegionCount);
            else if (!geometry.Matches(Chip))
            {
                MismatchedDeviceGeometry = geometry;
                MismatchedTableGeometry = new DeviceFlashGeometry(
                    Chip.PageCount, Chip.PageSize, Chip.PlaneCount, Chip.LockRegionCount);
                if (geometryPrecedence == SamBaGeometryPrecedence.Device)
                    Chip = Chip.WithGeometry(
                        geometry.PageCount, geometry.PageSize, geometry.PlaneCount,
                        geometry.LockRegionCount);
            }
        }

        /// <summary>
        /// Erases all flash from <paramref name="offset"/> (0 = full chip) to the end. What offsets
        /// are accepted is family-specific — the erase granularity is the hardware's, not this
        /// method's — so each override validates its own and throws
        /// <see cref="ArgumentOutOfRangeException"/> for the rest. Legacy EFC parts accept only 0.
        /// </summary>
        public abstract void EraseAll(uint offset);

        /// <summary>
        /// Programs one full write block; <paramref name="data"/> must hold
        /// <see cref="WriteBlockSize"/> bytes at <paramref name="dataOffset"/>. Whatever the block
        /// covers is replaced, so a caller holding only part of it must merge the rest in first —
        /// there is no smaller write.
        /// </summary>
        public abstract void WriteBlock(int block, byte[] data, int dataOffset);

        /// <summary>
        /// Throws if a write of <paramref name="length"/> bytes at flash <paramref name="offset"/>
        /// cannot be programmed in the controller's current mode, so a doomed write fails fast
        /// with actionable guidance instead of part-way through. The base implementation permits
        /// everything; EEFC overrides it to reject auto-erasing writes that reach past the sectors
        /// its erase-and-write-page command can erase within.
        /// </summary>
        public virtual void EnsureWriteSupported(uint offset, int length) { }

        /// <summary>Reads one full write block into <paramref name="data"/> at <paramref name="dataOffset"/>.</summary>
        /// <remarks>
        /// No explicit idle wait is needed: every controller's <see cref="WriteBlock"/> / erase
        /// completes synchronously (each waits out its own command), so the controller is already
        /// idle by the time a read runs. How the bytes travel — block stream or word reads — is
        /// <see cref="ReadRange"/>'s decision; see <see cref="ReadRequiresWords"/>.
        /// </remarks>
        public void ReadBlock(int block, byte[] data, int dataOffset)
        {
            ValidateBlock(block);
            ReadRange((uint)block * (uint)WriteBlockSize, data, dataOffset, WriteBlockSize);
        }

        /// <summary>
        /// Reads <paramref name="count"/> bytes of flash from byte <paramref name="offset"/> into
        /// <paramref name="buffer"/> at <paramref name="bufferOffset"/>. Offsets are into flash rather
        /// than addresses, and neither has to be aligned to anything: a read disturbs nothing, so an
        /// image written at an arbitrary offset can be verified from exactly there.
        /// <para>
        /// The only place the flash layer reads flash — <see cref="ReadBlock"/> comes through here too
        /// — which is what makes <see cref="ReadRequiresWords"/> one fork rather than a search for
        /// every reader. Not the only read of the flash address range in the assembly:
        /// <c>SamBaDevice.ReadMemory</c> reads whatever address a caller names, flash included (and
        /// forks on the same property), and <see cref="GetUniqueId"/> on the EEFC reads that window
        /// while the factory id is mapped over it. Neither reads flash content by geometry, which is
        /// what this method is for.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The range runs past the end of flash, or past the end of <paramref name="buffer"/>.
        /// </exception>
        public void ReadRange(uint offset, byte[] buffer, int bufferOffset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (bufferOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            if (count < 0 || count > buffer.Length - bufferOffset)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + (long)count > FlashSize)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    $"Read of {count} bytes at offset 0x{offset:X} runs past the {FlashSize}-byte flash.");

            if (ReadRequiresWords)
                Monitor.ReadViaWords(FlashAddress + offset, buffer, bufferOffset, count);
            else
                Monitor.Read(FlashAddress + offset, buffer, bufferOffset, count);
        }

        /// <summary>
        /// Whether this part's monitor must be read word-by-word (<c>SambaMonitor.ReadViaWords</c>)
        /// because its block-read command answers with all zeros instead of memory content. Not a
        /// flash-only defect where it exists — the boot memory at address 0 remaps the same flash —
        /// so <c>SamBaDevice.ReadMemory</c> forks every read on this, not just the flash window,
        /// which is why it is internal rather than protected.
        /// </summary>
        internal virtual bool ReadRequiresWords => false;

        /// <summary>Current lock state of every lock region.</summary>
        public abstract bool[] GetLockRegions();

        /// <summary>Current security-bit state.</summary>
        public abstract bool GetSecurity();

        /// <summary>
        /// The memory this part boots from. Every implementation answers
        /// <see cref="SamBaChipBootSource.Flash"/> when <see cref="CanSelectBootSource"/> is false, because
        /// there is no part here whose fixed source is the ROM; only the selectable case reads a bit.
        /// </summary>
        public abstract SamBaChipBootSource GetBootSource();

        /// <summary>
        /// Applies the requested option changes in the one order the hardware allows: boot bit, then
        /// lock regions, then security. Setting the security bit denies the controller any further
        /// command of its own, so anything sequenced after it would be dropped silently — which is
        /// why the order lives here, once, rather than in each family's controller.
        /// </summary>
        /// <remarks>
        /// No waits between the steps, and none after the last, because none are needed: the hooks
        /// below are self-sufficient — every command they issue is bracketed by the family's
        /// <c>Execute</c> (wait idle, command, wait out, check error) — so a step's failure is
        /// thrown inside that step, and the next one never reads status with an error still
        /// pending. That matters on the EFC family, where reading the status register clears its
        /// error bits: a step that opens by reading state would otherwise read a failed
        /// predecessor's error away, unreported. An empty option set is a no-op.
        /// </remarks>
        public void ApplyOptions(FlashOptionState options)
        {
            if (options.BootSource.HasValue)
                ApplyBootSource(options.BootSource.Value);

            if (options.Lock.HasValue)
            {
                // A named subset goes the long way round, through the validating entry point below,
                // rather than straight to the hook: the subset an update computes is arithmetic on an
                // image length, and that is exactly the kind of thing worth checking once against the
                // part's region count before it becomes a command.
                if (options.LockRegions == null)
                    ApplyLockRegions(options.Lock.Value, null);
                else
                    SetLockRegions(options.LockRegions, options.Lock.Value);
            }

            if (options.SetSecurity && !GetSecurity())
                ApplySecurityBit();
        }

        /// <summary>
        /// Locks or unlocks just the named regions, leaving every other region as it is. Validation
        /// and normalization happen here, once, so every family implementation receives a clean,
        /// deduplicated, ascending, non-empty subset — a bad index is refused before it can become
        /// a command aimed at a page that does not exist.
        /// </summary>
        /// <param name="regions">
        /// Region indexes to change; duplicates collapse, and an empty list is a no-op.
        /// </param>
        /// <param name="locked">True to lock the named regions, false to unlock them.</param>
        /// <exception cref="ArgumentNullException"><paramref name="regions"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// An index is negative or not below <see cref="LockRegionCount"/>.
        /// </exception>
        public void SetLockRegions(IReadOnlyList<int> regions, bool locked)
        {
            if (regions == null)
                throw new ArgumentNullException(nameof(regions));

            var normalized = new SortedSet<int>();
            foreach (int region in regions)
            {
                if (region < 0 || region >= LockRegionCount)
                    throw new ArgumentOutOfRangeException(nameof(regions), region,
                        $"Lock region {region} is outside 0..{LockRegionCount - 1}.");
                normalized.Add(region);
            }

            if (normalized.Count == 0)
                return;

            var subset = new int[normalized.Count];
            normalized.CopyTo(subset);
            ApplyLockRegions(locked, subset);
        }

        /// <summary>
        /// Points the boot source at <paramref name="source"/>. A request for the source the part is
        /// already using is a no-op on every family, whether or not that family could have moved; only
        /// a source an implementation cannot reach throws <c>SamBaFlashCommandException</c>. Which is
        /// why <see cref="CanSelectBootSource"/> alone does not decide the answer — on a fixed-source
        /// part it is <see cref="GetBootSource"/> that says which of the two requests is impossible,
        /// and since that is always <see cref="SamBaChipBootSource.Flash"/> there, the impossible one is
        /// always <see cref="SamBaChipBootSource.Rom"/>.
        /// <para>
        /// Contract for this and the two hooks below: return with the controller idle and any
        /// failure already thrown — issue commands through the family's <c>Execute</c>, which
        /// guarantees both. <see cref="ApplyOptions"/> runs the steps back to back on that promise.
        /// </para>
        /// </summary>
        protected abstract void ApplyBootSource(SamBaChipBootSource source);

        /// <summary>
        /// Brings lock regions to <paramref name="locked"/>: every region when
        /// <paramref name="regions"/> is null, otherwise exactly the listed ones (validated,
        /// deduplicated, ascending, never empty). Same contract as <see cref="ApplyBootSource"/>:
        /// idle on return, failures thrown here.
        /// </summary>
        protected abstract void ApplyLockRegions(bool locked, int[] regions);

        /// <summary>
        /// Sets the part's security bit. Called only when <see cref="GetSecurity"/> says it is not
        /// already set, and last of all the options, since it locks the controller out afterwards.
        /// Same contract as <see cref="ApplyBootSource"/>: idle on return, failures thrown here.
        /// </summary>
        protected abstract void ApplySecurityBit();

        /// <summary>
        /// Reads the chip's factory-programmed unique identifier. Overridden where there is one to
        /// read — the EEFC maps it over the flash window for a command pair, the NVMCTRL publishes it
        /// at fixed read-only addresses — so this base answer is for the parts that have none: legacy
        /// EFC, the SAM7L and SAM9XE EEFCs that omit the command, and any part identified only by
        /// fallback, whose provisional record says nothing about an id layout.
        /// </summary>
        public virtual IReadOnlyList<uint> GetUniqueId() => Array.Empty<uint>();

        /// <summary>
        /// Creates and initializes the controller implementation for an identified chip. Takes the
        /// whole <see cref="ChipIdentity"/> rather than its record because the two halves are both
        /// needed here: the controller runs on the record, and the identification words travel so that
        /// the one rejection this method can still raise reports what the probe read
        /// (see <see cref="ResolveGeometry"/>).
        /// <para>
        /// <see cref="ResolveGeometry"/> runs before <see cref="OnInitialize"/> deliberately: a
        /// provisional record has no geometry until the device supplies one, and initialization
        /// derives from geometry (the NVMCTRL user-area check divides by it). The probes are safe
        /// in that order — GETD and a PARAM read touch registers, never the flash array.
        /// </para>
        /// </summary>
        /// <param name="monitor">The connected monitor the controller will run its commands on.</param>
        /// <param name="identity">The chip just identified, record and raw identification words alike.</param>
        /// <param name="geometryPrecedence">
        /// Which account wins a disagreement between the table row and the device's own report. See
        /// <see cref="SamBaGeometryPrecedence"/>; does not affect a provisional record, which always
        /// adopts the device's account regardless.
        /// </param>
        /// <exception cref="SamBaUnsupportedDeviceException">
        /// The record is provisional — no table row matched — and the flash controller reported no
        /// usable geometry to run on instead.
        /// </exception>
        public static FlashController Create(
            SambaMonitor monitor, ChipIdentity identity,
            SamBaGeometryPrecedence geometryPrecedence = SamBaGeometryPrecedence.Table)
        {
            ChipRecord chip = identity.Record;
            FlashController controller;
            switch (chip.ControllerKind)
            {
                case FlashControllerKind.Efc: controller = new Efc(monitor, chip); break;
                case FlashControllerKind.Eefc: controller = new Eefc(monitor, chip); break;
                case FlashControllerKind.D2xNvm: controller = new D2xNvm(monitor, chip); break;
                case FlashControllerKind.D5xNvm: controller = new D5xNvm(monitor, chip); break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown flash controller kind: {chip.ControllerKind}.");
            }

            controller.ResolveGeometry(identity, geometryPrecedence);
            controller.OnInitialize();
            return controller;
        }

        /// <summary>Throws when <paramref name="block"/> is outside the chip's write-block range.</summary>
        protected void ValidateBlock(int block)
        {
            if (block < 0 || block >= WriteBlockCount)
                throw new ArgumentOutOfRangeException(
                    nameof(block), $"Write block {block} outside 0..{WriteBlockCount - 1}.");
        }

        /// <summary>
        /// Loads <paramref name="count"/> bytes into the write latch at <paramref name="targetAddress"/>
        /// (the memory-mapped flash address range itself) via batched <c>W#</c> word writes — one
        /// USB round-trip per page instead of one per word. The flash-vocabulary name for
        /// <see cref="SambaMonitor.WriteViaWords"/>, where the batching, the argument checks and
        /// the <c>SafeMode</c> word-per-exchange fallback all live.
        /// </summary>
        protected void LoadLatch(uint targetAddress, byte[] data, int dataOffset, int count) =>
            Monitor.WriteViaWords(targetAddress, data, dataOffset, count);
    }
}

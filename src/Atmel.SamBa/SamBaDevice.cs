using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Flash;
using Anp.Atmel.SamBa.Protocol;
using System;
using System.Collections.Generic;
using System.Text;

namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Represents a single Atmel/Microchip SAM device in SAM-BA monitor mode on a USB CDC
    /// serial port. Provides chip identification, raw memory access, and flash operations
    /// (erase / program / verify / boot configuration / lock regions / security).
    /// </summary>
    /// <remarks>
    /// Instances are single-consumer: operations must not overlap. Flash pages are programmed
    /// without an on-target SRAM applet — page data is streamed into the memory-mapped page latch
    /// as one batched write of <c>W#</c> monitor commands, then committed through the flash
    /// controller registers.
    /// </remarks>
    public sealed class SamBaDevice : IDisposable
    {
        /// <summary>Atmel USB vendor id.</summary>
        public const ushort DefaultVendorId = 0x03EB;

        /// <summary>SAM-BA USB CDC product id.</summary>
        public const ushort DefaultProductId = 0x6124;

        private readonly SambaMonitor _monitor;
        private FlashController _controller;

        /// <summary>This session's unique id once <see cref="GetUniqueId"/> has read it; null before.</summary>
        private IReadOnlyList<uint> _uniqueId;

        private bool _disposed;

        /// <summary>
        /// Initializes a new instance for the device at <paramref name="devicePath"/>. On the
        /// Windows targets that is a serial device-interface path or COM port name, e.g. from
        /// discovery, the watcher, or an external PnP toolkit; on the portable targets it is the
        /// OS port name — "COM7", "/dev/ttyACM0", "/dev/cu.usbmodem...". The device is not opened.
        /// </summary>
        /// <param name="devicePath">
        /// Path or port name identifying the port. Checked for null and nothing more —
        /// whether it names a device at all is not known until <see cref="Open"/>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="devicePath"/> is null.</exception>
        public SamBaDevice(string devicePath)
            : this(CreateDefaultTransport(devicePath))
        {
#if SAMBA_WIN32
            ProbePortMetadata(devicePath);
#else
            PortName = devicePath;
#endif
        }

        private static ISambaTransport CreateDefaultTransport(string devicePath)
        {
#if SAMBA_WIN32
            return new SerialPortTransport(devicePath);
#else
            return new PortableSerialTransport(devicePath);
#endif
        }

        /// <summary>
        /// Initializes a new instance over a caller-supplied transport — a custom serial stack, a
        /// TCP bridge, or a test double. The device is not opened, and it takes ownership of
        /// <paramref name="transport"/>: <see cref="Dispose"/> disposes it.
        /// <para>
        /// Port metadata stays empty on this path: <see cref="PortName"/> and
        /// <see cref="FriendlyName"/> are empty, <see cref="VendorId"/> and <see cref="ProductId"/>
        /// are null, and <see cref="DevicePath"/> reports whatever the transport answers —
        /// <see cref="DisplayName"/> falls back accordingly.
        /// </para>
        /// </summary>
        /// <param name="transport">Transport carrying the SAM-BA monitor protocol.</param>
        /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
        public SamBaDevice(ISambaTransport transport)
        {
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            _monitor = new SambaMonitor(transport) { ProgressReporter = RaiseProgress };
            PortName = string.Empty;
            FriendlyName = string.Empty;
            MonitorVersion = string.Empty;
        }

        /// <summary>System path that uniquely identifies the device.</summary>
        public string DevicePath => _monitor.DevicePath;

        /// <summary>COM port name (e.g. "COM7"); empty when unavailable. On the portable targets
        /// this is the device path as given ("COM7", "/dev/ttyACM0").</summary>
        public string PortName { get; private set; }

        /// <summary>USB vendor id parsed from the device path, if present. Windows targets only;
        /// null elsewhere.</summary>
        public ushort? VendorId { get; private set; }

        /// <summary>USB product id parsed from the device path, if present. Windows targets only;
        /// null elsewhere.</summary>
        public ushort? ProductId { get; private set; }

        /// <summary>Windows PnP friendly name for the port (e.g. "USB Serial Device (COM3)");
        /// empty when unavailable. Populated only on the Windows targets and only for
        /// path-constructed devices; empty on the portable targets and for devices built over a
        /// caller-supplied transport.</summary>
        public string FriendlyName { get; private set; }

        /// <summary>
        /// A human-readable label for the device. Once identified (after <see cref="Open"/>) it is
        /// "&lt;chip&gt; on &lt;port&gt;". Before identification it is "SAM-BA device on &lt;port&gt;"
        /// for a port carrying the Atmel SAM-BA VID/PID, otherwise the Windows PnP friendly name
        /// (or "Serial device on &lt;port&gt;" when that is unavailable) — so a port that has not
        /// been confirmed as SAM-BA is not labelled as one. Without Windows port metadata (portable
        /// targets, or a device built over a caller-supplied transport) the pre-identification form
        /// is always "Serial device on &lt;port&gt;".
        /// </summary>
        public string DisplayName
        {
            get
            {
                string port = string.IsNullOrEmpty(PortName) ? DevicePath : PortName;
                if (ChipInfo != null)
                    return $"{ChipInfo.Name} on {port}";
                if (VendorId == DefaultVendorId && ProductId == DefaultProductId)
                    return $"SAM-BA device on {port}";
                return string.IsNullOrEmpty(FriendlyName) ? $"Serial device on {port}" : FriendlyName;
            }
        }

        /// <summary>True while the device is open.</summary>
        public bool IsOpen => _monitor.IsConnected && _controller != null;

        /// <summary>
        /// Forces the slow, robust transfer path: each write block is loaded one word at a time
        /// instead of as a single batched stream of <c>W#</c> commands, and on parts that read
        /// word-by-word (see <see cref="ReadMemory"/>) each word read waits out its own reply
        /// instead of pipelining a batch. Default false. Enable only if a device drops data
        /// during programming (some bootloaders cannot keep up with a back-to-back command
        /// batch). Set it between operations.
        /// </summary>
        /// <remarks>
        /// Between operations, not during one, and the same goes for <see cref="ChipEraseTimeout"/>: a
        /// device is single-consumer (see the class remarks), so neither is a lever to pull from
        /// another thread against an operation already running. Nothing tears if you do — the latch
        /// loader reads it once per page, so a change lands on some later page — but which page, and
        /// therefore whether one NVMCTRL write block ends up taking both paths across its 4 or 16
        /// pages, is not something this class defines.
        /// <para>
        /// Budget for it before enabling it on a large part: the cost is not a modest multiple but a
        /// fixed delay per word, so a 256-byte write block takes between 64 ms and a second and an
        /// 8 KB NVMCTRL block between 2 and 32 seconds — a 1 MB image on such a part somewhere between
        /// four minutes and an hour. The spread is the Windows timer tick, not the device. Prefer
        /// leaving this off and reaching for it only against a part that has actually been seen to
        /// drop data.
        /// </para>
        /// </remarks>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public bool SafeMode
        {
            get
            {
                ThrowIfDisposed();
                return _monitor.SafeMode;
            }
            set
            {
                ThrowIfDisposed();
                _monitor.SafeMode = value;
            }
        }

        /// <summary>
        /// How long a whole-chip erase is allowed to take. Default 60 seconds, which covers the
        /// slowest part in the device table (the datasheet maxima reach roughly 25 seconds) with
        /// margin for a bootloader that erases unit by unit. Raise it only for a part or bootloader
        /// slower still: an erase abandoned part-way fails an operation that would have succeeded
        /// and leaves flash half-erased. Set it between operations, as with <see cref="SafeMode"/> —
        /// an erase already waiting keeps the budget it started on.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public TimeSpan ChipEraseTimeout
        {
            get
            {
                ThrowIfDisposed();
                return _monitor.ChipEraseTimeout;
            }
            set
            {
                ThrowIfDisposed();
                _monitor.ChipEraseTimeout = value;
            }
        }

        /// <summary>
        /// SAM-BA monitor version string (<c>V#</c> reply); empty until opened, and retained
        /// afterwards — <see cref="Close"/> does not clear it, so a closed device still says what it
        /// last connected to.
        /// </summary>
        public string MonitorVersion { get; private set; }

        /// <summary>
        /// Identified chip and flash geometry; null until opened, and retained afterwards for the same
        /// reason <see cref="MonitorVersion"/> is. Non-null with <see cref="IsOpen"/> false is
        /// therefore an ordinary state, not a contradiction: it describes the last device opened on
        /// this path.
        /// </summary>
        public SamBaChipInfo ChipInfo { get; private set; }

        /// <summary>
        /// Raised during connect / chip identification (indeterminate stage messages) and
        /// long-running operations — erase / write / verify / read (determinate snapshots).
        /// <para>
        /// Raised synchronously on the thread that called the operation, not on a thread-pool thread
        /// the way <c>SamBaDeviceWatcher</c>'s events are: a handler runs inline and holds the
        /// operation up until it returns, and a UI consumer driving the device from a background
        /// thread has to marshal before touching UI state.
        /// </para>
        /// </summary>
        public event EventHandler<SamBaProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Raised from <see cref="Open"/> when the flash controller's own account of its geometry
        /// (EEFC flash descriptor; NVMCTRL PARAM register) contradicts the device table's row for
        /// the identified chip. The table's figures stay in force — geometry decides what gets
        /// erased, and the device's account is unverified — so this is a report, not a mode
        /// change: one of the two sources is wrong, and a handler that logs it is how a wrong
        /// table row gets found from the field. Raised synchronously, like
        /// <see cref="ProgressChanged"/>.
        /// </summary>
        public event EventHandler<SamBaGeometryMismatchEventArgs> GeometryMismatchDetected;

        /// <summary>
        /// Raised from <see cref="Open"/> when the flash controller's own account of its geometry
        /// answered, but failed the sanity gate every geometry must pass before anything acts on
        /// it — most often an all-zero reply from a part with no descriptor register at all, but
        /// also an implausible non-zero one. The device table's figures stay in force; this is a
        /// report, not a mode change, same as <see cref="GeometryMismatchDetected"/> — a handler
        /// that logs it is how a device that cannot describe itself gets noticed. Raised
        /// synchronously, like <see cref="ProgressChanged"/>.
        /// </summary>
        public event EventHandler<SamBaUnusableGeometryEventArgs> UnusableDeviceGeometryDetected;

        /// <summary>
        /// Opens the serial port, switches the monitor to binary mode, identifies the chip
        /// and initializes its flash controller.
        /// </summary>
        /// <param name="identificationMode">
        /// Which probe <see cref="ChipIdentifier.Identify"/> takes at the legacy-CHIPID-versus-CPUID
        /// branch. Defaults to <see cref="SamBaChipIdentificationMode.Auto"/>, which reads the reset
        /// vector and lets its content decide — unchanged from before this parameter existed.
        /// <see cref="SamBaChipIdentificationMode.ChipId"/> and
        /// <see cref="SamBaChipIdentificationMode.CpuId"/> skip that read and force the named branch;
        /// see that type's remarks for why this is only safe on a part whose core generation is
        /// already known.
        /// </param>
        /// <param name="geometryPrecedence">
        /// Which account wins when a listed part's device-reported geometry disagrees with its table
        /// row. Defaults to <see cref="SamBaGeometryPrecedence.Table"/>. 
        /// See <see cref="SamBaGeometryPrecedence"/> for when
        /// <see cref="SamBaGeometryPrecedence.Device"/> is worth choosing instead.
        /// </param>
        /// <exception cref="SamBaTransportException">
        /// The device did not answer as a SAM-BA monitor — including the case where it answered with
        /// data left over from an interrupted session rather than with its version string. One retry
        /// happens inside this call; a second attempt is worth making before giving up on the port.
        /// </exception>
        /// <exception cref="SamBaUnsupportedDeviceException">
        /// The chip is not in the supported-device table, and it either has no self-describing
        /// flash controller or reported no usable geometry to run on instead.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// The chip identified, but its geometry is one the flash controller cannot drive: more lock
        /// regions per plane than the legacy <c>MC_FSR</c> can report, or an NVMCTRL user page too
        /// small for its own lock bits. Reachable from a device-table row and, since geometry is
        /// adopted from an unlisted part's own account, from a part that described itself
        /// implausibly — never from a healthy listed part.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void Open(
            SamBaChipIdentificationMode identificationMode = SamBaChipIdentificationMode.Auto,
            SamBaGeometryPrecedence geometryPrecedence = SamBaGeometryPrecedence.Table)
        {
            ThrowIfDisposed();
            if (IsOpen)
                return;

            // Connect belongs inside the try even though it already cleans up after itself: with
            // every step that can fail under one catch, a failed Open cannot leave the port
            // claimed, whatever gets added here later. Disconnecting a monitor that never
            // connected is a no-op.
            try
            {
                _monitor.Connect();

                ChipIdentity identity = ChipIdentifier.Identify(_monitor, identificationMode);
                _controller = FlashController.Create(_monitor, identity, geometryPrecedence);

                // Cleared here rather than in Close so that the cache cannot outlive the controller
                // it belongs to: IsOpen requires one, so every reopen — possibly of a different
                // board on the same path, whose unique id is a different number — passes through
                // this line and starts empty.
                _uniqueId = null;

                // Geometry comes from the controller's record, not the identity's: for a part
                // identified by family fallback, Create has just completed the record from the
                // device's own account, and the identity still holds the zero-geometry provisional.
                ChipInfo = new SamBaChipInfo(_controller.Record, identity);
                MonitorVersion = _monitor.Version;

                RaiseProgress(SamBaProgressEventArgs.Indeterminate(
                    $"Chip identified: {ChipInfo.Name}", ProgressStage.Identifying));

                if (_controller.MismatchedDeviceGeometry != null)
                    ReportGeometryMismatch(geometryPrecedence);
                if (_controller.UnusableDeviceGeometry != null)
                    ReportUnusableGeometry();
                if (_controller.DevicePrecedenceHadNoEffect)
                    ReportDevicePrecedenceHadNoEffect();
            }
            catch
            {
                _controller = null;
                _monitor.Disconnect();
                throw;
            }
        }

        /// <summary>
        /// Surfaces a table-versus-device geometry disagreement recorded during
        /// <see cref="FlashController.Create"/>: once through <see cref="GeometryMismatchDetected"/>
        /// with the figures, and once as a progress message so a consumer that only logs progress
        /// (the bundled updater) shows it without new wiring.
        /// <para>
        /// A subscriber that throws is contained here rather than allowed out, which is the one place
        /// in this class that is true. Everything else this device raises accompanies work the caller
        /// asked for, so a failing handler failing that work is the caller's own bug surfacing; this
        /// raise accompanies nothing — the table's figures are already in force and the device is
        /// already open. It sits inside <see cref="Open"/>'s try, whose catch closes the port and
        /// abandons the controller, so letting a logging handler throw here would turn a healthy
        /// device into a failed open over a report. Nowhere to report the handler's failure either:
        /// the only channel out of this class is the progress event, which is the other thing that
        /// may just have thrown.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Assumes <see cref="Open"/> has already checked <see cref="FlashController.MismatchedDeviceGeometry"/>
        /// is not null — the only caller, right where that check sits.
        /// </remarks>
        private void ReportGeometryMismatch(SamBaGeometryPrecedence geometryPrecedence)
        {
            DeviceFlashGeometry device = _controller.MismatchedDeviceGeometry.Value;
            DeviceFlashGeometry table = _controller.MismatchedTableGeometry.Value;
            var args = new SamBaGeometryMismatchEventArgs(
                _controller.Record.Name,
                table.PageCount, device.PageCount,
                table.PageSize, device.PageSize,
                table.PlaneCount, device.PlaneCount,
                table.LockRegionCount, device.LockRegionCount);

            string winner = geometryPrecedence == SamBaGeometryPrecedence.Device
                ? "using the device-reported geometry"
                : "using the table";

            try
            {
                GeometryMismatchDetected?.Invoke(this, args);
                RaiseProgress(SamBaProgressEventArgs.Indeterminate(
                    $"Device-reported flash geometry differs from the device table — {winner}. {args}",
                    ProgressStage.Identifying));
            }
            catch (Exception)
            {
                // A report is not worth failing an open for; see the remarks above.
            }
        }

        /// <summary>
        /// Surfaces a device geometry rejection recorded during <see cref="FlashController.Create"/>:
        /// once through <see cref="UnusableDeviceGeometryDetected"/> with the reported figures and
        /// the reason, and once as a progress message, mirroring <see cref="ReportGeometryMismatch"/>
        /// — including its reasoning for swallowing a subscriber's exception here rather than
        /// letting it fail the open.
        /// </summary>
        /// <remarks>
        /// Assumes <see cref="Open"/> has already checked <see cref="FlashController.UnusableDeviceGeometry"/>
        /// is not null — the only caller, right where that check sits.
        /// </remarks>
        private void ReportUnusableGeometry()
        {
            DeviceFlashGeometry device = _controller.UnusableDeviceGeometry.Value;
            var args = new SamBaUnusableGeometryEventArgs(
                _controller.Record.Name,
                device.PageCount, device.PageSize, device.PlaneCount, device.LockRegionCount,
                _controller.UnusableGeometryReason);

            try
            {
                UnusableDeviceGeometryDetected?.Invoke(this, args);
                RaiseProgress(SamBaProgressEventArgs.Indeterminate(
                    $"Device-reported flash geometry failed the sanity gate — using the table. {args}",
                    ProgressStage.Identifying));
            }
            catch (Exception)
            {
                // A report is not worth failing an open for; see the remarks on ReportGeometryMismatch.
            }
        }

        /// <summary>
        /// Surfaces a <see cref="SamBaGeometryPrecedence.Device"/> request that
        /// <see cref="FlashController.Create"/> could not honor: the controller had no geometry at
        /// all to adopt, so the table ran exactly as it would have under
        /// <see cref="SamBaGeometryPrecedence.Table"/>. One generic message covers both reasons that
        /// can leave nothing to adopt — a controller with no self-report command, or a probe that
        /// failed outright — since either way there is nothing more specific to say.
        /// </summary>
        /// <remarks>
        /// Assumes <see cref="Open"/> has already checked
        /// <see cref="FlashController.DevicePrecedenceHadNoEffect"/> is true — the only caller, right
        /// where that check sits.
        /// </remarks>
        private void ReportDevicePrecedenceHadNoEffect()
        {
            try
            {
                RaiseProgress(SamBaProgressEventArgs.Indeterminate(
                    "Geometry precedence set to Device, but the flash controller reported no " +
                    "geometry to adopt — using the table.",
                    ProgressStage.Identifying));
            }
            catch (Exception)
            {
                // A report is not worth failing an open for; see the remarks on ReportGeometryMismatch.
            }
        }

        /// <summary>
        /// Closes the serial port. Chip information from the last Open is retained
        /// (<see cref="ChipInfo"/>, <see cref="MonitorVersion"/>). No-op when not open, safe after
        /// <see cref="Dispose"/>, and throws nothing a caller has to catch: a port that fails to close
        /// because the device has already gone is the expected end of a session, not an error, so the
        /// transport swallows it. Reopening afterwards is allowed and starts a fresh session.
        /// </summary>
        public void Close()
        {
            _controller = null;
            _monitor.Disconnect();
        }

        // ---------------------------------------------------------------------------------
        // Raw monitor access (any address)
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Reads a block of memory from any address — RAM, peripheral registers, or flash
        /// (flash is memory-mapped and read directly).
        /// </summary>
        /// <remarks>
        /// On SAM3/SAM4/SAM9XE/SAMx7x the monitor's block-read command answers with all zeros
        /// instead of memory content, so every read on those parts travels as pipelined 32-bit
        /// word reads instead — transparently, just slower on the wire. Every address, not just
        /// flash, because the defect follows the flash into its remaps (the boot memory at
        /// address 0 is the same flash), and a whole aligned word is the safer access for the
        /// registers in between anyway. <see cref="SafeMode"/> applies to these reads too: it
        /// drops the pipelining and pays one round trip per word.
        /// </remarks>
        /// <param name="address">Absolute address to read from.</param>
        /// <param name="count">
        /// Bytes to read. Zero returns an empty array without touching the device.
        /// </param>
        /// <returns>
        /// Exactly <paramref name="count"/> bytes. A short reply is a transport failure rather than a
        /// short return, so the length never has to be checked.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, the read timed out, or the device was removed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public byte[] ReadMemory(uint address, int count)
        {
            EnsureOpen(nameof(ReadMemory));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            var buffer = new byte[count];
            if (_controller.ReadRequiresWords)
                _monitor.ReadViaWords(address, buffer, 0, count);
            else
                _monitor.Read(address, buffer, 0, count);

            return buffer;
        }

        /// <summary>
        /// Writes a block of memory to <paramref name="address"/>, choosing the path from the
        /// address: a range that lies entirely within the flash window is programmed through the
        /// flash controller (each affected page is erased and written, with read-modify-write for
        /// partially-covered pages); anything else is a raw write to RAM or peripheral registers.
        /// </summary>
        /// <remarks>
        /// For a full firmware image prefer <see cref="UpdateFirmware"/> (bulk erase + verify +
        /// boot/lock/security): a flash write here uses per-page auto-erase, which the EEFC
        /// hardware limits to the first 16 KB on SAM4 / SAMx7x.
        /// </remarks>
        /// <param name="address">Absolute address to write to.</param>
        /// <param name="data">
        /// Bytes to write. Empty returns without touching the device — including without the boundary
        /// check, since a zero-length range cannot straddle anything.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="ArgumentException">The range straddles the flash boundary.</exception>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// The flash path was taken and the controller rejected a command — a locked region, or an
        /// auto-erasing write reaching past the small sectors on SAM4 / SAMx7x.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The flash path was taken and the controller did not report ready in time.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void WriteMemory(uint address, byte[] data)
        {
            EnsureOpen(nameof(WriteMemory));
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            if (TryGetFlashOffset(address, data.Length, out uint offset))
                CreateProgrammer().WriteBytes(data, offset);
            else
                _monitor.Write(address, data, 0, data.Length);
        }

        /// <summary>
        /// Reads one 32-bit word from <paramref name="address"/> via the monitor's <c>w#</c>
        /// command — a single aligned 32-bit bus access.
        /// </summary>
        /// <remarks>
        /// This is the register-access primitive, not a narrow special case of
        /// <see cref="ReadMemory"/>. <see cref="ReadMemory"/> uses the byte-oriented block
        /// stream (<c>R#</c>), whereas memory-mapped peripheral registers generally require a
        /// single 32-bit access — reading (or writing) them a byte at a time can return a torn
        /// value or trigger the wrong side effect. Use this to read a specific register and get
        /// a <see cref="uint"/> back directly (no manual little-endian reassembly); use
        /// <see cref="ReadMemory"/> for arbitrary-length blocks of RAM or flash.
        /// </remarks>
        /// <param name="address">Absolute address to read; the device requires it word-aligned.</param>
        /// <returns>The word as read, assembled little-endian to match the target.</returns>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, the read timed out, or the device was removed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public uint ReadWord(uint address)
        {
            EnsureOpen(nameof(ReadWord));
            return _monitor.ReadWord(address);
        }

        /// <summary>
        /// Writes one 32-bit word to <paramref name="address"/> via the monitor's <c>W#</c>
        /// command — a single aligned 32-bit bus access.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="ReadWord"/> and the correct way to poke a memory-mapped
        /// register: it guarantees the single 32-bit access such registers require, which the
        /// byte-stream <see cref="WriteMemory(uint, byte[])"/> path does not. It is also always a
        /// raw write — unlike <see cref="WriteMemory(uint, byte[])"/> it never inspects the flash
        /// window or diverts into the flash-programming path, so it will not accidentally start
        /// an erase/program cycle. (Programming a flash cell needs the controller's latch-and-
        /// command sequence anyway; a bare word write into flash space does not stick.)
        /// </remarks>
        /// <param name="address">Absolute address to write; the device requires it word-aligned.</param>
        /// <param name="value">The 32-bit value to write.</param>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, the write timed out, or the device was removed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void WriteWord(uint address, uint value)
        {
            EnsureOpen(nameof(WriteWord));
            _monitor.WriteWord(address, value);
        }

        /// <summary>
        /// Jumps to code at <paramref name="address"/> (monitor <c>G#</c> command). The monitor does not
        /// come back: the CPU is running the code jumped to, so nothing here can be expected to answer
        /// afterwards.
        /// </summary>
        /// <remarks>
        /// <see cref="IsOpen"/> stays true all the same — the port is still open, and nothing tells this
        /// library whether the jumped-to code kept the USB CDC function alive. Follow with
        /// <see cref="Close"/> unless the target is known to return to the monitor.
        /// </remarks>
        /// <param name="address">Absolute address to jump to.</param>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, the command write timed out, or the device was removed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void Go(uint address)
        {
            EnsureOpen(nameof(Go));
            _monitor.Go(address);
        }

        // ---------------------------------------------------------------------------------
        // Flash operations
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Erases the entire flash. A destructive, whole-device wipe — for a firmware update use
        /// <see cref="UpdateFirmware"/>, which erases (from its offset) and programs in one pass.
        /// </summary>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// The controller rejected the erase — a locked region is the usual reason. Unlock first
        /// (<see cref="SetLockRegions(bool)"/>), which is what <see cref="UpdateFirmware"/> does
        /// for itself.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The erase did not finish within <see cref="ChipEraseTimeout"/>. Flash is left part-erased.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void EraseAllFlash()
        {
            EnsureOpen(nameof(EraseAllFlash));
            CreateProgrammer().EraseAll(0);
        }

        /// <summary>
        /// Programs a raw firmware image: erase (optional), page-wise write, verify (optional),
        /// then boot/lock/security options, then reset (optional) — see <see cref="SamBaUpdateOptions"/>.
        /// </summary>
        /// <param name="data">Raw binary image (no hex/ELF parsing).</param>
        /// <param name="options">
        /// Update options; null uses the defaults (auto-erasing write, verify, boot-to-flash, reset).
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <see cref="SamBaUpdateOptions.Offset"/> is not aligned to what this part requires, or the
        /// image does not fit in the flash left after it. Thrown before the unlock and the erase, so a
        /// rejected image leaves the existing firmware intact. Its <c>ParamName</c> is <c>"offset"</c>
        /// or <c>"data"</c> after the flash layer's own parameters, not after this method's — the
        /// offset it names lives on <paramref name="options"/>.
        /// </exception>
        /// <exception cref="SamBaVerificationException">Verification found a mismatch.</exception>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed. Flash is left part-written.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// The controller rejected a command: a region still locked with
        /// <see cref="SamBaUpdateOptions.UnlockBeforeWrite"/> false, or an auto-erasing write past the
        /// small sectors with <see cref="SamBaUpdateOptions.BulkErase"/> false on SAM4 / SAMx7x.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time — see <see cref="ChipEraseTimeout"/> for the
        /// erase budget. Flash is left part-written.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void UpdateFirmware(byte[] data, SamBaUpdateOptions options = null)
        {
            EnsureOpen(nameof(UpdateFirmware));
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                throw new ArgumentException("Firmware image is empty.", nameof(data));

            SamBaUpdateOptions opts = options != null ? options.Clone() : new SamBaUpdateOptions();
            FlashProgrammer programmer = CreateProgrammer();

            // Before the unlock and the erase, not inside the write: rejecting the image afterwards
            // would leave a blanked part with the old firmware gone and the new one unwritten. Costs
            // nothing to hoist — the checks are arithmetic on the offset and length alone.
            programmer.ValidateImage(data, opts.Offset);

            RaiseProgress(SamBaProgressEventArgs.Indeterminate($"Starting update with options: {opts}"));

            if (opts.UnlockBeforeWrite)
                UnlockAllIfLocked();

            if (opts.BulkErase)
                programmer.EraseAll(opts.Offset);

            programmer.Write(data, opts.Offset, assumeErased: opts.BulkErase);

            if (opts.Verify)
                programmer.Verify(data, opts.Offset);

            // Not requested must arrive as null: for the boot source false would be a request to point
            // the part at the ROM, and for the lock, false would be a request to unlock every region.
            // Hence the explicit nulls rather than bare defaults, which the conditionals would type
            // from the other operand as plain false.
            bool? lockValue;
            int[] lockRegions;
            switch (opts.Lock)
            {
                case FlashLockScope.All:
                    lockValue = true;
                    lockRegions = null;
                    break;

                // Scoped to what was just programmed rather than the whole flash: an image that fills
                // part of the flash has no business write-protecting the rest, which on an offset
                // image is somebody else's bootloader. The image has already been range-checked
                // above, so the regions this names are the part's own.
                case FlashLockScope.Written:
                    lockValue = true;
                    lockRegions = _controller.LockRegionsCovering(opts.Offset, data.Length);
                    break;

                default:
                    lockValue = null;
                    lockRegions = null;
                    break;
            }

            // Applied here, after the erase rather than before it, and that ordering is load-bearing:
            // the boot bit survives an erase on the parts that have one, so a part erased with the bit
            // clear comes back up in the monitor. Setting it last is what lets the new firmware run.
            var optionState = new FlashOptionState
            {
                // Silently skipped where the boot source is fixed — there is nothing to set, and those
                // parts are fixed at flash already.
                BootSource = opts.SetBootToFlash && _controller.CanSelectBootSource
                    ? SamBaChipBootSource.Flash
                    : (SamBaChipBootSource?)null,
                Lock = lockValue,
                LockRegions = lockRegions,
                SetSecurity = opts.SetSecurity == SecurityAction.SetPermanently,
            };
            if (!optionState.IsEmpty)
            {
                RaiseProgress(SamBaProgressEventArgs.Indeterminate($"Applying options: {optionState}", ProgressStage.Options));
                _controller.ApplyOptions(optionState);
            }

            if (opts.Reset)
            {
                RaiseProgress(SamBaProgressEventArgs.Indeterminate("Resetting device", ProgressStage.Resetting));

                // Said out loud rather than dropped: a part identified only by family fallback, with
                // no confirmed Cortex-M CPUID either, has no reset route at all, so the line above
                // would otherwise be the last word on a reset that never happened. The update itself
                // has succeeded — this is the one thing left undone.
                if (!DeviceResetter.TryReset(_monitor, ChipInfo.Family, ChipInfo.CpuId))
                    RaiseProgress(SamBaProgressEventArgs.Indeterminate(
                        $"No reset route for {ChipInfo.Family}; the device was not reset and is still " +
                        "running the SAM-BA monitor. Power-cycle it to start the new firmware.",
                        ProgressStage.Resetting));

                Close();
            }
        }


        /// <summary>
        /// Gets the memory the part boots from. Read from the boot-mode GPNVM bit where there is one
        /// (bootable legacy EFC, and every EEFC part — see
        /// <see cref="SamBaChipInfo.CanSelectBootSource"/>); answered without a command where there is
        /// not, and that answer is always <see cref="SamBaChipBootSource.Flash"/>, because no part here has
        /// the ROM as its fixed source.
        /// </summary>
        /// <returns>The boot source in force.</returns>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// The get-GPNVM command failed on an EEFC part.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public SamBaChipBootSource GetBootSource()
        {
            EnsureOpen(nameof(GetBootSource));
            return _controller.GetBootSource();
        }

        /// <summary>
        /// Points the boot source at <paramref name="source"/> through the boot-mode GPNVM bit. Asking
        /// for the source the part already uses does nothing and never fails, even where the source is
        /// fixed — a fixed source is only an obstacle to changing it, and <see cref="GetBootSource"/>
        /// says which one it is.
        /// </summary>
        /// <remarks>
        /// On the parts that can select, the bit is sticky and survives an erase, so pointing it back at
        /// flash is what lets newly written firmware run instead of the monitor coming up again. An
        /// update does that for you — see <see cref="SamBaUpdateOptions.SetBootToFlash"/>, on by default
        /// — and this method is for doing it on its own.
        /// <para>
        /// <see cref="SamBaChipBootSource.Rom"/> is the rarely useful direction: it hands the part back to
        /// the monitor on next reset. Recoverable, since the monitor is then what answers, so it can be
        /// pointed at flash again.
        /// </para>
        /// </remarks>
        /// <param name="source">The memory to boot from.</param>
        /// <exception cref="SamBaFlashCommandException">
        /// <paramref name="source"/> is not the source this part is fixed at — always
        /// <see cref="SamBaChipBootSource.Rom"/> on a part with no boot-mode bit, since flash is what those
        /// are fixed at. Also raised when the GPNVM command itself fails on a part that has the bit.
        /// </exception>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void SetBootSource(SamBaChipBootSource source)
        {
            EnsureOpen(nameof(SetBootSource));

            // No capability check of its own: the controller's hook applies the rule above, and one
            // here could only be a second, coarser opinion — it used to be exactly that, refusing a
            // request an NVMCTRL part had already satisfied by construction.
            _controller.ApplyOptions(new FlashOptionState { BootSource = source });
        }

        /// <summary>Gets the lock state of every lock region.</summary>
        /// <returns>
        /// One entry per region, <see cref="SamBaChipInfo.LockRegionCount"/> of them, region 0 first.
        /// Each covers <see cref="SamBaChipInfo.PageCount"/> / that many pages. A fresh list each call.
        /// </returns>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// The get-lock-bit command failed on an EFC or EEFC part.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public IReadOnlyList<bool> GetLockRegions()
        {
            EnsureOpen(nameof(GetLockRegions));
            return _controller.GetLockRegions();
        }

        /// <summary>
        /// Locks or unlocks all lock regions. Regions already in the requested state are left alone,
        /// so this costs only the commands it actually needs.
        /// </summary>
        /// <param name="locked">True to lock every region, false to unlock every region.</param>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// A set- or clear-lock-bit command failed. Some regions may already have changed.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time. Some regions may already have changed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void SetLockRegions(bool locked)
        {
            EnsureOpen(nameof(SetLockRegions));
            _controller.ApplyOptions(new FlashOptionState { Lock = locked });
        }

        /// <summary>
        /// Locks or unlocks just the named regions, leaving every other region as it is. Regions
        /// already in the requested state cost no command, duplicate indexes collapse into one,
        /// and an empty list changes nothing and touches no hardware.
        /// </summary>
        /// <param name="regions">
        /// Region indexes to change, each within 0..<see cref="SamBaChipInfo.LockRegionCount"/> - 1.
        /// </param>
        /// <param name="locked">True to lock the named regions, false to unlock them.</param>
        /// <exception cref="ArgumentNullException"><paramref name="regions"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// An index in <paramref name="regions"/> is negative or not below the part's
        /// <see cref="SamBaChipInfo.LockRegionCount"/>.
        /// </exception>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// A set- or clear-lock-bit command failed. Some regions may already have changed.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time. Some regions may already have changed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void SetLockRegions(IReadOnlyList<int> regions, bool locked)
        {
            EnsureOpen(nameof(SetLockRegions));
            _controller.SetLockRegions(regions, locked);
        }

        /// <summary>
        /// Gets the security-bit state, read from wherever the part keeps it: a live status bit on the
        /// legacy EFC (<c>MC_FSR</c>) and on the SAMD21 generation (<c>NVMCTRL.STATUS</c>), a GPNVM bit
        /// that costs a get-bit command on the EEFC parts, and the Device Service Unit
        /// (<c>DSU.STATUSB.PROT</c>) on SAMD51/E5x, whose NVMCTRL does not report it at all.
        /// </summary>
        /// <returns>True when the security bit is set and SAM-BA access is restricted.</returns>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">
        /// The get-GPNVM command failed on an EEFC part.
        /// </exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public bool GetSecurity()
        {
            EnsureOpen(nameof(GetSecurity));
            return _controller.GetSecurity();
        }

        /// <summary>
        /// Sets the security bit. WARNING: irreversible — SAM-BA access is blocked afterwards
        /// and the device can only be recovered with the erase pin.
        /// </summary>
        /// <remarks>
        /// Nothing useful follows a successful call on the same session: the part stops answering as it
        /// did, so a later operation here is as likely to fail as to work. Treat this as the last thing
        /// a session does. A no-op when the bit is already set.
        /// </remarks>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">The set-security command failed.</exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time. Whether the bit took cannot be told from here.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void SetSecurity()
        {
            EnsureOpen(nameof(SetSecurity));
            _controller.ApplyOptions(new FlashOptionState { SetSecurity = true });
        }

        /// <summary>
        /// Reads the chip's factory-programmed unique identifier as 32-bit words. Empty on families
        /// that don't expose one (legacy EFC, SAM7L, SAM9XE) and on a part identified only by
        /// fallback, both of which <see cref="SamBaChipInfo.HasUniqueId"/> reports without a read.
        /// <para>
        /// Read from the device once per <see cref="Open"/> and remembered, empty results included.
        /// The value is programmed at the factory, so nothing this library does can change it while
        /// the port is open, and on the EEFC the read is not free: it maps the id over the flash
        /// window for the span of a command pair, during which no other flash read may happen. (On the
        /// NVMCTRL parts it is four word reads of always-mapped read-only NVM, and cheap.) Caching
        /// keeps a caller that reads it repeatedly — a UI binding, a log line per operation — from
        /// re-entering that sequence each time.
        /// </para>
        /// </summary>
        /// <returns>
        /// The identifier as 32-bit words, or empty on a part that has none. The same instance every
        /// call within one <see cref="Open"/>, and read-only — a cast back to <c>uint[]</c> would
        /// otherwise let one caller rewrite what the next one reads.
        /// </returns>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed, an access timed out, or the device was removed.
        /// </exception>
        /// <exception cref="SamBaFlashCommandException">The unique-id command pair failed.</exception>
        /// <exception cref="SamBaFlashTimeoutException">
        /// The controller did not report ready in time, before or after the read.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public IReadOnlyList<uint> GetUniqueId()
        {
            EnsureOpen(nameof(GetUniqueId));
            if (_uniqueId == null)
                _uniqueId = _controller.GetUniqueId();

            return _uniqueId;
        }

        /// <summary>
        /// Resets the CPU (RSTC/AIRCR write) and closes the device. The USB CDC port is expected to
        /// drop as the device restarts. Closes the device whether or not the reset was triggered —
        /// a reset is the last thing a session does, so a failure here must not leave the port open.
        /// </summary>
        /// <exception cref="SamBaUnsupportedOperationException">
        /// <c>DeviceResetter</c> has no reset route for this part, so nothing was written. Reached by
        /// a part identified only by family fallback whose family is
        /// <see cref="SamBaChipFamily.Unknown"/> <em>and</em> whose <see cref="SamBaChipInfo.CpuId"/>
        /// is also null, meaning even the legacy-versus-Cortex-M probe did not confirm a Cortex-M
        /// core — such a part programs but cannot be reset from here, and power-cycling it is the way
        /// out. The device is closed either way. For a table-listed part it stays unreachable: every
        /// family the table reports has a route, so a missing one would be a gap between the chip
        /// table and the reset code.
        /// </exception>
        /// <exception cref="SamBaDeviceNotOpenException">The device is not open.</exception>
        /// <exception cref="SamBaTransportException">
        /// The port failed before the reset was written. A port that drops as the device restarts is
        /// the expected outcome and is not reported — the reset write is not waited on for a reply.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This device has been disposed.</exception>
        public void Reset()
        {
            EnsureOpen(nameof(Reset));
            bool supported = DeviceResetter.TryReset(_monitor, ChipInfo.Family, ChipInfo.CpuId);
            Close();
            if (!supported)
                throw new SamBaUnsupportedOperationException(
                    nameof(Reset),
                    $"No reset route for {ChipInfo.Family}; power-cycle the device to leave the SAM-BA monitor.");
        }

        /// <summary>
        /// Identity, port, open state, monitor version and chip, one per line. Diagnostic text; the
        /// format is not stable and nothing parses it back.
        /// </summary>
        /// <returns>The summary, one "key: value" per line.</returns>
        public override string ToString() => ToString(string.Empty, ": ", Environment.NewLine);

        /// <summary>
        /// The same summary as <see cref="ToString()"/> with separators of your choosing — for folding
        /// it into a log line or a table row instead of a block.
        /// </summary>
        /// <param name="prefixSeparator">String prepended before each key; null is treated as empty.</param>
        /// <param name="keyValueSeparator">String between key and value; null is treated as empty.</param>
        /// <param name="suffixSeparator">String appended after each value; null is treated as empty.</param>
        /// <returns>The summary, assembled with those separators.</returns>
        public string ToString(string prefixSeparator, string keyValueSeparator, string suffixSeparator)
        {
            var sb = new StringBuilder();
            Append(sb, nameof(DisplayName), DisplayName, prefixSeparator, keyValueSeparator, suffixSeparator);
            Append(sb, nameof(DevicePath), DevicePath, prefixSeparator, keyValueSeparator, suffixSeparator);
            Append(sb, nameof(PortName), PortName, prefixSeparator, keyValueSeparator, suffixSeparator);
            Append(sb, nameof(IsOpen), IsOpen.ToString(), prefixSeparator, keyValueSeparator, suffixSeparator);
            Append(
                sb, nameof(MonitorVersion), MonitorVersion, prefixSeparator, keyValueSeparator, suffixSeparator);
            Append(
                sb, nameof(ChipInfo), ChipInfo?.ToString() ?? "<not identified>",
                prefixSeparator, keyValueSeparator, suffixSeparator);
            return sb.ToString();
        }

        /// <summary>
        /// Closes the device and releases the transport. Safe to call more than once, and throws
        /// nothing a caller has to catch, for the reason <see cref="Close"/> gives. The device cannot be
        /// reopened afterwards — every member then raises <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
            _monitor.Dispose();
        }

        private static void Append(
            StringBuilder sb, string key, string value, string prefix, string kv, string suffix)
        {
            sb.Append(prefix).Append(key).Append(kv).Append(value).Append(suffix);
        }

        /// <summary>
        /// Clears every lock region before an update, skipping the work when nothing is locked.
        /// Deliberately broader than a <see cref="FlashLockScope.Written"/> lock that may follow the
        /// update: a full erase blanks the whole flash, so a region left locked anywhere would fail
        /// the erase no matter where the image itself lands.
        /// </summary>
        private void UnlockAllIfLocked()
        {
            bool anyLocked = false;
            foreach (bool locked in _controller.GetLockRegions())
            {
                if (locked)
                {
                    anyLocked = true;
                    break;
                }
            }

            if (!anyLocked)
                return;

            RaiseProgress(SamBaProgressEventArgs.Indeterminate(
                "Unlocking flash regions", ProgressStage.Unlocking));
            _controller.ApplyOptions(new FlashOptionState { Lock = false });
        }

        private FlashProgrammer CreateProgrammer() => new FlashProgrammer(_controller, RaiseProgress);

        /// <summary>
        /// Returns true and the byte offset into flash when <paramref name="address"/> ..
        /// <paramref name="length"/> lies entirely within the flash window; false when it is
        /// entirely outside (a raw memory write). Throws when the range only partially overlaps
        /// flash, which would mean writing part flash and part not.
        /// </summary>
        private bool TryGetFlashOffset(uint address, long length, out uint offset)
        {
            offset = 0;
            uint flashBase = _controller.FlashAddress;
            long flashEnd = flashBase + _controller.FlashSize;   // exclusive
            long rangeEnd = (long)address + length;              // exclusive

            if (address >= flashBase && rangeEnd <= flashEnd)
            {
                offset = address - flashBase;
                return true;
            }

            bool overlapsFlash = address < flashEnd && rangeEnd > flashBase;
            if (overlapsFlash)
                throw new ArgumentException(
                    $"Write range 0x{address:X8}..0x{rangeEnd - 1:X8} straddles the flash boundary " +
                    $"(flash is 0x{flashBase:X8}..0x{flashEnd - 1:X8}); split it into flash and non-flash writes.",
                    nameof(address));

            return false;
        }

        private void RaiseProgress(SamBaProgressEventArgs args) => ProgressChanged?.Invoke(this, args);

        private void EnsureOpen(string operationLabel)
        {
            ThrowIfDisposed();
            if (!IsOpen)
                throw new SamBaDeviceNotOpenException(operationLabel);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SamBaDevice));
        }

#if SAMBA_WIN32
        private void ProbePortMetadata(string devicePath)
        {
            // Port name and VID/PID come from the PnP registry / device path and are
            // available without opening the port; best effort only.
            try
            {
                using (var probe = new Anp.Serial.Win32.SerialDevice(devicePath))
                {
                    PortName = probe.PortName ?? string.Empty;
                    FriendlyName = probe.FriendlyName ?? string.Empty;
                    VendorId = probe.VendorId;
                    ProductId = probe.ProductId;
                }
            }
            catch (Exception)
            {
                // Metadata is cosmetic; never fail construction over it.
            }
        }
#endif
    }
}

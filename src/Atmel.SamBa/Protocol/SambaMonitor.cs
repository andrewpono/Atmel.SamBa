using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Anp.Atmel.SamBa.Protocol
{
    /// <summary>
    /// SAM-BA monitor protocol over a USB CDC serial port. USB CDC only — the UART/DBGU
    /// paths (XMODEM transfer, auto-baud) are not implemented.
    /// </summary>
    /// <remarks>
    /// Every command is ASCII: one letter, uppercase-hex arguments, <c>#</c>-terminated, no trailing
    /// NUL. The letters and punctuation are enumerated once in the nested <c>Syntax</c> class — and
    /// not repeated here, so the two cannot drift apart.
    /// <para>
    /// Not thread-safe, by design: one monitor per device, driven by its owning
    /// <see cref="SamBaDevice"/>. Commands are a request/reply exchange over a single serial port,
    /// so concurrent callers would interleave on the wire regardless of the shared scratch buffers.
    /// </para>
    /// </remarks>
    internal sealed class SambaMonitor : IDisposable
    {
        /// <summary>
        /// The monitor's wire syntax: one command letter, hex arguments separated by
        /// <see cref="ArgumentSeparator"/>, closed by <see cref="Terminator"/>. Gathered here
        /// because several letters serve more than one role — <c>X</c> is the chip-erase command,
        /// the flag advertising it in the version string, and the reply the command echoes — so a
        /// letter must not be spelled out at each site.
        /// </summary>
        private static class Syntax
        {
            /// <summary>Switch the monitor to binary (non-terminal) mode.</summary>
            public const char BinaryMode = 'N';

            /// <summary>Report the version string.</summary>
            public const char Version = 'V';

            /// <summary>Read a 32-bit word.</summary>
            public const char ReadWord = 'w';

            /// <summary>Write a 32-bit word.</summary>
            public const char WriteWord = 'W';

            /// <summary>Read a byte.</summary>
            public const char ReadByte = 'o';

            /// <summary>Write a byte.</summary>
            public const char WriteByte = 'O';

            /// <summary>Read a memory block as a binary stream.</summary>
            public const char ReadBlock = 'R';

            /// <summary>Write a memory block as a binary stream.</summary>
            public const char WriteBlock = 'S';

            /// <summary>Jump to an address.</summary>
            public const char Go = 'G';

            /// <summary>Erase the whole chip (bootloader extension, not in the ROM monitor).</summary>
            public const char ChipErase = 'X';

            /// <summary>Separates one command's arguments.</summary>
            public const char ArgumentSeparator = ',';

            /// <summary>Closes every command.</summary>
            public const char Terminator = '#';

            /// <summary>
            /// Count argument of the word and byte reads — <see cref="BytesPerWord"/> in decimal,
            /// not hex. Both commands carry it, though <see cref="ReadByte"/> answers with one byte.
            /// <para>
            /// Spelled out rather than derived, because <c>BytesPerWord.ToString()</c> is not a
            /// constant expression, so the two are kept in step by hand. Not by vigilance alone: the
            /// word-read test pins both halves of one exchange — the <c>,4#</c> argument text and a
            /// four-byte reply — so raising either constant on its own fails it.
            /// </para>
            /// </summary>
            public const string ReadCountArgument = "4";

            /// <summary>Argument digits, uppercase as the monitor prints them.</summary>
            public const string HexDigits = "0123456789ABCDEF";

            /// <summary>Bits carried by one hex digit.</summary>
            public const int BitsPerHexDigit = 4;

            /// <summary>Mask selecting one hex digit's worth of bits.</summary>
            public const int HexDigitMask = 0xF;

            /// <summary>Digits in a 32-bit address or value argument.</summary>
            public const int HexDigitsPerWord = BytesPerWord * 2;

            /// <summary>
            /// Opens the extension banner in the version string. A bootloader convention layered on
            /// the <c>V#</c> reply, not part of the ROM monitor's command set — but the flag letters
            /// inside it are the command letters above, which is why it lives here.
            /// </summary>
            public const string ArduinoExtensionsStart = "[Arduino:";

            /// <summary>Closes the extension banner.</summary>
            public const char ArduinoExtensionsEnd = ']';

            /// <summary>First printable ASCII code (space) — the version reply is trimmed to these.</summary>
            public const byte FirstPrintableAscii = 0x20;

            /// <summary>Last printable ASCII code (tilde); 0x7F is DEL.</summary>
            public const byte LastPrintableAscii = 0x7E;
        }

        //
        // Fixed sizes, in one block: each is a wire or buffer fact worth finding without reading
        // the command that happens to use it.
        //

        /// <summary>Bytes in a 32-bit word — the unit of the <c>w#</c> / <c>W#</c> commands.</summary>
        private const int BytesPerWord = 4;

        /// <summary>
        /// Wire length of one <c>W#</c> word write — command letter, 8-digit hex address, argument
        /// separator, 8-digit hex value, terminator: <c>W00080000,DEADBEEF#</c>.
        /// </summary>
        internal const int WriteWordCommandLength =
            1 + Syntax.HexDigitsPerWord + 1 + Syntax.HexDigitsPerWord + 1;

        /// <summary>
        /// Wire length of one <c>w#</c> word read — command letter, 8-digit hex address, argument
        /// separator, the one-character <see cref="Syntax.ReadCountArgument"/>, terminator:
        /// <c>w00080000,4#</c>. The count's length is spelled as a literal 1 because a string's
        /// <c>Length</c> is not a constant expression; the word-read pipelining test pins the
        /// encoded command text, so the two cannot drift apart unnoticed.
        /// </summary>
        internal const int ReadWordCommandLength = 1 + Syntax.HexDigitsPerWord + 1 + 1 + 1;

        /// <summary>
        /// Words per pipelined batch in <see cref="ReadViaWords"/>: one transport write carries this
        /// many <c>w#</c> commands back-to-back, then their replies are drained in one read. Some
        /// bound is needed because each batch's replies are waited out on one fixed budget; 128
        /// words keeps both directions small (1536 bytes of commands, 512 of replies) while already
        /// amortizing the round trip well — going wider mostly grows what a stall leaves undrained.
        /// </summary>
        private const int ReadWordBatchWords = 128;

        /// <summary>
        /// Words per batched transport write in <see cref="WriteViaWords"/>. <c>W#</c> sends no
        /// reply, so no reply budget constrains this bound the way it does the read one — it is
        /// there to keep one transport write inside the fixed budget it is waited out on, and the
        /// scratch blob a fixed size. 128 words is a whole page on the largest-page parts, so a
        /// latch load still travels as one write per page, which the hardware has always seen.
        /// </summary>
        private const int WriteWordBatchWords = 128;

        /// <summary>USB full-speed bulk endpoint maximum packet size (SAM-BA native USB is full-speed).</summary>
        private const int UsbBulkMaxPacket = 64;

        /// <summary>
        /// Longest single <c>R#</c> or <c>S#</c> transfer this library issues. Some bound is needed in
        /// both directions, because a transfer is waited out on a fixed budget: unbounded, a
        /// whole-image verify travelled as one read and a large RAM write as one write, and a
        /// multi-hundred-KB transfer does not finish inside a budget sized for a single command.
        /// Chunking also bounds how long a transfer that has died mid-stream goes unnoticed, which one
        /// long transfer cannot.
        /// <para>
        /// One byte below 256 KB on purpose, and the odd byte is a read-path requirement: a chunk that
        /// is an exact multiple of <see cref="UsbBulkMaxPacket"/> trips the byte-peel path in
        /// <see cref="Read"/> on every full chunk, paying an extra <c>o#</c> round-trip each time.
        /// Nothing about <see cref="Write"/> needs it, but one shared bound is worth more than the
        /// byte it saves there.
        /// </para>
        /// </summary>
        internal const int DefaultBlockChunk = (256 * 1024) - 1;

        /// <summary>Length of the <c>X#</c> reply: the echoed command letter and an LF/CR pair.</summary>
        private const int ChipEraseReplyLength = 3;

        /// <summary>
        /// Longest <c>V#</c> reply accepted, and so the bound on how much the version read
        /// accumulates before giving up on a terminator; the string itself is far shorter.
        /// </summary>
        private const int MaxVersionReplyLength = 255;

        /// <summary>
        /// Shortest <c>V#</c> reply accepted as a version string. The shortest one any monitor here
        /// prints is <c>v1.1</c>, so a printable run below this length is not a version at all — see
        /// <see cref="IsPlausibleVersion"/> for why that has to fail the connect.
        /// </summary>
        private const int MinVersionLength = 4;

        /// <summary>
        /// How long <see cref="ClearStalePipe"/> waits between its two purges, for bytes that were
        /// already on the wire when the first one ran. Sized for flight time and nothing more: the
        /// port was opened microseconds earlier, so a packet the driver is part-way through receiving
        /// lands within a USB frame or two. Nominally 10 ms, in practice the next system timer tick —
        /// see <see cref="FlushDelay"/> for why that distinction is left alone.
        /// <para>
        /// A device still streaming a previous session's data is deliberately not this figure's
        /// problem: no settle window bounds that, and <see cref="ReadVersion"/> is what catches it.
        /// </para>
        /// </summary>
        private const int PipeSettleDelayMs = 10;

        //
        // Timeouts. NormalTimeout and LongTimeout are read across the layer boundary by the flash
        // controllers, which wait out their own register commands on them; the chip-erase default
        // seeds the per-instance ChipEraseTimeout property below, which they read at each wait.
        // QuickTimeout is private, so the handshake's budget cannot be depended on from outside.
        //

        /// <summary>
        /// Timeout for the handshake's one opportunistic read: the read that closes off the version
        /// reply, which a monitor sending no terminator never answers. It may legitimately return
        /// nothing, so a connect can still cost this much once — which is why it is the only read in
        /// the handshake that is allowed to come back empty, and why nothing there reads on spec.
        /// </summary>
        private static readonly TimeSpan QuickTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary>Default command/reply timeout.</summary>
        public static readonly TimeSpan NormalTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Timeout for a single long-running device operation — generous next to any one flash
        /// command, but deliberately not sized for a whole-chip erase (see <see cref="ChipEraseTimeout"/>).
        /// <para>
        /// Also the budget for one <c>R#</c> or <c>S#</c> block transfer, which is bulk data rather
        /// than a command and so takes time in proportion to its length: a full
        /// <see cref="DefaultBlockChunk"/> moves in a fraction of a second at full-speed USB CDC
        /// rates, leaving better than tenfold margin without letting a transfer that has stalled hang
        /// the caller indefinitely.
        /// </para>
        /// </summary>
        public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long <see cref="Connect"/>'s first <see cref="ISambaTransport.Open"/> call may take
        /// before the connect attempt is abandoned without ever trying <see cref="Handshake"/>.
        /// Opening is where the real stall lives: <c>DtrEnable</c> carries no timeout of its own and
        /// can run tens of seconds when the far end is not answering (see DESIGN.md "Timeout
        /// ownership"). A port already this slow to open is not worth the further cost of a handshake
        /// attempt — and, on failure, <see cref="HandshakeWithRetry"/>'s one retry, itself another
        /// <c>Open</c> just as likely to stall — when both stand very little chance of succeeding.
        /// <para>
        /// A multiple of <see cref="NormalTimeout"/> rather than its own literal, so the margin above
        /// a normal open stays proportional if that budget ever changes.
        /// </para>
        /// </summary>
        private static readonly TimeSpan OpenPortBudget =
            TimeSpan.FromTicks(NormalTimeout.Ticks * 5);

        /// <summary>
        /// Default for <see cref="ChipEraseTimeout"/>.
        /// <para>
        /// A whole-chip erase is one command however it is issued — the monitor's <c>X#</c>, or an
        /// EFC-family erase-all polled to completion — and which route a part takes depends only on
        /// whether its bootloader advertises the extension, so one budget covers both. It is sized
        /// for the slowest part in the device table rather than the slowest single flash command:
        /// the datasheet maxima run to roughly 25 s (a 1 MB SAMD51/E5x, a 2 MB SAME70/S70/V71) and
        /// a bootloader may reach that by looping per-unit erases itself, so this doubles it for
        /// margin.
        /// </para>
        /// </summary>
        public static readonly TimeSpan DefaultChipEraseTimeout = TimeSpan.FromSeconds(60);

        private readonly ISambaTransport _transport;

        // Per-instance scratch for the word and byte reads, and for the one W# command WriteWord
        // sends. Reused rather than allocated per command, which is part of why a monitor is
        // single-threaded (see the class remarks).
        private readonly byte[] _replyBuffer = new byte[BytesPerWord];
        private readonly byte[] _wordCommand = new byte[WriteWordCommandLength];

        // Scratch for ReadViaWords' pipelined batches and WriteViaWords' command blob, each
        // allocated on first use: parts whose monitor serves block reads never pay for the read
        // pair, and a session that never writes flash never pays for the write blob.
        private byte[] _readBatchCommands;
        private byte[] _readBatchReplies;
        private byte[] _writeBatchCommands;
        private TimeSpan _chipEraseTimeout = DefaultChipEraseTimeout;
        private bool _connected;

        internal SambaMonitor(ISambaTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Version = string.Empty;
        }

        /// <summary>
        /// Optional sink for indeterminate connect/identify status messages. Set by the owning
        /// <see cref="SamBaDevice"/> so the handshake and chip probe can report progress.
        /// </summary>
        internal Action<SamBaProgressEventArgs> ProgressReporter { get; set; }

        /// <summary>
        /// Sends one indeterminate progress message to <see cref="ProgressReporter"/>, or nowhere
        /// when nothing is listening. Internal rather than private because the chip probe reports
        /// through this monitor as well (<c>ChipIdentifier</c>), and letting it build the args itself
        /// would put two spellings of one report in the assembly.
        /// </summary>
        internal void Report(string message, string stage) =>
            ProgressReporter?.Invoke(SamBaProgressEventArgs.Indeterminate(message, stage));

        /// <summary>System path of the underlying device (for diagnostics).</summary>
        public string DevicePath => _transport.DevicePath;

        /// <summary>True after a successful <see cref="Connect"/>.</summary>
        public bool IsConnected => _connected && _transport.IsOpen;

        /// <summary>
        /// Monitor version string returned by <c>V#</c>; empty before the first connect, and kept
        /// from the last one after a disconnect.
        /// </summary>
        public string Version { get; private set; }

        /// <summary>
        /// How this monitor differs from the baseline, read out of the version string. Reports
        /// <see cref="SambaCapabilities.RomMonitor"/> before the first connect and for every monitor
        /// without an Arduino extension banner; like <see cref="Version"/>, kept from the last
        /// connect after a disconnect.
        /// </summary>
        public SambaCapabilities Capabilities { get; private set; }

        /// <summary>
        /// When true, the methods that would batch many commands into one transport write —
        /// <see cref="WriteViaWords"/>, which loads the flash write latch, and
        /// <see cref="ReadViaWords"/> — instead fall back to one word per exchange. Avoids
        /// overrunning bootloaders that cannot consume a back-to-back command stream.
        /// <para>
        /// Far more than proportionally slower, because every <see cref="WriteWord"/> ends in
        /// <see cref="FlushDelay"/>: the cost is one sleep per word rather than one per write block.
        /// See <see cref="FlushDelay"/> for what that comes to.
        /// </para>
        /// </summary>
        public bool SafeMode { get; set; }

        /// <summary>
        /// Budget for a whole-chip erase, defaulting to <see cref="DefaultChipEraseTimeout"/>.
        /// Raise it for a part or bootloader slower than the device table's worst case: abandoning
        /// an erase part-way through fails a write that would have succeeded and leaves flash
        /// half-erased. Read at each wait, so a change takes effect on the next one.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
        public TimeSpan ChipEraseTimeout
        {
            get => _chipEraseTimeout;
            set => _chipEraseTimeout = value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "Chip-erase timeout must be positive.");
        }

        /// <summary>
        /// Opens the transport, switches the monitor to binary mode and reads the version string.
        /// </summary>
        /// <exception cref="SamBaTransportException">
        /// The device did not answer as a SAM-BA monitor, or answered with data left over from an
        /// interrupted session instead of a version string (see <see cref="IsPlausibleVersion"/>).
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Opening the port took longer than <see cref="OpenPortBudget"/>; no handshake was attempted.
        /// </exception>
        public void Connect()
        {
            Report("Connecting to device", ProgressStage.Connecting);
            // Covers every Open/Close/Purge the handshake below may issue (including its one
            // retry) — none of those calls carry a timeout of their own, so this is what turns a
            // stall inside one into a visible "stuck on X" instead of a silent gap.
            _transport.StatusReporter = message => Report(message, ProgressStage.Connecting);

            var stopwatch = Stopwatch.StartNew();
            _transport.Open();

            try
            {
                // A port already this slow to open is unlikely to answer a handshake either, and
                // would only go on to charge HandshakeWithRetry's one retry for another Open() just
                // as slow — so the handshake is not attempted at all once opening alone has already
                // spent the budget.
                if (stopwatch.Elapsed > OpenPortBudget)
                {
                    throw new TimeoutException(
                        $"Device open took {stopwatch.Elapsed.TotalSeconds:F1} s, longer than the " +
                        $"{OpenPortBudget.TotalSeconds:F1} s budget — not attempting a handshake. " +
                        "Restart the device and try again.");
                }

                HandshakeWithRetry();
            }
            catch
            {
                // Nothing else will close a port this method opened — the caller gets an exception
                // rather than a connected monitor — and a Windows serial port stays exclusively
                // claimed until closed, blocking the next attempt even from a fresh device object.
                _transport.Close();
                throw;
            }

            _connected = true;
        }

        /// <summary>Closes the transport.</summary>
        public void Disconnect()
        {
            _connected = false;
            _transport.StatusReporter = message => Report(message, ProgressStage.Disconnecting);
            _transport.Close();
        }

        /// <summary>Reads a 32-bit word (<c>w#</c> command, little-endian reply).</summary>
        public uint ReadWord(uint address)
        {
            WriteCommand(Syntax.ReadWord, Hex(address), Syntax.ReadCountArgument);
            _transport.ReadExact(_replyBuffer, 0, BytesPerWord, NormalTimeout);

            // Shifted by hand rather than through BitConverter: the reply is little-endian because
            // the target is, which BitConverter would only match while the host happens to agree.
            return (uint)(_replyBuffer[0]
                | (_replyBuffer[1] << 8)
                | (_replyBuffer[2] << 16)
                | (_replyBuffer[3] << 24));
        }

        /// <summary>Writes a 32-bit word (<c>W#</c> command).</summary>
        public void WriteWord(uint address, uint value)
        {
            EncodeWriteWord(_wordCommand, 0, address, value);
            _transport.Write(_wordCommand, 0, WriteWordCommandLength, NormalTimeout);
            // The SAM firmware gets confused when a command shares a USB packet with following
            // traffic, so insert a flush (1 ms delay) after every word/byte write and go.
            FlushDelay();
        }

        /// <summary>
        /// Encodes one <c>W#</c> word write into <paramref name="blob"/> at
        /// <paramref name="offset"/>, returning the position just past it; always writes
        /// <see cref="WriteWordCommandLength"/> bytes. The only producer of that command's text:
        /// <see cref="WriteWord"/> sends a single one, and <see cref="WriteViaWords"/> batches a
        /// page's worth.
        /// </summary>
        private static int EncodeWriteWord(byte[] blob, int offset, uint address, uint value)
        {
            blob[offset++] = (byte)Syntax.WriteWord;
            offset = AppendHexWord(blob, offset, address);
            blob[offset++] = (byte)Syntax.ArgumentSeparator;
            offset = AppendHexWord(blob, offset, value);
            blob[offset++] = (byte)Syntax.Terminator;
            return offset;
        }

        /// <summary>
        /// Encodes one <c>w#</c> word read into <paramref name="blob"/> at
        /// <paramref name="offset"/>, returning the position just past it; always writes
        /// <see cref="ReadWordCommandLength"/> bytes. Only <see cref="ReadViaWords"/> batches these —
        /// a single word read goes through <see cref="ReadWord"/>, whose command text
        /// <see cref="WriteCommand"/> builds.
        /// </summary>
        private static int EncodeReadWord(byte[] blob, int offset, uint address)
        {
            blob[offset++] = (byte)Syntax.ReadWord;
            offset = AppendHexWord(blob, offset, address);
            blob[offset++] = (byte)Syntax.ArgumentSeparator;
            blob[offset++] = (byte)Syntax.ReadCountArgument[0];
            blob[offset++] = (byte)Syntax.Terminator;
            return offset;
        }

        /// <summary>
        /// Appends <paramref name="value"/> as a fixed-width hex argument, most significant digit
        /// first, returning the position just past it.
        /// </summary>
        private static int AppendHexWord(byte[] blob, int offset, uint value)
        {
            for (int digit = Syntax.HexDigitsPerWord - 1; digit >= 0; digit--)
            {
                int index = (int)(value >> (digit * Syntax.BitsPerHexDigit)) & Syntax.HexDigitMask;
                blob[offset++] = (byte)Syntax.HexDigits[index];
            }

            return offset;
        }

        /// <summary>
        /// Reads a little-endian 32-bit word out of <paramref name="buffer"/> — shifted by hand
        /// for the same reason <see cref="ReadWord"/> unpacks its reply by hand.
        /// </summary>
        private static uint ReadLittleEndianWord(byte[] buffer, int offset) =>
            (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));

        /// <summary>Reads a byte (<c>o#</c> command).</summary>
        public byte ReadByte(uint address)
        {
            WriteCommand(Syntax.ReadByte, Hex(address), Syntax.ReadCountArgument);
            _transport.ReadExact(_replyBuffer, 0, 1, NormalTimeout);
            return _replyBuffer[0];
        }

        /// <summary>Writes a byte (<c>O#</c> command).</summary>
        public void WriteByte(uint address, byte value)
        {
            WriteCommand(Syntax.WriteByte, Hex(address), Hex(value));
            FlushDelay();
        }

        /// <summary>
        /// Reads a memory block via <c>R#</c> binary streams, applying the USB quirks: reads are
        /// chunked to <see cref="SambaCapabilities.ReadChunkLimit"/> when the monitor advertises one
        /// and to <see cref="DefaultBlockChunk"/> when it does not, and any <c>R#</c> whose length is
        /// an exact multiple of the 64-byte bulk max packet is trimmed by one byte — such a transfer
        /// isn't terminated by a short packet, so the read would hang.
        /// </summary>
        public void Read(uint address, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));

            // Every monitor gets a cap, its own or ours: the loop below waits each chunk out on one
            // fixed budget, which only means anything if the chunk has a bound.
            int chunkLimit = Capabilities.ReadChunkLimit ?? DefaultBlockChunk;

            while (count > 0)
            {
                int chunk = Math.Min(count, chunkLimit);

                // A USB-CDC bulk IN transfer whose length is an exact multiple of the 64-byte max
                // packet size is not followed by a terminating short packet, so the host read never
                // completes. Peel the first byte with a byte-read so the R# length isn't a multiple
                // of 64 (this subsumes the "power of two over 32 bytes" case reported for the SAM
                // firmware — those are just the powers of two that are multiples of 64).
                if (chunk >= UsbBulkMaxPacket && chunk % UsbBulkMaxPacket == 0)
                {
                    buffer[offset] = ReadByte(address);
                    address++;
                    offset++;
                    count--;
                    chunk = Math.Min(count, chunkLimit);

                    // Peeling a byte only breaks the multiple when the chunk shrinks along with the
                    // remaining count. A chunk limit that is itself a multiple of the max packet
                    // would pin the recomputed chunk back at the limit, so shorten it by hand and
                    // let the next iteration collect the byte left over. Neither limit in use is
                    // such a multiple — both are one below a round number for exactly that reason —
                    // so this is a backstop for a future limit chosen without that in mind.
                    if (chunk >= UsbBulkMaxPacket && chunk % UsbBulkMaxPacket == 0)
                        chunk--;
                }

                WriteCommand(Syntax.ReadBlock, Hex(address), Hex((uint)chunk));
                _transport.ReadExact(buffer, offset, chunk, LongTimeout);

                address += (uint)chunk;
                offset += chunk;
                count -= chunk;
            }
        }

        /// <summary>
        /// Reads a memory block via <c>w#</c> word reads — <see cref="Read"/>'s signature, without
        /// its <c>R#</c> stream. Exists because some ROM monitors answer a block read of flash, or
        /// of the boot memory at address 0 that remaps it, with all zeros; a word read of the same
        /// address returns the real content, so block reads on such parts travel this way instead.
        /// </summary>
        /// <remarks>
        /// One <c>w#</c> costs a round trip, so the words are pipelined: up to
        /// <see cref="ReadWordBatchWords"/> commands go out as one transport write, then their
        /// replies — four little-endian bytes each, which for consecutive words is exactly the
        /// destination byte stream — are drained in one read. No <see cref="FlushDelay"/> is
        /// needed anywhere in that: unlike the write-side batches, every batch here ends by
        /// waiting for its replies, which is all the separation the firmware asks for. The
        /// multiple-of-64 trim <see cref="Read"/> applies has no counterpart either — the replies
        /// arrive as many short transfers, not one exact-multiple bulk transfer.
        /// <para>
        /// <see cref="SafeMode"/> falls back to one command/reply round trip per word: a
        /// bootloader that cannot consume back-to-back commands in one transfer would drop parts
        /// of a batch exactly as it drops <see cref="WriteViaWords"/>' batches.
        /// </para>
        /// <para>
        /// An unaligned <paramref name="address"/> or <paramref name="count"/> is served from the
        /// containing words, so every bus access is a whole aligned word — sound for flash and
        /// RAM, where reading a neighbouring byte disturbs nothing, but not for a register window
        /// whose neighbours carry read side effects. Register callers already have
        /// <see cref="ReadWord"/>.
        /// </para>
        /// </remarks>
        public void ReadViaWords(uint address, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));

            // Unaligned head: the covered bytes of the word containing address.
            int headSkip = (int)(address % BytesPerWord);
            if (headSkip != 0 && count > 0)
            {
                uint word = ReadWord(address - (uint)headSkip);
                int take = Math.Min(BytesPerWord - headSkip, count);
                for (int i = 0; i < take; i++)
                    buffer[offset + i] = (byte)(word >> ((headSkip + i) * 8));

                address += (uint)take;
                offset += take;
                count -= take;
            }

            int words = count / BytesPerWord;
            int tail = count % BytesPerWord;

            if (SafeMode)
            {
                for (int w = 0; w < words; w++)
                {
                    uint word = ReadWord(address);
                    buffer[offset] = (byte)word;
                    buffer[offset + 1] = (byte)(word >> 8);
                    buffer[offset + 2] = (byte)(word >> 16);
                    buffer[offset + 3] = (byte)(word >> 24);
                    address += BytesPerWord;
                    offset += BytesPerWord;
                }
            }
            else if (words > 0)
            {
                if (_readBatchCommands == null)
                {
                    _readBatchCommands = new byte[ReadWordBatchWords * ReadWordCommandLength];
                    _readBatchReplies = new byte[ReadWordBatchWords * BytesPerWord];
                }

                while (words > 0)
                {
                    int batch = Math.Min(words, ReadWordBatchWords);

                    int pos = 0;
                    for (int w = 0; w < batch; w++)
                        pos = EncodeReadWord(_readBatchCommands, pos, address + (uint)(w * BytesPerWord));

                    _transport.Write(_readBatchCommands, 0, pos, NormalTimeout);

                    int replyLength = batch * BytesPerWord;
                    _transport.ReadExact(_readBatchReplies, 0, replyLength, LongTimeout);
                    Buffer.BlockCopy(_readBatchReplies, 0, buffer, offset, replyLength);

                    address += (uint)replyLength;
                    offset += replyLength;
                    words -= batch;
                }
            }

            // Unaligned tail: the leading bytes of the word containing the end of the range.
            if (tail > 0)
            {
                uint word = ReadWord(address);
                for (int i = 0; i < tail; i++)
                    buffer[offset + i] = (byte)(word >> (i * 8));
            }
        }

        /// <summary>
        /// Writes a memory block via <c>S#</c> binary streams, chunked to
        /// <see cref="DefaultBlockChunk"/> so that no single transfer outgrows the budget it is waited
        /// out on. Unlike <see cref="Read"/> no length is trimmed: the multiple-of-64 hang is an
        /// IN-transfer problem, and on the way out the monitor already knows how many bytes to expect
        /// from the <c>S#</c> argument, so it never has to infer the end from a short packet.
        /// <para>
        /// The monitor caps nothing here of its own — <see cref="SambaCapabilities.ReadChunkLimit"/>
        /// is a read-direction defect and has no write counterpart.
        /// </para>
        /// </summary>
        public void Write(uint address, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));

            while (count > 0)
            {
                int chunk = Math.Min(count, DefaultBlockChunk);

                WriteCommand(Syntax.WriteBlock, Hex(address), Hex((uint)chunk));
                // Separate the command from the binary payload; the SAM firmware misparses
                // them when the USB driver combines both into one packet.
                FlushDelay();
                _transport.Write(buffer, offset, chunk, LongTimeout);

                address += (uint)chunk;
                offset += chunk;
                count -= chunk;
            }
        }

        /// <summary>
        /// Writes a memory block via <c>W#</c> word writes — <see cref="Write"/>'s signature plus
        /// word granularity, and <see cref="ReadViaWords"/>' write-direction counterpart. The way
        /// every flash controller loads the page latch: <c>W#</c> sends no reply, so up to
        /// <see cref="WriteWordBatchWords"/> commands travel as one transport write and the
        /// monitor consumes them back-to-back — one USB round-trip per batch instead of one per
        /// word, which is what replaces an on-target word-copy applet.
        /// </summary>
        /// <remarks>
        /// Word-granular on purpose, where <see cref="ReadViaWords"/> is not: an unaligned read
        /// can be served from the containing words without anyone noticing, but a containing-word
        /// write would clobber the neighbouring bytes — merging into surrounding content is the
        /// flash layer's read-modify-write, not a wire concern.
        /// <para>
        /// <see cref="SafeMode"/> falls back to one <see cref="WriteWord"/> round trip per word,
        /// each paying the per-command flush — see <see cref="FlushDelay"/> for what that costs.
        /// </para>
        /// </remarks>
        public void WriteViaWords(uint address, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));
            // Alignment last, so each complaint only speaks for values otherwise in range.
            if (address % BytesPerWord != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(address), "Word writes need a word-aligned address.");
            if (count % BytesPerWord != 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Word writes are word-granular.");

            int words = count / BytesPerWord;

            if (SafeMode)
            {
                // No command batching: one W# round-trip per word. Slow but robust on
                // bootloaders that drop commands sent back-to-back in a single transfer.
                for (int w = 0; w < words; w++)
                {
                    WriteWord(address, ReadLittleEndianWord(buffer, offset));
                    address += BytesPerWord;
                    offset += BytesPerWord;
                }
                return;
            }

            if (words == 0)
                return;

            if (_writeBatchCommands == null)
                _writeBatchCommands = new byte[WriteWordBatchWords * WriteWordCommandLength];

            while (words > 0)
            {
                int batch = Math.Min(words, WriteWordBatchWords);

                int pos = 0;
                for (int w = 0; w < batch; w++)
                {
                    pos = EncodeWriteWord(_writeBatchCommands, pos, address, ReadLittleEndianWord(buffer, offset));
                    address += BytesPerWord;
                    offset += BytesPerWord;
                }

                _transport.Write(_writeBatchCommands, 0, pos, NormalTimeout);
                words -= batch;
            }

            // Keep the batch separated from whatever command follows (usually the flash
            // controller's commit), same rationale as the per-write flush. Once per call, not per
            // batch: between batches the stream stays homogeneous W# commands, no different from
            // the command boundaries inside one.
            FlushDelay();
        }

        /// <summary>Jumps to an address (<c>G#</c> command).</summary>
        public void Go(uint address)
        {
            WriteCommand(Syntax.Go, Hex(address));
            FlushDelay();
        }

        /// <summary>
        /// Extended chip-erase (<c>X#</c>); only valid when
        /// <see cref="SambaCapabilities.HasChipEraseCommand"/>. Parts without it are erased through
        /// the flash controller instead, which is the flash layer's decision, not this method's.
        /// </summary>
        /// <remarks>
        /// Why the extension exists at all: the EFC and EEFC controllers carry a whole-chip erase in
        /// hardware, so a host erases a part with one register write and a poll. The NVMCTRL
        /// controllers on the SAMD and SAME5x families have no such command — their smallest erase is
        /// one row or block — so erasing from the host means a loop of unit erases, several USB
        /// round-trips apiece, running to roughly a thousand iterations on a part with 256-byte rows.
        /// Moving that loop inside the bootloader collapses it into one command, which is the whole
        /// point; the ROM monitor never needed it because the ROM-monitor families have the hardware
        /// command.
        /// <para>
        /// <paramref name="startAddress"/> is what lets such a bootloader survive the erase: the
        /// command clears from there to the end of flash, so an image placed past the bootloader can
        /// be written without erasing it. Nothing here guards a start address of 0 on a part whose
        /// monitor lives in flash — that asks the bootloader to erase its own code, and the caller's
        /// offset is the only thing standing in the way.
        /// </para>
        /// </remarks>
        public void ChipErase(uint startAddress)
        {
            if (!Capabilities.HasChipEraseCommand)
                throw new SamBaTransportException(DevicePath, nameof(ChipErase), "Monitor does not advertise the X# extension.");

            WriteCommand(Syntax.ChipErase, Hex(startAddress));
            ExpectReply(Syntax.ChipErase, ChipEraseReplyLength, ChipEraseTimeout, nameof(ChipErase));
        }

        /// <summary>
        /// Closes and disposes the transport. The monitor is its only owner — it is handed one at
        /// construction and no layer above keeps a reference — so nothing else would dispose it.
        /// </summary>
        public void Dispose()
        {
            _connected = false;
            _transport.Dispose();
        }

        /// <summary>
        /// Handshakes, retrying once on a freshly reopened port. Some bootloaders (e.g. SAM7SE512
        /// rev B v2.00) do not answer until the port has been reopened after first contact.
        /// <para>
        /// It is also the recovery route for a port carrying a previous session's data, which
        /// <see cref="ReadVersion"/> turns into the exception caught here: closing the port is what
        /// stops the driver pulling the remainder of an interrupted transfer, so the second attempt
        /// starts from a genuinely fresh pipe rather than from wherever the first one gave up.
        /// </para>
        /// </summary>
        private void HandshakeWithRetry()
        {
            try
            {
                Handshake();
                return;
            }
            catch (SamBaTransportException)
            {
                Report("Retrying handshake on a freshly reopened port", ProgressStage.Connecting);
                _transport.Close();
                _transport.Open();
                Handshake();
            }
        }

        private void Handshake()
        {
            ClearStalePipe();

            // Switch to binary (non-terminal) mode, then throw away whatever it answers — a
            // terminal-mode monitor echoes LF/CR, one already in binary mode says nothing at all.
            // Discarded rather than read, because reading a reply that may never come costs a full
            // timeout on every reconnect; and the clear doubles as the separation the next command
            // needs, since the monitor misparses two commands that share one USB packet.
            Report("Entering binary mode", ProgressStage.Connecting);
            WriteCommand(Syntax.BinaryMode);
            ClearStalePipe();

            ReadVersion();
            Report($"Reading monitor version ({Version})", ProgressStage.Connecting);
        }

        /// <summary>
        /// Clears the pipe of bytes nobody is going to read: purge, wait
        /// <see cref="PipeSettleDelayMs"/> for bytes that were already on the wire, purge again. Used
        /// twice by <see cref="Handshake"/> — once for whatever a half-finished previous session left
        /// behind, once for the <c>N#</c> mode-switch echo.
        /// <para>
        /// Two purges each time, because <see cref="ISambaTransport.Purge"/> only discards what has
        /// already reached the driver: the byte in question is in flight, one purge earlier than it
        /// needs to be, and would otherwise be waiting where the next reply is about to be looked for.
        /// </para>
        /// <para>
        /// Safe to call straight after writing a command, though the purge covers the transmit
        /// direction too: the transport's write is awaited to completion, and on USB CDC a completed
        /// write means the bulk transfer was acknowledged by the device, so nothing of ours is left
        /// queued for the purge to abort.
        /// </para>
        /// <para>
        /// No reads, deliberately, and that is the whole point of the method. Clearing by reading until
        /// a read timed out is what this replaced, and it cost every connect a full
        /// <see cref="QuickTimeout"/> plus the cancellation and timeout exceptions the serial layer
        /// raises to implement one — on every port, including the quiet ones, which is nearly all of
        /// them. The <c>N#</c> echo made it worse rather than better: a monitor already in binary mode
        /// sends no echo, so a read for it timed out on every reconnect.
        /// </para>
        /// <para>
        /// Nor did reading settle the case it was written for. A device still streaming an interrupted
        /// block read resumes as soon as the new session posts reads, has up to a whole 256 KB chunk
        /// left to send, and so outlasts any budget short enough to sit in front of a connect. That
        /// case is caught where it shows instead — see <see cref="ReadVersion"/>.
        /// </para>
        /// </summary>
        private void ClearStalePipe()
        {
            Report("Purging buffered data", ProgressStage.Connecting);
            _transport.Purge();
            Thread.Sleep(PipeSettleDelayMs);
            _transport.Purge();
        }

        private void ReadVersion()
        {
            WriteCommand(Syntax.Version);

            // Accumulated rather than taken from one read, because one read returns one USB packet:
            // a reply split across two would be truncated at the packet boundary, and the
            // [Arduino:…] banner sits at the end of the string — losing it reports a bootloader as a
            // plain ROM monitor, dropping the read cap it needs and corrupting every later block
            // read. Stop as soon as the CR/LF closing the string arrives, so a reply that does fit
            // one packet costs no extra wait; a monitor that sends no terminator pays one
            // QuickTimeout on the empty read that follows.
            var reply = new byte[MaxVersionReplyLength];
            int size = 0;
            while (size < reply.Length)
            {
                int read = _transport.Read(reply, size, reply.Length - size, QuickTimeout);
                if (read <= 0)
                    break;

                size += read;

                // A run that still reaches the end of what has arrived is unfinished, and no run at
                // all means only the terminator of an earlier reply has turned up so far — keep
                // reading in both cases.
                FindPrintableRun(reply, size, out int runStart, out int runLength);
                if (runLength > 0 && runStart + runLength < size)
                    break;
            }

            if (size <= 0)
                throw new SamBaTransportException(DevicePath, "Version", "No reply to V# — not a SAM-BA monitor?");

            // Keep the printable run only ("v1.1 Dec 15 2010 19:25:04" etc).
            FindPrintableRun(reply, size, out int start, out int length);
            string version = Encoding.ASCII.GetString(reply, start, length);
            if (!IsPlausibleVersion(version))
                throw new SamBaTransportException(
                    DevicePath, "Version",
                    $"V# answered {size} bytes with no version string in them (\"{version}\"). " +
                    "The port may still be carrying data from an interrupted session.");

            Version = version;
            Capabilities = ParseArduinoExtensions(version);
        }

        /// <summary>
        /// Whether a <c>V#</c> reply is a version string rather than data left over from an
        /// interrupted session: at least <see cref="MinVersionLength"/> printable characters, one of
        /// them a digit. Every reply this library has seen qualifies — the plain monitors print
        /// <c>v1.1 Dec 15 2010 19:25:04</c> and the bootloaders <c>v2.0 [Arduino:XYZ] ...</c>.
        /// <para>
        /// The point is not to identify the monitor; it is to make a contaminated pipe fail. Without
        /// this, leftover data passes as a version: the accumulate loop stops at whatever non-printable
        /// byte closes its first run, <c>size</c> is non-zero so nothing throws, and the connect
        /// succeeds carrying that run — or nothing at all — as the version, with baseline
        /// <see cref="Capabilities"/> behind it. Which drops the read cap
        /// an Arduino bootloader needs, corrupting every later block read, while the remainder of the
        /// leftover data misaligns each reply after it and the chip probe reads a garbage id. Failing
        /// instead hands the case to <see cref="HandshakeWithRetry"/>, and past that to the caller,
        /// for whom repeating a connect is cheap.
        /// </para>
        /// <para>
        /// A heuristic, and knowingly not airtight: a dump that happens to carry a long printable run
        /// with a digit in it passes. Tightening it further would mean betting on the format of
        /// firmware version strings nobody here has read — and the one airtight test, that the pipe is
        /// quiet after the reply, costs exactly the timed-out read <see cref="ClearStalePipe"/> was
        /// changed to stop paying.
        /// </para>
        /// </summary>
        private static bool IsPlausibleVersion(string version)
        {
            if (version.Length < MinVersionLength)
                return false;

            foreach (char character in version)
            {
                if (character >= '0' && character <= '9')
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Locates the printable-ASCII run in a <c>V#</c> reply — the version string itself, between
        /// anything that preceded it and the CR/LF the monitor closes it with. A run that ends before
        /// <paramref name="size"/> is a complete one: its terminator has arrived.
        /// <para>
        /// It is a run rather than a prefix because the reply need not start at the first byte. The
        /// <c>N#</c> mode-switch echo is cleared without being read, and a monitor slower to emit it
        /// than <see cref="PipeSettleDelayMs"/> leaves its LF/CR sitting in front of this reply. Taking
        /// a prefix would score that as a zero-length version and fail the connect — permanently, since
        /// the retry would hit the same latency — so the terminator of an earlier reply is stepped over
        /// instead. Only leading non-printables are skipped: a monitor that echoed something printable
        /// would still have it land at the head of the version, which is what the purge is for.
        /// </para>
        /// </summary>
        /// <param name="reply">Buffer holding the reply so far.</param>
        /// <param name="size">Bytes of <paramref name="reply"/> received.</param>
        /// <param name="start">Index of the first printable byte, or <paramref name="size"/> if
        /// there is none.</param>
        /// <param name="length">Length of the run, zero when nothing printable has arrived.</param>
        private static void FindPrintableRun(byte[] reply, int size, out int start, out int length)
        {
            start = 0;
            while (start < size && !IsPrintable(reply[start]))
                start++;

            int end = start;
            while (end < size && IsPrintable(reply[end]))
                end++;

            length = end - start;
        }

        /// <summary>Whether a reply byte belongs to a version string rather than terminating one.</summary>
        private static bool IsPrintable(byte value) =>
            value >= Syntax.FirstPrintableAscii && value <= Syntax.LastPrintableAscii;

        /// <summary>
        /// Reads the <c>[Arduino:…]</c> extension banner out of a <c>V#</c> version string. Only
        /// Arduino-family bootloaders emit one — a stock ROM monitor's version string has no such
        /// field, so anything without the banner is reported as
        /// <see cref="SambaCapabilities.RomMonitor"/>.
        /// </summary>
        internal static SambaCapabilities ParseArduinoExtensions(string version)
        {
            int start = version.IndexOf(Syntax.ArduinoExtensionsStart, StringComparison.Ordinal);
            if (start < 0)
                return SambaCapabilities.RomMonitor;

            int flagsStart = start + Syntax.ArduinoExtensionsStart.Length;
            int end = version.IndexOf(Syntax.ArduinoExtensionsEnd, flagsStart);
            if (end < 0)
                return SambaCapabilities.RomMonitor;

            // The 'Y' (SRAM-to-flash write-buffer) and 'Z' (CRC16 checksum-buffer) extensions are
            // intentionally not tracked — this library programs the flash latch directly with
            // batched W# writes and verifies by reading the region back and comparing.
            bool chipErase = false;
            for (int i = flagsStart; i < end; i++)
            {
                switch (version[i])
                {
                    case Syntax.ChipErase: chipErase = true; break;
                }
            }

            // The read cap follows from the banner being there at all, not from any flag in it:
            // these bootloaders corrupt USB reads of 64+ bytes whatever they advertise.
            return SambaCapabilities.ArduinoBootloader(chipErase);
        }

        /// <summary>
        /// Sends one command: <paramref name="letter"/>, then <paramref name="arguments"/> joined by
        /// the argument separator, then the terminator — so the punctuation is spelled out here and
        /// nowhere else. Command writes all use <see cref="NormalTimeout"/>; reply budgets differ per
        /// command and are passed at the read.
        /// </summary>
        private void WriteCommand(char letter, params string[] arguments)
        {
            string command = letter
                + string.Join(Syntax.ArgumentSeparator.ToString(), arguments)
                + Syntax.Terminator;

            byte[] bytes = Encoding.ASCII.GetBytes(command);
            _transport.Write(bytes, 0, bytes.Length, NormalTimeout);
        }

        /// <summary>Formats a 32-bit command argument.</summary>
        private static string Hex(uint value) => value.ToString("X8");

        /// <summary>Formats a byte command argument.</summary>
        private static string Hex(byte value) => value.ToString("X2");

        private void ExpectReply(char expected, int length, TimeSpan timeout, string operation)
        {
            var reply = new byte[length];
            _transport.ReadExact(reply, 0, length, timeout);
            if (reply[0] != (byte)expected)
                throw new SamBaTransportException(
                    DevicePath, operation, $"Expected '{expected}' reply, got 0x{reply[0]:X2}.");
        }

        /// <summary>
        /// Separates a command from whatever follows it on the wire, defeating USB write combining.
        /// Nominally 1 ms.
        /// <para>
        /// Nominally, because <see cref="Thread.Sleep(int)"/> does not sleep 1 ms — it sleeps to the
        /// next system timer tick, so the real figure is 1 to 15.6 ms depending on a timer resolution
        /// this library does not set and should not raise process-wide. That is unremarkable at one
        /// call per write block, and the reason <see cref="SafeMode"/> is expensive out of all
        /// proportion at one call per word: a 256-byte block costs 64 sleeps (64 ms to 1 s), an 8 KB
        /// NVMCTRL block costs 2048 (2 s to 32 s), and a 1 MB image on such a part spends between four
        /// minutes and an hour here and nowhere else.
        /// </para>
        /// <para>
        /// Left as it is deliberately. The delay is a workaround for a firmware quirk, and it may be
        /// working precisely because a tick is far longer than the 1 ms asked for — spinning to a real
        /// 1 ms deadline would cut the cost by an order of magnitude and cannot be shown to preserve
        /// the behaviour without a part that exhibits the quirk.
        /// </para>
        /// </summary>
        private static void FlushDelay()
        {
            Thread.Sleep(1);
        }
    }
}

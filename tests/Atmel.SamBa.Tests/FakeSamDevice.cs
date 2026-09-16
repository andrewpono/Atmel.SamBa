using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System.Globalization;
using System.Text;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// Minimal SAM-BA device simulator: parses monitor commands (including batched W# blobs
    /// and S#/R# binary streams) against a sparse word-addressed memory. Register behavior
    /// is data-driven — tests preload status registers (FSR/INTFLAG) with ready values and
    /// inspect <see cref="WordWrites"/> for command sequences.
    /// </summary>
    internal sealed class FakeSamDevice : ISambaTransport
    {
        private readonly Dictionary<uint, uint> _memory = new();
        private readonly Dictionary<uint, Queue<uint>> _wordReadQueues = new();
        private readonly Queue<byte> _replies = new();
        private uint _pendingWriteAddress;
        private int _pendingWriteRemaining;

        public string Version { get; set; } = "v1.1 Dec 15 2010 19:25:04";

        /// <summary>Every W#/O# register/memory write in order (address, value).</summary>
        public List<(uint Address, uint Value)> WordWrites { get; } = new();

        /// <summary>Byte count of every transport Write call (to assert batching).</summary>
        public List<int> WriteSizes { get; } = new();

        /// <summary>Every R# binary read command in order (address, size).</summary>
        public List<(uint Address, int Size)> ReadCommands { get; } = new();

        /// <summary>Start address of every X# chip-erase command in order.</summary>
        public List<uint> ChipEraseCommands { get; } = new();

        public string DevicePath => @"\\?\FAKE#VID_03EB&PID_6124";

        public bool IsOpen { get; private set; }

        public Action<string>? StatusReporter { get; set; }

        public void SetWord(uint address, uint value) => _memory[address & ~3u] = value;

        /// <summary>
        /// Scripts successive <c>w#</c> reads of one address to return successive values — the
        /// hardware pattern <see cref="_memory"/>'s single-valued words cannot express, and the way
        /// EEFC result registers actually behave: each read of FRR yields the next descriptor or
        /// lock-bit word. Reads beyond the scripted values fall back to whatever
        /// <see cref="SetWord"/> put there (default 0), matching a drained result register.
        /// </summary>
        public void EnqueueWordRead(uint address, params uint[] values)
        {
            uint key = address & ~3u;
            if (!_wordReadQueues.TryGetValue(key, out Queue<uint>? queue))
                _wordReadQueues[key] = queue = new Queue<uint>();
            foreach (uint value in values)
                queue.Enqueue(value);
        }

        public uint GetWord(uint address) => _memory.TryGetValue(address & ~3u, out uint value) ? value : 0;

        public byte GetByte(uint address) => (byte)(GetWord(address) >> (int)((address & 3) * 8));

        public void SetByte(uint address, byte value)
        {
            int shift = (int)((address & 3) * 8);
            uint word = GetWord(address);
            word = (word & ~(0xFFu << shift)) | ((uint)value << shift);
            SetWord(address, word);
        }

        public void SetBytes(uint address, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
                SetByte(address + (uint)i, data[i]);
        }

        public byte[] GetBytes(uint address, int count)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++)
                data[i] = GetByte(address + (uint)i);
            return data;
        }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            WriteSizes.Add(count);
            int pos = offset;
            int end = offset + count;

            // Binary payload of a pending S# stream?
            while (_pendingWriteRemaining > 0 && pos < end)
            {
                SetByte(_pendingWriteAddress++, buffer[pos++]);
                _pendingWriteRemaining--;
            }

            // Parse '#'-terminated ASCII commands (a batch blob contains many).
            int start = pos;
            for (int i = pos; i < end; i++)
            {
                if (buffer[i] != (byte)'#')
                    continue;
                Execute(Encoding.ASCII.GetString(buffer, start, i - start));
                start = i + 1;
            }

            if (start != end && _pendingWriteRemaining == 0)
                throw new InvalidOperationException("FakeSamDevice: incomplete command in write.");
        }

        public void ReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            if (_replies.Count < count)
                throw new SamBaTransportException(DevicePath, "ReadExact", $"Expected {count} bytes, fake has {_replies.Count}.");
            for (int i = 0; i < count; i++)
                buffer[offset + i] = _replies.Dequeue();
        }

        public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            int available = Math.Min(count, _replies.Count);
            for (int i = 0; i < available; i++)
                buffer[offset + i] = _replies.Dequeue();
            return available;
        }

        public void Purge() => _replies.Clear();

        public void Dispose() => Close();

        private void Execute(string command)
        {
            if (command.Length == 0)
                return;

            char op = command[0];
            string args = command.Substring(1);

            switch (op)
            {
                case 'N':
                    Reply("\n\r");
                    break;

                case 'V':
                    Reply(Version + "\n\r");
                    break;

                case 'W':
                {
                    (uint address, uint value) = ParsePair(args);
                    _memory[address & ~3u] = value;
                    WordWrites.Add((address, value));
                    break;
                }

                case 'w':
                {
                    uint address = ParseAddress(args);
                    uint value = _wordReadQueues.TryGetValue(address & ~3u, out Queue<uint>? queue)
                        && queue.Count > 0
                        ? queue.Dequeue()
                        : GetWord(address);
                    Reply((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
                    break;
                }

                case 'O':
                {
                    (uint address, uint value) = ParsePair(args);
                    SetByte(address, (byte)value);
                    WordWrites.Add((address, value));
                    break;
                }

                case 'o':
                    Reply(GetByte(ParseAddress(args)));
                    break;

                case 'R':
                {
                    (uint address, uint size) = ParsePair(args);
                    ReadCommands.Add((address, (int)size));
                    Reply(GetBytes(address, (int)size));
                    break;
                }

                case 'S':
                {
                    (uint address, uint size) = ParsePair(args);
                    _pendingWriteAddress = address;
                    _pendingWriteRemaining = (int)size;
                    break;
                }

                case 'X':
                    // Bootloader chip-erase extension: echo the command letter as the real one does.
                    // The erase itself is not simulated; tests assert the command was issued.
                    ChipEraseCommands.Add(ParseAddress(args));
                    Reply("X\n\r");
                    break;

                case 'G':
                    break;  // jump — nothing to simulate

                default:
                    throw new InvalidOperationException($"FakeSamDevice: unsupported command '{command}'.");
            }
        }

        private static uint ParseAddress(string args)
        {
            int comma = args.IndexOf(',');
            string hex = comma >= 0 ? args.Substring(0, comma) : args;
            return uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static (uint, uint) ParsePair(string args)
        {
            int comma = args.IndexOf(',');
            uint first = uint.Parse(args.Substring(0, comma), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            uint second = uint.Parse(args.Substring(comma + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (first, second);
        }

        private void Reply(string ascii) => Reply(Encoding.ASCII.GetBytes(ascii));

        private void Reply(params byte[] data)
        {
            foreach (byte b in data)
                _replies.Enqueue(b);
        }
    }
}

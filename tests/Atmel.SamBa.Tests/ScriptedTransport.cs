using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System.Text;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// Test transport that records every write call verbatim and serves reads from a byte
    /// stream fed by registered command→reply pairs: when a write matches a registered
    /// command, its reply is queued for subsequent reads (request/response, like the real
    /// monitor — data never exists in the pipe before the command that solicits it).
    /// Timeouts are simulated by an exhausted read queue.
    /// </summary>
    internal sealed class ScriptedTransport : ISambaTransport
    {
        private readonly Queue<byte> _readData = new();
        private readonly Dictionary<string, Queue<byte[]>> _responses = new();

        /// <summary>Every Write call payload, one entry per call.</summary>
        public List<byte[]> Writes { get; } = new();

        /// <summary>All written bytes decoded as ASCII, call boundaries marked with '|'.</summary>
        public string WrittenText => string.Join("|", Writes.Select(w => Encoding.ASCII.GetString(w)));

        public string DevicePath => @"\\?\TEST#VID_03EB&PID_6124";

        public bool IsOpen { get; private set; }

        public Action<string>? StatusReporter { get; set; }

        public int OpenCount { get; private set; }

        public int PurgeCount { get; private set; }

        /// <summary>Dispose calls, one per invocation — pins that the owner disposes exactly once.</summary>
        public int DisposeCount { get; private set; }

        /// <summary>
        /// Reads of either kind. Lets a test pin that a caller reads only in answer to a command it
        /// wrote — a speculative read costs a full timeout on hardware and shows up as nothing here.
        /// </summary>
        public int ReadCount { get; private set; }

        /// <summary>Invoked on every Open — lets a test re-arm replies for reconnect scenarios.</summary>
        public Action<ScriptedTransport>? OnOpen { get; set; }

        /// <summary>
        /// Caps how many bytes one <see cref="Read"/> hands back, modelling the driver returning a
        /// single USB packet rather than everything the caller asked for. Zero (the default) hands
        /// back every queued byte, which is what the request/response tests want; a small value is
        /// what a caller that must survive a split reply has to be tested against.
        /// </summary>
        public int MaxBytesPerRead { get; set; }

        /// <summary>
        /// Budget passed to the most recent read of either kind — lets a test pin which timeout a
        /// command waits its reply out with, since the reads here always succeed immediately.
        /// </summary>
        public TimeSpan LastReadTimeout { get; private set; }

        /// <summary>Registers the reply to queue when <paramref name="command"/> is written (FIFO per command).</summary>
        public void AddResponse(string command, string asciiReply) =>
            AddResponse(command, Encoding.ASCII.GetBytes(asciiReply));

        /// <summary>Registers the reply to queue when <paramref name="command"/> is written (FIFO per command).</summary>
        public void AddResponse(string command, params byte[] reply)
        {
            if (!_responses.TryGetValue(command, out Queue<byte[]>? queue))
            {
                queue = new Queue<byte[]>();
                _responses[command] = queue;
            }
            queue.Enqueue(reply);
        }

        /// <summary>Queues raw bytes for reading immediately (bypasses command matching).</summary>
        public void EnqueueReply(params byte[] data)
        {
            foreach (byte b in data)
                _readData.Enqueue(b);
        }

        public void Open()
        {
            IsOpen = true;
            OpenCount++;
            OnOpen?.Invoke(this);
        }

        public void Close() => IsOpen = false;

        public void Write(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            Writes.Add(copy);

            string text = Encoding.ASCII.GetString(copy);
            if (_responses.TryGetValue(text, out Queue<byte[]>? queue) && queue.Count > 0)
                EnqueueReply(queue.Dequeue());
        }

        public void ReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            LastReadTimeout = timeout;
            ReadCount++;
            if (_readData.Count < count)
                throw new SamBaTransportException(DevicePath, "ReadExact", $"Expected {count} bytes, scripted {_readData.Count}.");
            for (int i = 0; i < count; i++)
                buffer[offset + i] = _readData.Dequeue();
        }

        public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            LastReadTimeout = timeout;
            ReadCount++;
            int available = Math.Min(count, _readData.Count);
            if (MaxBytesPerRead > 0)
                available = Math.Min(available, MaxBytesPerRead);
            for (int i = 0; i < available; i++)
                buffer[offset + i] = _readData.Dequeue();
            return available;
        }

        public void Purge()
        {
            PurgeCount++;
            _readData.Clear();
        }

        public void Dispose()
        {
            DisposeCount++;
            Close();
        }
    }
}

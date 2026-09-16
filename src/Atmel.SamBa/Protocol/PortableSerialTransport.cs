using Anp.Atmel.SamBa.Exceptions;
using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;


namespace Anp.Atmel.SamBa.Protocol
{
    /// <summary>
    /// <see cref="ISambaTransport"/> implementation over <see cref="SerialPort"/> for the portable
    /// targets, where the Windows serial layer is not referenced. Works on Windows, Linux and macOS.
    /// Translates serial-layer failures into <see cref="SamBaTransportException"/>.
    /// <para>
    /// Two behavioral notes against the Windows transport. Opening needs permission on the tty on
    /// Linux (the dialout group on most distributions); a refusal surfaces as
    /// <see cref="UnauthorizedAccessException"/>, which the failure predicate already covers. And
    /// closing the port from another thread while a read is blocked is not a supported
    /// <see cref="SerialPort"/> pattern — instead of the Windows layer's immediate abort, the close
    /// waits out the current read's own timeout. Every read the monitor issues is bounded (5 s at
    /// most), so the cost is a short stall, not a hang.
    /// </para>
    /// </summary>
    internal sealed class PortableSerialTransport : ISambaTransport
    {
        /// <summary>
        /// <see cref="SerialPort"/> demands a baud rate even though a USB CDC device never acts on
        /// one — the CDC line-coding request carries whatever number is set and the SAM-BA firmware
        /// ignores it. 115200 is the conventional SAM-BA monitor rate, so it is also the right value
        /// if the path ever names a real UART.
        /// </summary>
        private const int CdcBaudRate = 115200;

        private readonly string _devicePath;
        private SerialPort _port;

        internal PortableSerialTransport(string devicePath)
        {
            _devicePath = devicePath ?? throw new ArgumentNullException(nameof(devicePath));
        }

        public string DevicePath => _devicePath;

        public bool IsOpen => _port != null && _port.IsOpen;

        public Action<string> StatusReporter { get; set; }

        public void Open()
        {
            if (IsOpen)
                return;

            SerialPort port = null;
            try
            {
                // Always start from a fresh port object; a half-torn-down port left over from a
                // surprise removal must not be reused (Samba Lite lesson).
                _port?.Dispose();
                _port = null;

                // Parity, stop bits and data bits stay at the 8-N-1 defaults — like the baud rate,
                // they are CDC line-coding fields the device ignores. DTR is set before Open: the
                // port object hands its cached property values to the stream as part of opening, so
                // the line state is deterministic from the first byte. SAM-BA on the native USB CDC
                // port expects DTR asserted.
                port = new SerialPort(_devicePath, CdcBaudRate)
                {
                    Handshake = Handshake.None,
                    DtrEnable = true,
                };
                StatusReporter?.Invoke("Opening port");
                port.Open();
                _port = port;
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex) || ex is ArgumentException)
            {
                // The ArgumentException arm is Open-only and deliberate: SerialPort reports a
                // malformed or foreign-OS port name ("/dev/ttyACM0" on Windows) that way, and at
                // this seam "the path names no openable device" is a transport failure.
                port?.Dispose();
                throw new SamBaTransportException(_devicePath, nameof(Open), ex.Message, ex);
            }
        }

        public void Close()
        {
            if (_port == null)
                return;

            try
            {
                StatusReporter?.Invoke("Closing port");
                _port.Dispose();
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                // Best effort; the device may already be gone.
            }
            finally
            {
                _port = null;
            }
        }

        public void Write(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            SerialPort port = GetOpenPort(nameof(Write));
            try
            {
                // Set per call, never restored: nothing else reads the port's timeout properties.
                // SerialPort.Write completes whole or throws TimeoutException, which the predicate
                // covers, so this satisfies "exactly count bytes or throws".
                port.WriteTimeout = ToPortTimeout(timeout);
                port.Write(buffer, offset, count);
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                throw new SamBaTransportException(_devicePath, nameof(Write), ex.Message, ex);
            }
        }

        public void ReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            SerialPort port = GetOpenPort(nameof(ReadExact));
            try
            {
                // SerialPort.Read returns as soon as any byte is available, not when count bytes
                // are, so accumulate against a deadline; the passed timeout is the budget for the
                // whole read, not per chunk.
                Stopwatch clock = Stopwatch.StartNew();
                int total = 0;
                while (total < count)
                {
                    TimeSpan remaining = timeout - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                        throw new TimeoutException($"Read of {count} bytes timed out after {timeout}.");

                    port.ReadTimeout = ToPortTimeout(remaining);
                    total += port.Read(buffer, offset + total, count - total);
                }
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                throw new SamBaTransportException(
                    _devicePath, nameof(ReadExact), $"Expected {count} bytes. {ex.Message}", ex);
            }
        }

        public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            SerialPort port = GetOpenPort(nameof(Read));
            try
            {
                // On both the Windows and the termios implementations a single Read waits up to
                // ReadTimeout for the first byte and then returns what is immediately available,
                // which is exactly this method's contract.
                port.ReadTimeout = ToPortTimeout(timeout);
                return port.Read(buffer, offset, count);
            }
            catch (TimeoutException)
            {
                return 0;
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                throw new SamBaTransportException(_devicePath, nameof(Read), ex.Message, ex);
            }
        }

        public void Purge()
        {
            SerialPort port = GetOpenPort(nameof(Purge));
            try
            {
                // DiscardInBuffer also resets SerialPort's internal read cache, so no read-ahead
                // byte survives this. Bytes still in flight from the device are untouched
                // (PurgeComm and tcflush alike), which is why the monitor's pipe-clearing purges
                // twice around a settle delay.
                StatusReporter?.Invoke("Purging buffered data");
                port.DiscardOutBuffer();
                port.DiscardInBuffer();
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                throw new SamBaTransportException(_devicePath, nameof(Purge), ex.Message, ex);
            }
        }

        public void Dispose()
        {
            Close();
        }

        /// <summary>
        /// Converts a per-call budget to the millisecond form <see cref="SerialPort"/>'s timeout
        /// properties take. Ceiling rather than truncation so a sub-millisecond budget does not
        /// collapse to 0, and a floor of 1 because those properties reject 0 and every negative
        /// except <see cref="SerialPort.InfiniteTimeout"/>. Internal so the arithmetic is testable.
        /// </summary>
        internal static int ToPortTimeout(TimeSpan timeout)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                return SerialPort.InfiniteTimeout;

            double ms = Math.Ceiling(timeout.TotalMilliseconds);
            if (ms < 1)
                return 1;
            return ms > int.MaxValue ? int.MaxValue : (int)ms;
        }

        private SerialPort GetOpenPort(string operation)
        {
            SerialPort port = _port;
            if (port == null || !port.IsOpen)
                throw new SamBaTransportException(_devicePath, operation, "Port is not open.");
            return port;
        }
    }
}

using Anp.Atmel.SamBa.Exceptions;
using Anp.Serial.Win32;
using System;


namespace Anp.Atmel.SamBa.Protocol
{
    /// <summary>
    /// <see cref="ISambaTransport"/> implementation over <see cref="Anp.Serial.Win32.SerialDevice"/>.
    /// Translates serial-layer failures into <see cref="SamBaTransportException"/>.
    /// </summary>
    internal sealed class SerialPortTransport : ISambaTransport
    {
        private readonly string _devicePath;
        private SerialDevice _device;

        internal SerialPortTransport(string devicePath)
        {
            _devicePath = devicePath ?? throw new ArgumentNullException(nameof(devicePath));
        }

        public string DevicePath => _devicePath;

        public bool IsOpen => _device != null && _device.IsOpen;

        public Action<string> StatusReporter { get; set; }

        public void Open()
        {
            if (IsOpen)
                return;

            try
            {
                // Always start from a fresh device object; a half-torn-down port left over from a
                // surprise removal must not be reused (Samba Lite lesson).
                _device?.Dispose();
                _device = new SerialDevice(_devicePath);
                StatusReporter?.Invoke("Opening port");
                _device.Open();

                // No baud rate, parity, stop bits or flow control is applied, and none is missing:
                // this is a USB CDC port, where usbser.sys ignores the line parameters altogether —
                // the device sees the same bulk endpoints whatever the DCB says.

                // Assert DTR on every open (EscapeCommFunction SETDTR). SAM-BA on the native
                // USB CDC port expects DTR asserted; doing it explicitly here makes the line
                // state deterministic regardless of what a previous app left in the port's DCB
                // (a stale DTR line otherwise leaves the monitor unresponsive on some adapters).
                // Neither call above carries a timeout — it is a single blocking Win32 IOCTL — so
                // the reports around them are the only warning a caller gets before one stalls.
                StatusReporter?.Invoke("Setting DTR");
                _device.DtrEnable = true;
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                _device?.Dispose();
                _device = null;
                throw new SamBaTransportException(_devicePath, nameof(Open), ex.Message, ex);
            }
        }

        public void Close()
        {
            if (_device == null)
                return;

            try
            {
                // Dispose closes the handle, itself a single blocking call with no timeout:
                // usbser.sys drains any IRP it still has outstanding before it returns.
                StatusReporter?.Invoke("Closing port");
                _device.Dispose();
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                // Best effort; the device may already be gone.
            }
            finally
            {
                _device = null;
            }
        }

        public void Write(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            SerialDevice device = GetOpenDevice(nameof(Write));
            try
            {
                device.Write(buffer, offset, count, timeout);
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                throw new SamBaTransportException(_devicePath, nameof(Write), ex.Message, ex);
            }
        }

        public void ReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            SerialDevice device = GetOpenDevice(nameof(ReadExact));
            try
            {
                byte[] data = device.ReadExact(count, timeout);
#if DEBUG
                // Traces the read completing, not a short one: the call above returns exactly count
                // bytes or throws, so the two numbers always agree. Fully qualified so no using has
                // to be conditioned on DEBUG alongside it.
                //System.Diagnostics.Debug.WriteLine($"ReadExact: Expected {count} bytes. Got {data.Length}");
#endif
                Buffer.BlockCopy(data, 0, buffer, offset, count);
            }
            catch (Exception ex) when (TransportFailure.IsFailure(ex))
            {
                throw new SamBaTransportException(
                    _devicePath, nameof(ReadExact), $"Expected {count} bytes. {ex.Message}", ex);
            }
        }

        public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            SerialDevice device = GetOpenDevice(nameof(Read));
            try
            {
                return device.Read(buffer, offset, count, timeout);
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
            SerialDevice device = GetOpenDevice(nameof(Purge));
            try
            {
                device.Purge();
                device.ClearReadAheadBuffer();
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

        private SerialDevice GetOpenDevice(string operation)
        {
            SerialDevice device = _device;
            if (device == null || !device.IsOpen)
                throw new SamBaTransportException(_devicePath, operation, "Port is not open.");
            return device;
        }
    }
}

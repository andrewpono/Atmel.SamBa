using System;


namespace Anp.Atmel.SamBa.Protocol
{
    /// <summary>
    /// Byte-level transport under the SAM-BA monitor protocol. The library provides serial
    /// implementations internally, selected by the <see cref="SamBaDevice(string)"/> constructor;
    /// implement this to drive a device over a custom link — a TCP bridge, a different serial
    /// stack — and hand it to <see cref="SamBaDevice(ISambaTransport)"/>.
    /// </summary>
    /// <remarks>
    /// Ownership: <see cref="SamBaDevice"/> takes ownership of the transport at construction and
    /// disposes it from its own <see cref="SamBaDevice.Dispose"/>. <see cref="Open"/> must work
    /// again after <see cref="Close"/> on the same instance: the device reopens the transport when
    /// recovering a lost connection.
    /// <para>
    /// Threading: single consumer — the owning device serializes every call, so an implementation
    /// need not be thread-safe. The one cross-thread interaction the library performs is
    /// <see cref="Close"/> or <see cref="IDisposable.Dispose"/> while a read is pending, which is
    /// how a caller aborts an operation that has stopped making progress. An implementation that
    /// cannot abort a pending read that way should let the read fail or time out on its own rather
    /// than hang.
    /// </para>
    /// <para>
    /// Errors: link failures surface as <see cref="Exceptions.SamBaTransportException"/> — never as
    /// the underlying stack's own exception types. <see cref="Write"/> and <see cref="ReadExact"/>
    /// throw it on timeout; <see cref="Read"/> returns 0 on timeout and never throws for one.
    /// </para>
    /// </remarks>
    public interface ISambaTransport : IDisposable
    {
        /// <summary>System path that uniquely identifies the device.</summary>
        string DevicePath { get; }

        /// <summary>True while the underlying port is open.</summary>
        bool IsOpen { get; }

        /// <summary>
        /// Sink for granular status messages an implementation may emit around a blocking
        /// control-plane call that carries no timeout of its own — opening the handle, applying
        /// line state, purging, closing. Set by the owning <see cref="Protocol.SambaMonitor"/> so a
        /// stall inside one of those calls shows up as "stuck on X" instead of a silent gap before
        /// the eventual failure. Null (the default) means nothing is listening; an implementation
        /// that has no such calls may leave every invocation of it a no-op.
        /// </summary>
        Action<string> StatusReporter { get; set; }

        /// <summary>Opens the underlying port and applies port settings.</summary>
        /// <exception cref="Exceptions.SamBaTransportException">
        /// The port could not be opened — it does not exist, is in use, or access was denied.
        /// </exception>
        void Open();

        /// <summary>Closes the underlying port. Safe to call when already closed; throws nothing.</summary>
        void Close();

        /// <summary>
        /// Writes exactly <paramref name="count"/> bytes or throws
        /// <see cref="Exceptions.SamBaTransportException"/>.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <param name="offset">Offset into <paramref name="buffer"/> of the first byte to write.</param>
        /// <param name="count">Number of bytes to write.</param>
        /// <param name="timeout">Budget for the whole write.</param>
        /// <exception cref="Exceptions.SamBaTransportException">
        /// The port is not open, the write timed out, or the link failed.
        /// </exception>
        void Write(byte[] buffer, int offset, int count, TimeSpan timeout);

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes within <paramref name="timeout"/> or throws
        /// <see cref="Exceptions.SamBaTransportException"/>.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <param name="offset">Offset into <paramref name="buffer"/> of the first byte to fill.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <param name="timeout">Budget for the whole read, not per byte.</param>
        /// <exception cref="Exceptions.SamBaTransportException">
        /// The port is not open, fewer than <paramref name="count"/> bytes arrived in time, or the
        /// link failed.
        /// </exception>
        void ReadExact(byte[] buffer, int offset, int count, TimeSpan timeout);

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes: waits up to <paramref name="timeout"/> for the first
        /// byte, then returns what is immediately available. Returns 0 on timeout (no data).
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <param name="offset">Offset into <paramref name="buffer"/> of the first byte to fill.</param>
        /// <param name="count">Maximum number of bytes to read.</param>
        /// <param name="timeout">How long to wait for the first byte.</param>
        /// <returns>The number of bytes read; 0 when nothing arrived in time.</returns>
        /// <exception cref="Exceptions.SamBaTransportException">
        /// The port is not open or the link failed. A timeout is not an error here.
        /// </exception>
        int Read(byte[] buffer, int offset, int count, TimeSpan timeout);

        /// <summary>
        /// Discards any received data that has already reached the transport (including any
        /// implementation read-ahead) and any unsent transmit data. Bytes still in flight from the
        /// device are not covered — a caller that needs those gone purges, waits, and purges again.
        /// </summary>
        /// <exception cref="Exceptions.SamBaTransportException">
        /// The port is not open or the link failed.
        /// </exception>
        void Purge();
    }
}

namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// SAM-BA operation attempted on a device that is not open. Raised by
    /// <see cref="SamBaDevice"/> before anything reaches the wire, so nothing was attempted on the
    /// device and calling <see cref="SamBaDevice.Open"/> first is the whole fix.
    /// </summary>
    public sealed class SamBaDeviceNotOpenException : SamBaException
    {
        /// <summary>
        /// Name of the operation that was attempted — the library method that was called
        /// (e.g. "ReadMemory"). Always set, and exposed rather than left in the message only, so this
        /// type carries the operation the way its three neighbours do (see
        /// <see cref="SamBaFlashCommandException.Operation"/>).
        /// </summary>
        public string Operation { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaDeviceNotOpenException"/> class.
        /// </summary>
        /// <param name="operationLabel">Name of the operation that was attempted</param>
        internal SamBaDeviceNotOpenException(string operationLabel)
            : base($"Device is not open. Call Open() before {operationLabel}.")
        {
            Operation = operationLabel ?? string.Empty;
        }
    }
}

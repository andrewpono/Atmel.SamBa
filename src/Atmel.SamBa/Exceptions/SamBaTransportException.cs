using System;


namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// Represents failures in the serial transport layer (port open, read, write, timeout, device removal).
    /// </summary>
    public sealed class SamBaTransportException : SamBaException
    {
        /// <summary>
        /// Gets the system path that uniquely identifies the device.
        /// </summary>
        public string DevicePath { get; }

        /// <summary>
        /// Gets the name of the operation associated with this instance.
        /// </summary>
        public string Operation { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaTransportException"/> class.
        /// </summary>
        /// <param name="devicePath">System path of the device</param>
        /// <param name="operation">Name of the transport operation that failed</param>
        /// <param name="message">Message string</param>
        /// <param name="innerException">Inner exception</param>
        internal SamBaTransportException(
            string devicePath,
            string operation,
            string message,
            Exception innerException = null)
            : base(FormatMessage(devicePath, operation, message), innerException)
        {
            DevicePath = devicePath ?? string.Empty;
            Operation = operation ?? string.Empty;
        }

        private static string FormatMessage(string devicePath, string operation, string message)
        {
            string path = string.IsNullOrEmpty(devicePath) ? "<unknown>" : devicePath;
            string op = string.IsNullOrEmpty(operation) ? "<operation>" : operation;
            string msg = string.IsNullOrEmpty(message) ? "Transport operation failed." : message;

            return $"Serial transport error during {op}. Path: '{path}'. {msg}";
        }
    }
}

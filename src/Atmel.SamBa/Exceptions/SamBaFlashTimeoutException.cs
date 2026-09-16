using System;


namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// The flash controller did not report ready within the polling budget: FSR FRDY on the EFC
    /// family, NVMCTRL INTFLAG.READY on the D2x generation and STATUS.READY on the D5x.
    /// </summary>
    public sealed class SamBaFlashTimeoutException : SamBaException
    {
        /// <summary>
        /// Name of the flash controller operation that was pending — usually the library method
        /// that issued the command (e.g. "WriteBlock", "EraseAll"), or a description of the specific
        /// command where that is more precise (e.g. "boot-source change"). Always set: naming the
        /// operation is required of every throw site.
        /// </summary>
        public string Operation { get; }

        /// <summary>
        /// The polling budget that elapsed. Wall-clock time actually spent waiting, not a poll count.
        /// </summary>
        public TimeSpan Timeout { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaFlashTimeoutException"/> class.
        /// </summary>
        /// <param name="operation">Name of the operation that was pending</param>
        /// <param name="timeout">The polling budget that elapsed</param>
        internal SamBaFlashTimeoutException(string operation, TimeSpan timeout)
            : base(FormatMessage(operation, timeout))
        {
            Operation = operation ?? string.Empty;
            Timeout = timeout;
        }

        private static string FormatMessage(string operation, TimeSpan timeout)
        {
            string op = string.IsNullOrEmpty(operation) ? "<operation>" : operation;
            return $"Flash controller did not become ready within {timeout.TotalSeconds:0.###} s during {op}.";
        }
    }
}

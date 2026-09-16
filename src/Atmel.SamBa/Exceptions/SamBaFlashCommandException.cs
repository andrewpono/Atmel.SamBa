namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// A flash controller command failed: the controller reported a command error, a lock error, or
    /// the requested operation is not supported by the chip family. Which one is reported by
    /// <see cref="IsCommandError"/>, <see cref="IsLockError"/> and <see cref="IsUnsupported"/>.
    /// </summary>
    public sealed class SamBaFlashCommandException : SamBaException
    {
        /// <summary>
        /// Name of the flash controller operation that failed — usually the library method that
        /// issued the command (e.g. "WriteBlock", "EraseAll"), or a description of the specific
        /// command where that is more precise (e.g. "boot-source change"). Always set: naming the
        /// operation is required of every throw site.
        /// </summary>
        public string Operation { get; }

        /// <summary>
        /// True when the controller reported a lock error (attempt to program or erase a locked region).
        /// </summary>
        public bool IsLockError { get; }

        /// <summary>
        /// True when the controller reported a command/programming error.
        /// </summary>
        public bool IsCommandError { get; }

        /// <summary>
        /// True when no command reached the controller at all, because the chip family cannot do what
        /// was asked — a fixed boot source, an auto-erasing write past the small sectors. The message
        /// names the limitation; a retry will not help.
        /// <para>
        /// Only for work the flash controller would have carried out. An operation that never reaches
        /// one and is unsupported all the same — a reset on a family with no reset route — raises
        /// <see cref="SamBaUnsupportedOperationException"/> instead.
        /// </para>
        /// </summary>
        public bool IsUnsupported { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaFlashCommandException"/> class. Callers
        /// set the one cause flag that applies.
        /// </summary>
        /// <param name="message">Message string</param>
        /// <param name="operation">Name of the operation that failed</param>
        /// <param name="isLockError">The controller reported a lock error</param>
        /// <param name="isCommandError">The controller reported a command error</param>
        /// <param name="isUnsupported">The chip family cannot do what was asked</param>
        internal SamBaFlashCommandException(
            string message,
            string operation,
            bool isLockError = false,
            bool isCommandError = false,
            bool isUnsupported = false)
            : base(FormatMessage(message, operation, isLockError, isCommandError))
        {
            Operation = operation ?? string.Empty;
            IsLockError = isLockError;
            IsCommandError = isCommandError;
            IsUnsupported = isUnsupported;
        }

        /// <summary>
        /// Builds the message from the cause flags. <c>isUnsupported</c> deliberately adds no
        /// sentence: those call sites name the part and the limitation in their own message, which
        /// beats anything generic that could be appended here.
        /// </summary>
        private static string FormatMessage(
            string message, string operation, bool isLockError, bool isCommandError)
        {
            string msg = string.IsNullOrEmpty(message) ? "Flash controller command failed." : message;

            if (!string.IsNullOrEmpty(operation))
                msg += $" Operation: {operation}.";
            if (isLockError)
                msg += " The flash region is locked.";
            if (isCommandError)
                msg += " The controller reported a command error.";

            return msg;
        }
    }
}

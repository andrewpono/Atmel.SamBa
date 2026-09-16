namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// The part cannot do what was asked, and no command was issued to find that out. A limitation of
    /// the silicon in front of you rather than a failure of anything attempted on it, so a retry can
    /// only fail the same way.
    /// <para>
    /// Distinct from <see cref="SamBaFlashCommandException"/> with
    /// <see cref="SamBaFlashCommandException.IsUnsupported"/>, which reports the same kind of
    /// limitation for something the flash controller would have carried out — a boot source with no
    /// GPNVM bit behind it, an auto-erasing write past the small sectors. This type is for an
    /// operation that never reaches a flash controller at all.
    /// </para>
    /// </summary>
    public sealed class SamBaUnsupportedOperationException : SamBaException
    {
        /// <summary>
        /// Name of the operation that cannot be performed — the library method that was called
        /// (e.g. "Reset"). Always set: naming the operation is required of every throw site, as it is
        /// on <see cref="SamBaFlashCommandException.Operation"/> and its two neighbours.
        /// </summary>
        public string Operation { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaUnsupportedOperationException"/> class.
        /// </summary>
        /// <param name="operation">Name of the operation that cannot be performed</param>
        /// <param name="reason">Why this part cannot perform it; the operation name is appended</param>
        internal SamBaUnsupportedOperationException(string operation, string reason)
            : base(FormatMessage(operation, reason))
        {
            Operation = operation ?? string.Empty;
        }

        private static string FormatMessage(string operation, string reason)
        {
            string msg = string.IsNullOrEmpty(reason) ? "The device does not support this operation." : reason;

            if (!string.IsNullOrEmpty(operation))
                msg += $" Operation: {operation}.";

            return msg;
        }
    }
}

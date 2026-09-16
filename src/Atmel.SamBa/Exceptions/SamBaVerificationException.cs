namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// Read-back verification found a byte that does not match what was written.
    /// </summary>
    /// <remarks>
    /// Reports where the first mismatch is, to help pin down a flash or timing problem.
    /// </remarks>
    public sealed class SamBaVerificationException : SamBaException
    {
        /// <summary>
        /// Index of the first mismatching byte within the verified data — not a flash offset. Add the
        /// offset the data was verified at to get one, or use <see cref="MismatchAddress"/>, which
        /// already accounts for it.
        /// </summary>
        public int MismatchOffset { get; }

        /// <summary>
        /// Absolute address of the first mismatching byte.
        /// </summary>
        public uint MismatchAddress { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaVerificationException"/> class.
        /// </summary>
        /// <param name="message">Message string</param>
        /// <param name="offset">Index of the first mismatch within the verified data</param>
        /// <param name="address">Address of the first mismatch</param>
        internal SamBaVerificationException(string message, int offset, uint address)
            : base(message)
        {
            MismatchOffset = offset;
            MismatchAddress = address;
        }
    }
}

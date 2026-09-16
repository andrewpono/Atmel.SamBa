using System;


namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// SAM-BA-specific exception base class. Every device failure derives from this, so one catch
    /// covers them all. Caller mistakes do not: those stay as the BCL exception that fits
    /// (<see cref="ArgumentOutOfRangeException"/>, <see cref="ObjectDisposedException"/>).
    /// <para>
    /// Public to be caught, but every constructor is internal: the hierarchy is closed on purpose,
    /// and nothing outside this assembly can raise or extend a SAM-BA exception.
    /// </para>
    /// </summary>
    public class SamBaException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaException"/> class.
        /// </summary>
        /// <param name="message">Message string</param>
        internal SamBaException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaException"/> class.
        /// </summary>
        /// <param name="message">Message string</param>
        /// <param name="inner">Inner exception</param>
        internal SamBaException(string message, Exception inner) : base(message, inner) { }
    }
}

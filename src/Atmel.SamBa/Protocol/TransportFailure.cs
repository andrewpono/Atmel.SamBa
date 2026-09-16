using Anp.Atmel.SamBa.Exceptions;
using System;
using System.IO;


namespace Anp.Atmel.SamBa.Protocol
{
    /// <summary>
    /// The one definition of which exceptions count as a link failure, shared by every transport
    /// implementation the library ships so that callers see a uniform
    /// <see cref="SamBaTransportException"/> boundary whichever serial stack is underneath.
    /// </summary>
    internal static class TransportFailure
    {
        /// <summary>
        /// Failures that belong to the port rather than to the caller, and so are reported as
        /// <see cref="SamBaTransportException"/>. Surprise removal is covered without naming it: the
        /// Windows serial layer's disconnection exception derives from <see cref="IOException"/>.
        /// <para>
        /// <see cref="OperationCanceledException"/> is here because the Windows serial layer raises it
        /// when the port is closed while a read is pending — which is how a caller aborts an operation
        /// that has stopped making progress, <see cref="SamBaDevice.Close"/> being public and taking no
        /// lock. No cancellation token reaches this library, so nobody is waiting to observe that
        /// exception as cancellation; letting it through would only put a bare BCL type outside the
        /// <c>SamBaException</c> hierarchy in front of a caller who closed a port and got a read
        /// error, which is what actually happened.
        /// </para>
        /// </summary>
        internal static bool IsFailure(Exception ex)
        {
            return ex is IOException
                || ex is TimeoutException
                || ex is UnauthorizedAccessException
                || ex is InvalidOperationException
                || ex is ObjectDisposedException
                || ex is OperationCanceledException;
        }
    }
}

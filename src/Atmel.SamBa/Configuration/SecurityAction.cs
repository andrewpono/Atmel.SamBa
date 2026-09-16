namespace Anp.Atmel.SamBa.Configuration
{
    /// <summary>
    /// What <see cref="SamBaUpdateOptions.SetSecurity"/> does to the security bit after everything
    /// else. An enum rather than a bool so the irreversible choice has to be spelled out at the call
    /// site rather than riding in on a default: a stray <c>true</c> is easy to miss on a
    /// <see cref="System.Boolean"/> field carried through a settings object, a cloned options
    /// literal, or a value that drifted in from somewhere else, and here there is no undoing it — only
    /// the ERASE pin can.
    /// </summary>
    public enum SecurityAction
    {
        /// <summary>Leave the security bit as it is. Default; not a request to clear it.</summary>
        Leave = 0,

        /// <summary>
        /// Set the security bit. WARNING: irreversible — the device can only be recovered with the
        /// erase pin, and further SAM-BA access is blocked.
        /// </summary>
        SetPermanently = 1,
    }
}

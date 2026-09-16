namespace Anp.Atmel.SamBa.Configuration
{
    /// <summary>
    /// Which lock regions <see cref="SamBaUpdateOptions.Lock"/> locks after programming.
    /// </summary>
    public enum FlashLockScope
    {
        /// <summary>
        /// Leave the lock state alone. Not a request to unlock — see
        /// <see cref="SamBaUpdateOptions.UnlockBeforeWrite"/> for that.
        /// </summary>
        None = 0,

        /// <summary>
        /// Lock only the regions the image occupies, leaving every other region as it is. A region
        /// whose tail the image only partly reaches is locked in full — locking is region-granular,
        /// not byte-granular.
        /// </summary>
        Written = 1,

        /// <summary>
        /// Lock every region, regardless of what the image covers. Equivalent to
        /// <see cref="SamBaDevice.SetLockRegions(bool)"/> called with <c>true</c> right after the
        /// update.
        /// </summary>
        All = 2,
    }
}

namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Which account wins when <see cref="Flash.FlashController.Create"/> finds a listed part's
    /// device-reported geometry disagreeing with its table row. Only meaningful for a matched row —
    /// a provisional record (no row matched) always adopts the device's account, since it is the
    /// only one there is.
    /// </summary>
    /// <remarks>
    /// The disagreement is recorded and reported (<see cref="SamBaDevice.GeometryMismatchDetected"/>
    /// plus a progress message) regardless of this setting — it only changes which geometry the
    /// controller runs on afterwards.
    /// </remarks>
    public enum SamBaGeometryPrecedence
    {
        /// <summary>
        /// The table row wins. The default: the descriptor read is unverified against real silicon,
        /// while the table is datasheet-sourced, so trusting it is the safe choice for every part this
        /// library already lists.
        /// </summary>
        Table = 0,

        /// <summary>
        /// The device's own account wins, provided it still passes
        /// <see cref="Flash.DeviceFlashGeometry.IsUsable"/>. Only worth choosing when the table row
        /// itself is known to be wrong for the connected part and no corrected release is available
        /// yet — trusting an unverified descriptor read is what <see cref="Table"/> exists to avoid.
        /// Has no effect — the table runs exactly as under <see cref="Table"/> — on a part whose
        /// controller has no self-report at all (a legacy EFC predating GETD) or whose probe failed
        /// outright; there is nothing to adopt either way, and a progress message says so.
        /// </summary>
        Device,
    }
}

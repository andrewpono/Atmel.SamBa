namespace Anp.Atmel.SamBa.Flash
{
    /// <summary>
    /// Requested option changes (boot bit, lock regions, security), dirty-tracked so only what was
    /// asked for is written. An update collects them and applies the whole set once at the very end,
    /// after verify; the standalone option setters build one of these with a single property set.
    /// </summary>
    /// <remarks>
    /// The order <c>FlashController.ApplyOptions</c> writes them in is fixed and matters: boot bit,
    /// then lock regions, then security. The security bit denies the controller any further command
    /// of its own, so anything applied after it would be silently dropped.
    /// </remarks>
    internal sealed class FlashOptionState
    {
        /// <summary>Requested boot source (GPNVM boot bit); null = leave unchanged.</summary>
        public SamBaChipBootSource? BootSource { get; set; }

        /// <summary>Requested lock state for the regions named below; null = leave unchanged.</summary>
        public bool? Lock { get; set; }

        /// <summary>
        /// Which regions <see cref="Lock"/> applies to; null means every region. A firmware update
        /// fills this with the regions its image occupies, so programming part of the flash does not
        /// write-protect the part it never touched; the stand-alone lock/unlock setters leave it null.
        /// Ignored when <see cref="Lock"/> has no value.
        /// </summary>
        public int[] LockRegions { get; set; }

        /// <summary>
        /// True to set the security bit. Deliberately not <c>bool?</c> like the two above: the bit can
        /// only be set, never cleared by a command — recovery is an ERASE-pin operation on the board —
        /// so there is no third state to distinguish, and false means "don't", not "clear it".
        /// </summary>
        public bool SetSecurity { get; set; }

        /// <summary>
        /// True when nothing was requested, which is what lets an update skip the option pass — and
        /// the reason the two settings above are nullable rather than defaulting to false.
        /// </summary>
        public bool IsEmpty => !BootSource.HasValue && !Lock.HasValue && !SetSecurity;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            if (BootSource.HasValue)
                sb.Append($"{nameof(BootSource)}: {BootSource.Value}");
            if (Lock.HasValue)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append($"{nameof(Lock)}: {Lock.Value}");
                if (LockRegions != null && LockRegions.Length > 0)
                    sb.Append($"[{string.Join(",", LockRegions)}]");
            }
            if (SetSecurity)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append($"{nameof(SetSecurity)}: {SetSecurity}");
            }
            return sb.ToString();
        }
    }
}

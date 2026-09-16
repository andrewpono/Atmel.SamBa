namespace Anp.Atmel.SamBa.Events
{
    /// <summary>
    /// The stage names this library puts in <see cref="SamBaProgressEventArgs.Stage"/>. Gathered
    /// here because a stage spans layers — "Identifying" is reported from the chip probe, from
    /// <c>SamBaDevice.Open</c> and from the geometry check: three sites across two files, which have to
    /// agree on the spelling or a consumer grouping progress by stage sees three stages, not one.
    /// <para>
    /// Internal: the strings themselves are the contract a consumer matches on, so they are part of
    /// what this library publishes, but the names below are not — a consumer comparing
    /// <see cref="SamBaProgressEventArgs.Stage"/> against its own literal is doing the expected
    /// thing. Which is also why no value here may change without a note in the changelog.
    /// </para>
    /// </summary>
    internal static class ProgressStage
    {
        /// <summary>Opening the port, entering binary mode, reading the monitor version.</summary>
        public const string Connecting = "Connecting";

        /// <summary>Closing the port at the end of a session.</summary>
        public const string Disconnecting = "Disconnecting";

        /// <summary>Reading the identification registers, and reporting what they came to.</summary>
        public const string Identifying = "Identifying";

        /// <summary>Erasing flash, whole-chip or from an offset.</summary>
        public const string Erasing = "Erasing";

        /// <summary>Programming write blocks, including the padding and merge notices.</summary>
        public const string Writing = "Writing";

        /// <summary>Reading the written region back and comparing it against the image.</summary>
        public const string Verifying = "Verifying";

        /// <summary>Clearing lock regions ahead of an erase or write.</summary>
        public const string Unlocking = "Unlock";

        /// <summary>Applying the boot, lock and security options after a write.</summary>
        public const string Options = "Options";

        /// <summary>Resetting the device at the end of an update.</summary>
        public const string Resetting = "Reset";
    }
}

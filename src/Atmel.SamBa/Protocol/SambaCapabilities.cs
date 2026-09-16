namespace Anp.Atmel.SamBa.Protocol
{
    /// <summary>
    /// How the monitor on the other end differs from the baseline command set: one shortcut it may
    /// offer, and one limit it may impose. Both follow from the <c>[Arduino:XYZ]</c> banner in the
    /// <c>V#</c> version reply, which only Arduino-family bootloaders emit — anything else is a
    /// <see cref="RomMonitor"/>.
    /// </summary>
    internal readonly struct SambaCapabilities
    {
        /// <summary>
        /// Longest <c>R#</c> chunk an Arduino bootloader tolerates: they corrupt USB reads of 64
        /// bytes or more, so chunks are cut to 63. A defect to work around, not a feature.
        /// </summary>
        internal const int ArduinoReadChunkLimit = 63;

        /// <summary>
        /// True when the monitor offers the <c>X#</c> whole-chip-erase command. This says nothing
        /// about whether the chip can be erased — every supported part can, through its flash
        /// controller (<c>FlashController.EraseAll</c>), and both routes erase from the requested
        /// offset to the end of flash. <c>X#</c> is only the shorter one: a single command the
        /// monitor waits out itself in place of a host-driven register loop. See
        /// <see cref="SambaMonitor.ChipErase(uint)"/> for why the extension was added.
        /// </summary>
        public bool HasChipEraseCommand { get; }

        /// <summary>
        /// Maximum bytes per <c>R#</c> read chunk that this monitor imposes, or null when it imposes
        /// none — in which case <see cref="SambaMonitor.DefaultBlockChunk"/> applies instead, since a
        /// read is waited out on a fixed budget and an unbounded transfer has no budget that means
        /// anything. Null rather than 0 so the absence of a monitor cap is in the type instead of a
        /// sentinel — and so the default value of this struct, which an unconnected monitor reports,
        /// means "no cap of its own" rather than "zero-byte chunks".
        /// </summary>
        public int? ReadChunkLimit { get; }

        private SambaCapabilities(bool hasChipEraseCommand, int? readChunkLimit)
        {
            HasChipEraseCommand = hasChipEraseCommand;
            ReadChunkLimit = readChunkLimit;
        }

        /// <summary>
        /// A stock ROM monitor: nothing advertised, no read cap of its own. Also what an unconnected
        /// monitor reports, since it is the default value of this struct.
        /// </summary>
        public static SambaCapabilities RomMonitor => new SambaCapabilities(false, null);

        /// <summary>
        /// An Arduino-family bootloader. Carries <see cref="ArduinoReadChunkLimit"/> whatever its
        /// banner lists, plus <c>X#</c> when the banner lists it.
        /// </summary>
        public static SambaCapabilities ArduinoBootloader(bool hasChipEraseCommand) =>
            new SambaCapabilities(hasChipEraseCommand, ArduinoReadChunkLimit);
    }
}

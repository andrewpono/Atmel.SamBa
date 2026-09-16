namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Which path <see cref="Chips.ChipIdentifier.Identify"/> takes at its one branch that both can be
    /// overridden and is worth being able to: the read of the ARM reset vector at address 0, which
    /// chooses between the legacy CHIPID probe (AT91SAM7/9) and the Cortex-M CPUID probe (every other
    /// supported part).
    /// </summary>
    /// <remarks>
    /// That read is ordinary flash content, not a register a bus decodes specially, and the detection
    /// is only a guess at what it means: a genuine AT91SAM7/9 part is not guaranteed to have a branch
    /// opcode at address 0 at all — blank flash, or firmware that left something else there, reads back
    /// looking like a Cortex-M part despite the core being legacy. When that happens, <c>Auto</c> takes
    /// the wrong branch and the very next read — the CPUID register, which does not exist on ARM7/9 —
    /// hangs the SAM-BA monitor rather than merely answering wrong, leaving the library unable to talk
    /// to the part for the rest of the session. <see cref="ChipId"/> and <see cref="CpuId"/> remove the
    /// guess entirely by naming the branch directly instead of inferring it.
    /// <para>
    /// That directness is also the risk in the other direction: forcing either value on a part of the
    /// other generation makes <see cref="Chips.ChipIdentifier.Identify"/> read a register the connected
    /// part does not have, which is the same hang <see cref="Chips.ChipIdentifier"/>'s own probe order
    /// exists to avoid (see DESIGN.md "Timeout ownership"). Use <see cref="ChipId"/> or <see cref="CpuId"/>
    /// only for a part whose core generation is already known — from a previous <see cref="Auto"/>
    /// identification of the same board, for instance — never to guess.
    /// </para>
    /// </remarks>
    public enum SamBaChipIdentificationMode
    {
        /// <summary>
        /// Reads the reset vector and takes the branch its content selects. The default, and the only
        /// mode safe to use without already knowing the part's core generation.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Skips the reset-vector read and goes straight to the legacy CHIPID probe — the AT91SAM7/9
        /// parts, told apart by the CIDR register alone. Only use this on a part already known to be
        /// one of those; see the type remarks for what forcing it on anything else costs.
        /// </summary>
        ChipId,

        /// <summary>
        /// Skips the reset-vector read and goes straight to the Cortex-M CPUID probe — every other
        /// supported part — which then still branches on the CPUID PARTNO field, the reset-handler
        /// region and the DSU exactly as <see cref="Auto"/> would once it arrived there. Only use this
        /// on a part already known to have a CPUID register; see the type remarks for what forcing it
        /// on an AT91SAM7/9 part costs.
        /// </summary>
        CpuId,
    }
}

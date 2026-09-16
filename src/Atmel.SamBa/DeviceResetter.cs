using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;

namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Per-family CPU reset: one keyed write, either to the part's reset controller (RSTC_CR) or to
    /// the Cortex-M core's own reset request (AIRCR). Both registers ignore a write that does not
    /// carry their key, which is why each command word below is a key ORed with the reset bits.
    /// </summary>
    internal static class DeviceResetter
    {
        /// <summary>
        /// Reset register addresses. AIRCR sits at a fixed architectural address on every Cortex-M;
        /// RSTC is a peripheral whose base moved between families, so each family group carries its
        /// own. Internal rather than private so <see cref="Families"/>'s table can reference these
        /// by name instead of repeating the literals.
        /// </summary>
        internal static class Reg
        {
            /// <summary>Cortex-M application interrupt and reset control register.</summary>
            public const uint CortexAircr = 0xE000ED0C;

            /// <summary>RSTC_CR on SAM3A / SAM3S / SAM3X.</summary>
            public const uint RstcSam3A3S3X = 0x400E1A00;

            /// <summary>RSTC_CR on SAM3U.</summary>
            public const uint RstcSam3U = 0x400E1200;

            /// <summary>RSTC_CR on SAM3N / SAM4S.</summary>
            public const uint RstcSam3N4S = 0x400E1400;

            /// <summary>RSTC_CR on SAM4E.</summary>
            public const uint RstcSam4E = 0x400E1800;

            /// <summary>
            /// RSTC_CR on SAM7 and SAM9XE, where the reset controller lives in the legacy
            /// system-controller block at the top of the address space.
            /// </summary>
            public const uint RstcSam7And9 = 0xFFFFFD00;
        }

        /// <summary>RSTC_CR write key (KEY field) — a write without it is ignored.</summary>
        private const uint RstcKey = 0xA5;

        /// <summary>Position of the RSTC_CR key field (bits 31-24).</summary>
        private const int RstcKeyShift = 24;

        /// <summary>RSTC_CR.PROCRST (bit 0) — reset the processor.</summary>
        private const uint RstcProcessorResetMask = 1u << 0;

        /// <summary>RSTC_CR.PERRST (bit 2) — reset the peripherals.</summary>
        private const uint RstcPeripheralResetMask = 1u << 2;

        /// <summary>RSTC_CR.EXTRST (bit 3) — assert the external NRST line.</summary>
        private const uint RstcExternalResetMask = 1u << 3;

        /// <summary>
        /// The only RSTC_CR word this class writes: keyed, resetting the core, the peripherals and
        /// NRST together, so nothing is left running against a freshly reset processor.
        /// </summary>
        private const uint RstcResetCommand = (RstcKey << RstcKeyShift)
            | RstcProcessorResetMask
            | RstcPeripheralResetMask
            | RstcExternalResetMask;

        /// <summary>AIRCR write key (VECTKEY field) — a write without it is ignored.</summary>
        private const uint AircrVectorKey = 0x05FA;

        /// <summary>Position of the AIRCR key field (bits 31-16).</summary>
        private const int AircrVectorKeyShift = 16;

        /// <summary>AIRCR.SYSRESETREQ (bit 2) — request a system reset.</summary>
        private const uint AircrSystemResetRequestMask = 1u << 2;

        /// <summary>The only AIRCR word this class writes: a keyed system-reset request.</summary>
        private const uint AircrResetCommand =
            (AircrVectorKey << AircrVectorKeyShift) | AircrSystemResetRequestMask;

        /// <summary>
        /// Resets the device. The write usually kills the USB CDC port mid-transaction, so a
        /// transport failure here is the expected success signature and is swallowed.
        /// <para>
        /// One family errata worth knowing about on the D5x/E5x parts: the detection of a user
        /// reset — external, watchdog or the system reset request written below — can fail on
        /// revision A and D silicon whose BOD33 Disable fuse is cleared. The remedy is device
        /// configuration (enable BOD33 through SUPC.BOD33 and leave the fuse set), so it is not
        /// something this method can do. The failure mode to recognise is a reset that reports
        /// success and leaves the part running the bootloader: the write itself is accepted, only
        /// its effect goes missing.
        /// </para>
        /// </summary>
        /// <param name="monitor">The open SAM-BA monitor connection.</param>
        /// <param name="family">The identified family, or <see cref="SamBaChipFamily.Unknown"/>.</param>
        /// <param name="cpuId">
        /// The part's CPUID register, if the initial probe read one (<see cref="SamBaChipInfo.CpuId"/>).
        /// Non-null only for a part that answered on the Cortex-M identification branch, which is
        /// exactly the set of cores that carry AIRCR at its fixed architectural address — so an
        /// <see cref="SamBaChipFamily.Unknown"/> part with a CPUID still has a reset route, even
        /// though it has no family-specific one.
        /// </param>
        /// <returns>
        /// False when neither the family nor the CPUID probe gives a reset route: an
        /// <see cref="SamBaChipFamily.Unknown"/> part whose CPUID read did not happen either, meaning
        /// it was identified on the legacy CHIPID-only branch and so is not confirmed to be Cortex-M.
        /// </returns>
        public static bool TryReset(SambaMonitor monitor, SamBaChipFamily family, uint? cpuId)
        {
            bool hasFamilyRoute = Families.TryGet(family, out FamilyDescriptor descriptor);
            if (!hasFamilyRoute && !cpuId.HasValue)
                return false;

            try
            {
                // NVMCTRL parts, the SAMx7x generation, and an unlisted-but-confirmed-Cortex-M part
                // (no descriptor at all) carry no RstcAddress: they reset through the core's own
                // architectural AIRCR instead of a peripheral whose base moved between families.
                if (hasFamilyRoute && descriptor.RstcAddress.HasValue)
                    monitor.WriteWord(descriptor.RstcAddress.Value, RstcResetCommand);
                else
                    monitor.WriteWord(Reg.CortexAircr, AircrResetCommand);
                return true;
            }
            catch (SamBaTransportException)
            {
                // The device dropped off the bus while resetting — that is the point.
                return true;
            }
        }
    }
}

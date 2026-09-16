namespace Anp.Atmel.SamBa.Exceptions
{
    /// <summary>
    /// The connected device answered the SAM-BA monitor but its chip identification
    /// did not match any supported device.
    /// <para>
    /// Each identification word below is null when the probe never read the register holding it, and
    /// carries a value when it did — zero included. The difference matters in a report of a
    /// misidentified part: a DSU that answered zero says the part was asked and had nothing to say,
    /// while a null DSU DID says the probe branched elsewhere and never asked. Expect most of them to
    /// be null, since a chip is identified through CHIPID or through the DSU and the probe stops as
    /// soon as it knows which.
    /// </para>
    /// </summary>
    public sealed class SamBaUnsupportedDeviceException : SamBaException
    {
        /// <summary>
        /// CHIPID identification word (CIDR); null when the probe never read a CHIPID register.
        /// </summary>
        public uint? ChipId { get; }

        /// <summary>
        /// CHIPID extension word (EXID); null when no CIDR answered and no extension was looked for.
        /// Zero on the many CHIPID parts that have none.
        /// </summary>
        public uint? ExtendedChipId { get; }

        /// <summary>
        /// DSU device identification word (DID); null when the probe never read a DSU.
        /// </summary>
        public uint? DeviceId { get; }

        /// <summary>
        /// Whole ARM CPUID register; null on the ARM7/ARM9 parts, which have no such register.
        /// </summary>
        public uint? CpuId { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SamBaUnsupportedDeviceException"/> class with
        /// every identification word the probe managed to read, null standing for one it never
        /// reached.
        /// </summary>
        /// <param name="chipId">CHIPID identification word (CIDR)</param>
        /// <param name="extendedChipId">CHIPID extension word (EXID)</param>
        /// <param name="deviceId">DSU device identification word (DID)</param>
        /// <param name="cpuId">ARM CPUID register value</param>
        internal SamBaUnsupportedDeviceException(uint? chipId, uint? extendedChipId, uint? deviceId, uint? cpuId)
            : this("Device is not supported.", chipId, extendedChipId, deviceId, cpuId)
        {
        }

        /// <summary>
        /// Initializes an instance whose <paramref name="reason"/> says more than "not supported" —
        /// a rejection past the probe, where a part was placed by family fallback and its flash
        /// controller then reported no usable geometry to run on. The identification words are
        /// carried here as they are on the probe's own rejection, since they are what a report about
        /// an unplaceable part needs and null on this type means the register went unread.
        /// </summary>
        /// <param name="reason">Why the device is unsupported; the words are appended to it.</param>
        /// <param name="chipId">CHIPID identification word (CIDR)</param>
        /// <param name="extendedChipId">CHIPID extension word (EXID)</param>
        /// <param name="deviceId">DSU device identification word (DID)</param>
        /// <param name="cpuId">ARM CPUID register value</param>
        internal SamBaUnsupportedDeviceException(
            string reason, uint? chipId, uint? extendedChipId, uint? deviceId, uint? cpuId)
            : base(FormatMessage(reason, chipId, extendedChipId, deviceId, cpuId))
        {
            ChipId = chipId;
            ExtendedChipId = extendedChipId;
            DeviceId = deviceId;
            CpuId = cpuId;
        }

        private static string FormatMessage(
            string reason, uint? chipId, uint? extendedChipId, uint? deviceId, uint? cpuId)
        {
            return $"{reason} " +
                $"CHIPID={Word(chipId)}, EXID={Word(extendedChipId)}, " +
                $"DSU DID={Word(deviceId)}, CPUID={Word(cpuId)}.";
        }

        /// <summary>
        /// One identification word for the message: its hex value, or "not read" where the probe
        /// never reached the register. Spelled out rather than shown as 0x00000000, so that a report
        /// pasted from this message cannot be misread as a register that answered zero.
        /// </summary>
        private static string Word(uint? value)
        {
            return value.HasValue ? $"0x{value.Value:X8}" : "not read";
        }
    }
}

namespace Anp.Atmel.SamBa.Configuration
{
    /// <summary>
    /// Options controlling SAM-BA device discovery and watching.
    /// </summary>
    public sealed class SamBaDiscoveryOptions
    {
        /// <summary>
        /// USB vendor id filter; null matches any. Defaults to Atmel (<see cref="SamBaDevice.DefaultVendorId"/>).
        /// </summary>
        public ushort? VendorId { get; set; } = SamBaDevice.DefaultVendorId;

        /// <summary>
        /// USB product id filter; null matches any. Defaults to the SAM-BA CDC function
        /// (<see cref="SamBaDevice.DefaultProductId"/>).
        /// </summary>
        public ushort? ProductId { get; set; } = SamBaDevice.DefaultProductId;

        /// <summary>
        /// Creates an independent copy. Both properties are value types, so the copy shares nothing
        /// with the original — <see cref="SamBaDeviceDiscovery.ConfigureDefaults"/> depends on that
        /// to configure a copy and swap it in without a concurrent reader ever seeing a half-applied
        /// change. Keep it true of anything added here.
        /// </summary>
        public SamBaDiscoveryOptions Clone() => (SamBaDiscoveryOptions)MemberwiseClone();

        /// <summary>
        /// The two filters as hex, or "any" where one is unset. Diagnostic text; the format is not
        /// stable and nothing parses it back.
        /// </summary>
        public override string ToString()
        {
            string vid = VendorId.HasValue ? $"0x{VendorId.Value:X4}" : "any";
            string pid = ProductId.HasValue ? $"0x{ProductId.Value:X4}" : "any";
            return $"{nameof(VendorId)}: {vid}, {nameof(ProductId)}: {pid}";
        }
    }
}

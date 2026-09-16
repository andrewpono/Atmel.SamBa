using System;
using Anp.Atmel.SamBa.Configuration;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// The discovery defaults are process-wide static state, so every test here restores what it
    /// found. Keeping them in one class is part of that: xUnit runs a class's tests one at a time,
    /// so no two of these overlap, and nothing outside this file reads or writes the defaults.
    /// </summary>
    public class SamBaDeviceDiscoveryTests
    {
        [Fact]
        public void ConfigureDefaults_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SamBaDeviceDiscovery.ConfigureDefaults(null!));
        }

        [Fact]
        public void Defaults_OutOfTheBox_AreTheAtmelSamBaFilter()
        {
            var defaults = SamBaDeviceDiscovery.SnapshotDefaults();

            Assert.Equal(SamBaDevice.DefaultVendorId, defaults.VendorId);
            Assert.Equal(SamBaDevice.DefaultProductId, defaults.ProductId);
        }

        [Fact]
        public void ConfigureDefaults_AppliesToLaterSnapshots()
        {
            var saved = SamBaDeviceDiscovery.SnapshotDefaults();
            try
            {
                SamBaDeviceDiscovery.ConfigureDefaults(o =>
                {
                    o.VendorId = 0x2341;
                    o.ProductId = null;     // null matches any product
                });

                var applied = SamBaDeviceDiscovery.SnapshotDefaults();
                Assert.Equal((ushort)0x2341, applied.VendorId);
                Assert.Null(applied.ProductId);
            }
            finally
            {
                Restore(saved);
            }
        }

        [Fact]
        public void SnapshotDefaults_ReturnsAnIndependentCopy()
        {
            var saved = SamBaDeviceDiscovery.SnapshotDefaults();
            try
            {
                var snapshot = SamBaDeviceDiscovery.SnapshotDefaults();
                snapshot.VendorId = 0xDEAD;

                // Editing a snapshot must not reach the shared defaults: ConfigureDefaults relies on
                // Clone handing out something detached, and so does every caller that holds one.
                Assert.Equal(saved.VendorId, SamBaDeviceDiscovery.SnapshotDefaults().VendorId);
            }
            finally
            {
                Restore(saved);
            }
        }

        [Fact]
        public void ConfigureDefaults_WhenConfigureThrows_LeavesDefaultsUnchanged()
        {
            var saved = SamBaDeviceDiscovery.SnapshotDefaults();
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    SamBaDeviceDiscovery.ConfigureDefaults(o =>
                    {
                        o.VendorId = 0xBAD1;
                        throw new InvalidOperationException("configure failed");
                    }));

                var after = SamBaDeviceDiscovery.SnapshotDefaults();
                Assert.Equal(saved.VendorId, after.VendorId);
                Assert.Equal(saved.ProductId, after.ProductId);
            }
            finally
            {
                Restore(saved);
            }
        }

        private static void Restore(SamBaDiscoveryOptions saved)
        {
            SamBaDeviceDiscovery.ConfigureDefaults(o =>
            {
                o.VendorId = saved.VendorId;
                o.ProductId = saved.ProductId;
            });
        }
    }
}

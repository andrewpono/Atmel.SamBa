using Anp.Atmel.SamBa.Protocol;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// Pins the public transport constructor's contract: the seam a caller uses to run the
    /// library over a custom link. Metadata behavior, path flow-through, and the ownership rule
    /// (the device disposes the transport, exactly once).
    /// </summary>
    public sealed class SamBaDeviceTransportCtorTests
    {
        [Fact]
        public void Ctor_NullTransport_ThrowsWithTheParameterName()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new SamBaDevice((ISambaTransport)null!));

            Assert.Equal("transport", ex.ParamName);
        }

        [Fact]
        public void Ctor_LeavesPortMetadataEmpty_AndTakesThePathFromTheTransport()
        {
            var transport = new ScriptedTransport();

            using var device = new SamBaDevice(transport);

            Assert.Equal(string.Empty, device.PortName);
            Assert.Equal(string.Empty, device.FriendlyName);
            Assert.Null(device.VendorId);
            Assert.Null(device.ProductId);
            Assert.Equal(transport.DevicePath, device.DevicePath);
        }

        [Fact]
        public void DisplayName_WithoutMetadataOrIdentification_FallsBackToSerialDeviceOnPath()
        {
            var transport = new ScriptedTransport();

            using var device = new SamBaDevice(transport);

            Assert.Equal($"Serial device on {transport.DevicePath}", device.DisplayName);
        }

        [Fact]
        public void Dispose_DisposesTheOwnedTransportExactlyOnce_AndASecondDisposeIsANoOp()
        {
            var transport = new ScriptedTransport();
            var device = new SamBaDevice(transport);

            device.Dispose();
            device.Dispose();

            Assert.Equal(1, transport.DisposeCount);
        }
    }
}

using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System.IO.Ports;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// The System.IO.Ports transport the portable targets ship behind <c>SamBaDevice(string)</c>.
    /// Everything here runs without hardware: the timeout arithmetic, construction, and the
    /// closed-port refusals. Open() against a real port is exercised only on a device.
    /// This file compiles only on the net8.0 test target, where the library's portable build
    /// (the one carrying this type) is resolved.
    /// </summary>
    public sealed class PortableSerialTransportTests
    {
        [Fact]
        public void Ctor_NullPath_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PortableSerialTransport(null!));
        }

        [Fact]
        public void DevicePath_EchoesTheConstructionPath_AndStartsClosed()
        {
            var transport = new PortableSerialTransport("/dev/ttyACM0");

            Assert.Equal("/dev/ttyACM0", transport.DevicePath);
            Assert.False(transport.IsOpen);
        }

        [Fact]
        public void ToPortTimeout_InfiniteMapsToTheSerialPortSentinel()
        {
            Assert.Equal(
                SerialPort.InfiniteTimeout, PortableSerialTransport.ToPortTimeout(Timeout.InfiniteTimeSpan));
        }

        [Theory]
        [InlineData(0, 1)]                    // zero budget still has to be a legal value
        [InlineData(400, 1)]                  // 0.04 ms rounds up, not down to an illegal 0
        [InlineData(10_000, 1)]               // 1 ms exactly
        [InlineData(50_000_000, 5000)]        // 5 s
        public void ToPortTimeout_CeilsToWholeMilliseconds_WithAFloorOfOne(long ticks, int expected)
        {
            Assert.Equal(expected, PortableSerialTransport.ToPortTimeout(TimeSpan.FromTicks(ticks)));
        }

        [Fact]
        public void ToPortTimeout_HugeBudgetsClampToIntMax()
        {
            Assert.Equal(int.MaxValue, PortableSerialTransport.ToPortTimeout(TimeSpan.MaxValue));
        }

        [Fact]
        public void EveryIoOperation_OnAClosedPort_RefusesWithTheOperationName()
        {
            var transport = new PortableSerialTransport("/dev/ttyACM0");
            var buffer = new byte[4];

            var write = Assert.Throws<SamBaTransportException>(
                () => transport.Write(buffer, 0, 4, TimeSpan.FromSeconds(1)));
            var readExact = Assert.Throws<SamBaTransportException>(
                () => transport.ReadExact(buffer, 0, 4, TimeSpan.FromSeconds(1)));
            var read = Assert.Throws<SamBaTransportException>(
                () => transport.Read(buffer, 0, 4, TimeSpan.FromSeconds(1)));
            var purge = Assert.Throws<SamBaTransportException>(() => transport.Purge());

            Assert.Equal("Write", write.Operation);
            Assert.Equal("ReadExact", readExact.Operation);
            Assert.Equal("Read", read.Operation);
            Assert.Equal("Purge", purge.Operation);
        }

        [Fact]
        public void CloseAndDispose_OnANeverOpenedTransport_AreNoOps()
        {
            var transport = new PortableSerialTransport("/dev/ttyACM0");

            transport.Close();
            transport.Dispose();

            Assert.False(transport.IsOpen);
        }
    }
}

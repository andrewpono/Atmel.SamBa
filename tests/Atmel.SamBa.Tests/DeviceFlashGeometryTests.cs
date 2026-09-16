using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Flash;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// The gate on its own. Every criterion here is one the flash layer's arithmetic relies on
    /// silently, so a geometry that fails one would not read oddly — it would corrupt a write. The two
    /// probes that exist are exercised through their controllers in <c>FlashControllerTests</c>; these
    /// go at the gate directly, including several answers no probe produces today, because the gate is
    /// the contract for any device-reported geometry rather than for those two alone.
    /// </summary>
    public class DeviceFlashGeometryTests
    {
        // A page is a write block on the EFC families, which is the gate's easiest case: every other
        // generation asks for more pages per block, never fewer.
        private const int OnePagePerBlock = 1;

        [Fact]
        public void IsUsable_AcceptsARealGeometry()
        {
            // ATSAM3X8: 2048 pages x 256 B across two EEFCs, 32 lock regions.
            var geometry = new DeviceFlashGeometry(2048, 256, 2, 32);

            Assert.True(geometry.IsUsable(OnePagePerBlock));
            Assert.Equal(512 * 1024, geometry.FlashSize);
        }

        [Theory]
        // Zeros: a part with no descriptor, or a register that reads dead.
        [InlineData(0, 0, 0, 0)]
        // A page size that is not a power of two, and one outside the encodable range either way.
        [InlineData(1024, 96, 1, 16)]
        [InlineData(1024, 4, 1, 16)]
        [InlineData(1024, 16384, 1, 16)]
        // Bigger than any SAM part's flash by a wide margin — 32 MB.
        [InlineData(65536, 512, 1, 16)]
        // Plane counts the flash layer has no register block for.
        [InlineData(1024, 256, 0, 16)]
        [InlineData(1024, 256, 3, 16)]
        // No lock regions, and a count the pages do not divide into evenly.
        [InlineData(1024, 256, 1, 0)]
        [InlineData(1000, 256, 1, 16)]
        // Two planes, odd totals: PagesPerPlane and LockRegionsPerPlane are plain integer halves, so
        // either of these would put every page number and lock argument above the boundary one short.
        [InlineData(1023, 256, 2, 31)]
        [InlineData(2048, 256, 2, 1)]
        public void IsUsable_RefusesGeometryTheFlashLayerCannotAddressEvenly(
            int pageCount, int pageSize, int planeCount, int lockRegionCount)
        {
            var geometry = new DeviceFlashGeometry(pageCount, pageSize, planeCount, lockRegionCount);

            Assert.False(geometry.IsUsable(OnePagePerBlock));
        }

        [Fact]
        public void IsUsable_RefusesPagesThatDoNotFillWholeWriteBlocks()
        {
            // Same geometry, two generations: the D5x erases 16 pages at a time, so a page count that
            // leaves a part-block is unusable there while being perfectly fine on an EEFC part.
            var geometry = new DeviceFlashGeometry(1000, 512, 1, 8);

            Assert.True(geometry.IsUsable(OnePagePerBlock));
            Assert.False(geometry.IsUsable(16));
        }

        [Fact]
        public void Matches_ComparesAllFourFieldsAgainstTheRow()
        {
            ChipRecord row = ChipTable.Rows.First(r => r.Name == "ATSAM3X8");
            var same = new DeviceFlashGeometry(2048, 256, 2, 32);
            var differsByOneField = new DeviceFlashGeometry(2048, 256, 2, 16);

            Assert.True(same.Matches(row));
            Assert.False(differsByOneField.Matches(row));
        }
    }
}

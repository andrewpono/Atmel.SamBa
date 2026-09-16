using System.Linq;
using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Chips;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    public class SupportedChipsTests
    {
        [Fact]
        public void Get_ReturnsTheSameListEveryCall()
        {
            Assert.Same(SupportedChips.Get(), SupportedChips.Get());
        }

        [Fact]
        public void Get_HandsOutAListNobodyCanWriteTo()
        {
            // The catalog is built once and shared, so the wrapper is what keeps one caller from
            // rewriting what every later caller sees. A bare array would let that through a cast.
            var chips = SupportedChips.Get();

            Assert.IsNotType<SamBaChipInfo[]>(chips);
            Assert.False(chips is IList<SamBaChipInfo> list && !list.IsReadOnly);
        }

        [Fact]
        public void Get_ReturnsDistinctNames()
        {
            var chips = SupportedChips.Get();

            int expectedDistinct = ChipTable.Rows.Select(r => r.Name).Distinct().Count();
            Assert.Equal(expectedDistinct, chips.Count);

            // No duplicate names in the result.
            Assert.Equal(chips.Count, chips.Select(c => c.Name).Distinct().Count());

            // Dedup actually collapsed variant rows sharing a name.
            Assert.True(chips.Count < ChipTable.Rows.Count);
        }

        [Fact]
        public void Get_ContainsKnownParts()
        {
            var chips = SupportedChips.Get();

            foreach (var name in new[] { "ATSAM3X8", "ATSAMD21x18", "ATSAME54x20", "AT91SAM7SE512" })
                Assert.Contains(chips, c => c.Name == name);

            Assert.All(chips, c =>
            {
                Assert.True(c.FlashSize > 0, c.Name);
                Assert.NotEqual(SamBaChipFamily.Unknown, c.Family);
            });
        }

        [Fact]
        public void Get_TableEntries_CarryNoIdentificationWords()
        {
            // A catalog entry is not a probe result, so all four words are null rather than 0 — and
            // ToString stays a pure geometry summary, with no empty id list bracketed onto the end.
            var chips = SupportedChips.Get();

            Assert.All(chips, c =>
            {
                Assert.Null(c.ChipId);
                Assert.Null(c.ExtendedChipId);
                Assert.Null(c.DeviceId);
                Assert.Null(c.CpuId);
                Assert.DoesNotContain("[", c.ToString());
            });
        }

        [Fact]
        public void Get_TableEntries_ReportUniqueIdSupport()
        {
            // Unlike the identification words, this one is a property of the part, so a catalog entry
            // answers it — that is what makes it askable before anything is plugged in.
            var chips = SupportedChips.Get();

            Assert.All(chips, c =>
                Assert.Equal(ChipTable.Rows.First(r => r.Name == c.Name).UniqueIdWords != 0, c.HasUniqueId));

            Assert.True(chips.First(c => c.Name == "ATSAM3X8").HasUniqueId);
            Assert.False(chips.First(c => c.Name == "AT91SAM9XE512").HasUniqueId);   // EEFC without the command
            Assert.True(chips.First(c => c.Name == "ATSAMD21x18").HasUniqueId);      // NVMCTRL serial number
            Assert.True(chips.First(c => c.Name == "ATSAME54x20").HasUniqueId);      // the other generation
            Assert.False(chips.First(c => c.Name == "AT91SAM7SE512").HasUniqueId);   // legacy EFC
        }

        [Fact]
        public void Get_OrderedByFamilyThenName()
        {
            var chips = SupportedChips.Get();

            // Ordinal, matching Get(): the default string comparer is culture-sensitive, so asserting
            // with it would pin whatever order this machine's locale happens to produce.
            var expected = chips.OrderBy(c => c.Family).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
            Assert.Equal(expected.Select(c => c.Name), chips.Select(c => c.Name));
        }
    }
}

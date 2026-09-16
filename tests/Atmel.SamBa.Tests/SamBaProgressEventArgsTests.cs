using Anp.Atmel.SamBa.Events;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    public class SamBaProgressEventArgsTests
    {
        [Fact]
        public void ToString_DeterminateWithStage_RendersCountsUnitsAndPercentage()
        {
            // The shape every write-loop event takes: stage set, message left empty.
            var args = new SamBaProgressEventArgs(1, 3, stage: "Writing", units: "blocks");

            Assert.Equal("1/3 blocks (33%) - Writing", args.ToString());
        }

        [Theory]
        // Nothing to separate: neither a trailing dash nor a trailing colon may survive.
        [InlineData(null, null, "5/10 (50%)")]
        [InlineData("Writing", null, "5/10 (50%) - Writing")]
        [InlineData(null, "halfway", "5/10 (50%) - halfway")]
        [InlineData("Writing", "halfway", "5/10 (50%) - Writing: halfway")]
        public void ToString_OmitsSeparatorsForAbsentParts(string? stage, string? message, string expected)
        {
            var args = new SamBaProgressEventArgs(5, 10, message: message, stage: stage);

            Assert.Equal(expected, args.ToString());
        }

        [Fact]
        public void ToString_IndeterminateWithStageOnly_HasNoDanglingColon()
        {
            var args = SamBaProgressEventArgs.Indeterminate(null, "Erasing");

            Assert.Equal("Erasing", args.ToString());
        }

        [Fact]
        public void ToString_IndeterminateWithStageAndMessage_JoinsThem()
        {
            var args = SamBaProgressEventArgs.Indeterminate("Erasing flash", "Erasing");

            Assert.Equal("Erasing: Erasing flash", args.ToString());
        }

        [Theory]
        // Truncating, so 100 arrives only on completion rather than from 99.5% onwards.
        [InlineData(0, 100, 0)]
        [InlineData(994, 1000, 99)]
        [InlineData(999, 1000, 99)]
        [InlineData(1000, 1000, 100)]
        public void Percentage_Truncates(long value, long maximum, int expected)
        {
            Assert.Equal(expected, new SamBaProgressEventArgs(value, maximum).Percentage);
        }

        [Fact]
        public void Fraction_ClampsValuesOutsideTheRange()
        {
            // Nothing validates Value against Maximum, so the clamp is the only thing keeping a
            // consumer's progress bar inside its track.
            Assert.Equal(1d, new SamBaProgressEventArgs(20, 10).Fraction);
            Assert.Equal(0d, new SamBaProgressEventArgs(-5, 10).Fraction);
            Assert.Equal(100, new SamBaProgressEventArgs(20, 10).Percentage);
        }

        [Fact]
        public void NonPositiveMaximum_IsIndeterminate_EvenViaTheDeterminateConstructor()
        {
            var zero = new SamBaProgressEventArgs(0, 0);

            Assert.True(zero.IsIndeterminate);
            Assert.Null(zero.Fraction);
            Assert.Null(zero.Percentage);
        }
    }
}

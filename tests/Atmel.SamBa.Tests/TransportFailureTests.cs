using Anp.Atmel.SamBa.Protocol;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// Pins the one shared definition of "link failure" every transport wraps into
    /// <c>SamBaTransportException</c> — the boundary both serial stacks must agree on.
    /// </summary>
    public sealed class TransportFailureTests
    {
        [Theory]
        [InlineData(typeof(IOException))]
        [InlineData(typeof(TimeoutException))]
        [InlineData(typeof(UnauthorizedAccessException))]
        [InlineData(typeof(InvalidOperationException))]
        [InlineData(typeof(ObjectDisposedException))]
        [InlineData(typeof(OperationCanceledException))]
        public void IsFailure_MatchesEveryPortLevelExceptionType(Type exceptionType)
        {
            var ex = (Exception)Activator.CreateInstance(exceptionType, "boom")!;

            Assert.True(TransportFailure.IsFailure(ex));
        }

        [Fact]
        public void IsFailure_MatchesDerivedTypes_SurpriseRemovalIsAnIOException()
        {
            Assert.True(TransportFailure.IsFailure(new FileNotFoundException("gone")));
        }

        [Theory]
        [InlineData(typeof(ArgumentException))]
        [InlineData(typeof(ArgumentNullException))]
        [InlineData(typeof(NotSupportedException))]
        [InlineData(typeof(Exception))]
        public void IsFailure_LeavesCallerBugsAndUnknownsAlone(Type exceptionType)
        {
            var ex = (Exception)Activator.CreateInstance(exceptionType)!;

            Assert.False(TransportFailure.IsFailure(ex));
        }
    }
}

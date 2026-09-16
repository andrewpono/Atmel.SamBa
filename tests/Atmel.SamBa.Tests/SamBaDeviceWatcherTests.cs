using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    /// <summary>
    /// Covers the event-raising contract only. The rest of <see cref="SamBaDeviceWatcher"/> drives a
    /// real serial watcher and OS PnP notifications, which nothing here can stand in for.
    /// </summary>
    public class SamBaDeviceWatcherTests
    {
        [Fact]
        public void RaiseToEverySubscriber_NoneThrow_EachSeesTheSameSenderAndArgs()
        {
            var seen = new List<object>();
            EventHandler<EventArgs> handler = (s, e) => seen.Add(s!);
            handler += (s, e) => seen.Add(e);

            SamBaDeviceWatcher.RaiseToEverySubscriber(handler, this, EventArgs.Empty);

            Assert.Equal(new object[] { this, EventArgs.Empty }, seen);
        }

        [Fact]
        public void RaiseToEverySubscriber_OneThrows_LaterSubscribersStillRun()
        {
            var reached = new List<string>();
            EventHandler<EventArgs> handler = (s, e) =>
            {
                reached.Add("first");
                throw new InvalidOperationException("boom");
            };
            handler += (s, e) => reached.Add("second");

            var thrown = Assert.Throws<InvalidOperationException>(
                () => SamBaDeviceWatcher.RaiseToEverySubscriber(handler, this, EventArgs.Empty));

            Assert.Equal(new[] { "first", "second" }, reached);
            Assert.Equal("boom", thrown.Message);
        }

        [Fact]
        public void RaiseToEverySubscriber_OneThrows_KeepsTheStackTraceOfWhatThrew()
        {
            EventHandler<EventArgs> handler = (s, e) => ThrowFromHere();

            var thrown = Assert.Throws<InvalidOperationException>(
                () => SamBaDeviceWatcher.RaiseToEverySubscriber(handler, this, EventArgs.Empty));

            // The frame that actually threw has to survive. A bare `throw failure;` would replace the
            // trace with one starting inside the watcher, which is not a report anyone can debug.
            Assert.Contains(nameof(ThrowFromHere), thrown.StackTrace);
        }

        [Fact]
        public void RaiseToEverySubscriber_SeveralThrow_TheFailuresTravelTogether()
        {
            int reached = 0;
            EventHandler<EventArgs> handler = (s, e) =>
            {
                reached++;
                throw new InvalidOperationException("first");
            };
            handler += (s, e) =>
            {
                reached++;
                throw new NotSupportedException("second");
            };
            handler += (s, e) => reached++;

            var aggregate = Assert.Throws<AggregateException>(
                () => SamBaDeviceWatcher.RaiseToEverySubscriber(handler, this, EventArgs.Empty));

            Assert.Equal(3, reached);
            Assert.Collection(
                aggregate.InnerExceptions,
                first => Assert.IsType<InvalidOperationException>(first),
                second => Assert.IsType<NotSupportedException>(second));
        }

        // Not inlined, so the assertion above has a frame to find whatever the build configuration is.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowFromHere()
        {
            throw new InvalidOperationException("boom");
        }
    }
}

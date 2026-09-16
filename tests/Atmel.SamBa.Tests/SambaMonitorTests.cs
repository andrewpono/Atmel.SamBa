using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using System.Text;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    public class SambaMonitorTests
    {
        private const string PlainVersion = "v1.1 Dec 15 2010 19:25:04";
        private const string ArduinoVersion = "v2.0 [Arduino:XYZ] Apr 19 2019 14:38:48";

        private static (SambaMonitor monitor, ScriptedTransport transport) Connected(string version = PlainVersion)
        {
            var transport = new ScriptedTransport();
            transport.AddResponse("N#", "\n\r");
            transport.AddResponse("V#", version + "\n\r");
            var monitor = new SambaMonitor(transport);
            monitor.Connect();
            transport.Writes.Clear();
            return (monitor, transport);
        }

        [Fact]
        public void Connect_SendsBinaryModeThenVersion()
        {
            var transport = new ScriptedTransport();
            transport.AddResponse("N#", "\n\r");
            transport.AddResponse("V#", PlainVersion + "\n\r");
            var monitor = new SambaMonitor(transport);

            monitor.Connect();

            Assert.True(monitor.IsConnected);
            Assert.Contains(transport.Writes, w => Encoding.ASCII.GetString(w) == "N#");
            Assert.Contains(transport.Writes, w => Encoding.ASCII.GetString(w) == "V#");
            Assert.Equal(PlainVersion, monitor.Version);   // printable prefix only, "\n\r" stripped
            Assert.False(monitor.Capabilities.HasChipEraseCommand);
            Assert.Null(monitor.Capabilities.ReadChunkLimit);
        }

        [Fact]
        public void Connect_ParsesArduinoExtensions()
        {
            var (monitor, _) = Connected(ArduinoVersion);

            Assert.True(monitor.Capabilities.HasChipEraseCommand);
            Assert.Equal(SambaCapabilities.ArduinoReadChunkLimit, monitor.Capabilities.ReadChunkLimit);
        }

        [Fact]
        public void Connect_RetriesHandshakeOnFreshPortOnce()
        {
            var transport = new ScriptedTransport
            {
                // First open: no version reply -> handshake fails. Second open: full script.
                OnOpen = t =>
                {
                    if (t.OpenCount == 2)
                    {
                        t.AddResponse("N#", "\n\r");
                        t.AddResponse("V#", PlainVersion + "\n\r");
                    }
                },
            };
            var monitor = new SambaMonitor(transport);

            monitor.Connect();

            Assert.Equal(2, transport.OpenCount);
            Assert.Equal(PlainVersion, monitor.Version);
        }

        [Fact]
        public void Connect_WhenHandshakeFailsEntirely_ClosesThePort()
        {
            var transport = new ScriptedTransport();   // answers nothing, so V# never replies

            Assert.Throws<SamBaTransportException>(() => new SambaMonitor(transport).Connect());

            // A Windows serial port stays exclusively claimed until closed, so leaving it open on a
            // failed connect would block the next attempt — including one through a fresh device.
            Assert.False(transport.IsOpen);
            Assert.Equal(2, transport.OpenCount);   // the handshake was retried once before giving up
        }

        [Fact]
        public void Connect_ClearsTheStalePipeWithoutReadingOnSpec()
        {
            var transport = new ScriptedTransport();
            transport.AddResponse("N#", "\n\r");
            transport.AddResponse("V#", PlainVersion + "\n\r");
            var monitor = new SambaMonitor(transport);

            monitor.Connect();

            // Two clears of two purges each: one before the handshake for a previous session's
            // leftovers, one after N# for the mode-switch echo. Each is purge-settle-purge, because
            // the first purge cannot reach a byte that is still in flight.
            Assert.Equal(4, transport.PurgeCount);

            // And exactly one read in the whole handshake — the version. Both discarded replies used
            // to be read instead, which cost a connect up to two full read timeouts: one on any quiet
            // port, and one more on every reconnect, a monitor already in binary mode sending no echo.
            Assert.Equal(1, transport.ReadCount);
        }

        [Fact]
        public void Connect_WhenTheMonitorIsAlreadyInBinaryMode_DoesNotWaitForAnEchoItWillNotSend()
        {
            // Binary mode survives closing the port, so this is every connect after the first: N#
            // answers nothing at all. Reading for that reply is what used to make a reconnect cost a
            // full timeout more than a first connect.
            var transport = new ScriptedTransport();
            transport.AddResponse("V#", PlainVersion + "\n\r");
            var monitor = new SambaMonitor(transport);

            monitor.Connect();

            Assert.Equal(PlainVersion, monitor.Version);
            Assert.Equal(1, transport.ReadCount);
            Assert.Equal(1, transport.OpenCount);   // no handshake retry: nothing failed
        }

        [Fact]
        public void Connect_WhenTheModeSwitchEchoArrivesLate_StepsOverIt()
        {
            // The echo is normally purged, but the purge waits a fixed settle window and firmware
            // latency is not something this library can bound — so a slow echo can land in front of
            // the version reply. The fake has no clock, so "late" is expressed by leaving the echo
            // attached to the front of the V# reply, which is what the monitor would see.
            // Scoring that as a zero-length version would fail the connect for good, since a retry
            // meets the same latency.
            var transport = new ScriptedTransport();
            transport.AddResponse("V#", "\n\r" + ArduinoVersion + "\n\r");
            var monitor = new SambaMonitor(transport);

            monitor.Connect();

            Assert.Equal(ArduinoVersion, monitor.Version);
            Assert.Equal(SambaCapabilities.ArduinoReadChunkLimit, monitor.Capabilities.ReadChunkLimit);
        }

        [Theory]
        // Nothing printable in it at all — leftover binary. This is the case that used to connect
        // silently with Version = "" and baseline capabilities.
        [InlineData("\0")]
        // A printable run too short to be a version; "v1.1" is the shortest one a monitor prints.
        [InlineData("ab\0cd")]
        // Long and printable but with no digit in it — leftover bytes that landed in a string table.
        [InlineData("just some words\n\r")]
        // A leading terminator is stepped over, and what follows is still not a version.
        [InlineData("\n\rleftover block-read bytes\n\r")]
        public void Connect_WhenTheVersionReplyIsLeftoverData_FailsInsteadOfConnecting(string reply)
        {
            // Accepting it is worse than failing: baseline capabilities drop the 63-byte read cap an
            // Arduino bootloader needs (corrupting every block read afterwards), and the rest of the
            // leftover data misaligns each reply after it, so the chip probe reads a garbage id. A
            // failed connect, by contrast, is cheap for a caller to repeat.
            var transport = new ScriptedTransport();
            transport.AddResponse("N#", "\n\r");
            transport.AddResponse("V#", reply);
            var monitor = new SambaMonitor(transport);

            Assert.Throws<SamBaTransportException>(() => monitor.Connect());

            Assert.False(monitor.IsConnected);
            Assert.False(transport.IsOpen);
            // Closing the port is what stops the driver pulling the remainder of an interrupted
            // transfer, so the retry gets a genuinely fresh pipe rather than the leftovers.
            Assert.Equal(2, transport.OpenCount);
        }

        [Fact]
        public void Connect_AcceptsTheShortestVersionStringAMonitorPrints()
        {
            var (monitor, _) = Connected("v1.1");

            Assert.Equal("v1.1", monitor.Version);
        }

        [Fact]
        public void Connect_VersionArrivingInPieces_StillSeesTheArduinoBanner()
        {
            // One read returns one USB packet. The banner sits at the end of the version string, so a
            // reply taken from a single read would be cut at the packet boundary and parse as a plain
            // ROM monitor — dropping the 63-byte read cap those bootloaders need, which corrupts
            // every block read afterwards instead of failing outright.
            var transport = new ScriptedTransport { MaxBytesPerRead = 8 };
            transport.AddResponse("N#", "\n\r");
            transport.AddResponse("V#", ArduinoVersion + "\n\r");
            var monitor = new SambaMonitor(transport);

            monitor.Connect();

            Assert.Equal(ArduinoVersion, monitor.Version);
            Assert.True(monitor.Capabilities.HasChipEraseCommand);
            Assert.Equal(SambaCapabilities.ArduinoReadChunkLimit, monitor.Capabilities.ReadChunkLimit);
        }

        [Theory]
        [InlineData("plain v1.1", false, null)]            // no banner -> no cap at all
        [InlineData("v2.0 [Arduino:X] ...", true, 63)]
        [InlineData("v2.0 [Arduino:YZ] ...", false, 63)]   // Y (write-buffer) and Z (checksum) are ignored
        [InlineData("v2.0 [Arduino:] ...", false, 63)]
        public void ParseArduinoExtensions_ReadsExtensionFlags(
            string version, bool chipErase, int? chunkLimit)
        {
            var caps = SambaMonitor.ParseArduinoExtensions(version);

            Assert.Equal(chipErase, caps.HasChipEraseCommand);
            Assert.Equal(chunkLimit, caps.ReadChunkLimit);
        }

        [Fact]
        public void ReadWord_FormatsCommand_ParsesLittleEndian()
        {
            var (monitor, transport) = Connected();
            transport.AddResponse("w400E0740,4#", 0x78, 0x56, 0x34, 0x12);

            uint value = monitor.ReadWord(0x400E0740);

            Assert.Equal("w400E0740,4#", transport.WrittenText);
            Assert.Equal(0x12345678u, value);
        }

        [Fact]
        public void WriteWord_FormatsCommand()
        {
            var (monitor, transport) = Connected();

            monitor.WriteWord(0x400E0A04, 0x5A000003);

            Assert.Equal("W400E0A04,5A000003#", transport.WrittenText);
        }

        [Fact]
        public void ReadByte_And_WriteByte_FormatCommands()
        {
            var (monitor, transport) = Connected();
            transport.AddResponse("o20001000,4#", 0xAB);

            byte value = monitor.ReadByte(0x20001000);
            monitor.WriteByte(0x20001001, 0xCD);

            Assert.Equal(0xAB, value);
            Assert.Equal("o20001000,4#|O20001001,CD#", transport.WrittenText);
        }

        [Fact]
        public void Read_PowerOfTwoOver32Bytes_SplitsFirstByte()
        {
            var (monitor, transport) = Connected();  // no Arduino ext -> no chunk limit
            transport.AddResponse("o00001000,4#", 0x11);
            transport.AddResponse("R00001001,0000003F#", Enumerable.Range(1, 63).Select(i => (byte)i).ToArray());

            var buffer = new byte[64];
            monitor.Read(0x00001000, buffer, 0, 64);

            Assert.Equal("o00001000,4#|R00001001,0000003F#", transport.WrittenText);
            Assert.Equal(0x11, buffer[0]);
            Assert.Equal(1, buffer[1]);
            Assert.Equal(63, buffer[63]);
        }

        [Fact]
        public void Read_NonPowerOfTwo_SingleChunk()
        {
            var (monitor, transport) = Connected();
            transport.AddResponse("R00001000,00000064#", new byte[100]);

            var buffer = new byte[100];
            monitor.Read(0x00001000, buffer, 0, 100);

            Assert.Equal("R00001000,00000064#", transport.WrittenText);
        }

        [Fact]
        public void Read_WithArduinoExtension_ChunksTo63Bytes()
        {
            var (monitor, transport) = Connected(ArduinoVersion);
            transport.AddResponse("R00002000,0000003F#", new byte[63]);
            transport.AddResponse("R0000203F,0000003F#", new byte[63]);
            transport.AddResponse("R0000207E,00000002#", new byte[2]);

            var buffer = new byte[128];
            monitor.Read(0x00002000, buffer, 0, 128);

            Assert.Equal("R00002000,0000003F#|R0000203F,0000003F#|R0000207E,00000002#", transport.WrittenText);
        }

        [Fact]
        public void Read_MultipleOf64NotPowerOfTwo_SplitsFirstByte()
        {
            // 192 = 64*3: a multiple of the USB bulk max packet but not a power of two. The old
            // power-of-two check missed this; it must still be trimmed to avoid the hang.
            var (monitor, transport) = Connected();
            transport.AddResponse("o00001000,4#", 0x11);
            transport.AddResponse("R00001001,000000BF#", new byte[191]);

            var buffer = new byte[192];
            monitor.Read(0x00001000, buffer, 0, 192);

            // First byte peeled with o#, remaining 191 (0xBF, not a multiple of 64) via one R#.
            Assert.Equal("o00001000,4#|R00001001,000000BF#", transport.WrittenText);
            Assert.Equal(0x11, buffer[0]);
        }

        [Fact]
        public void Read_LongerThanTheDefaultChunk_SplitsIntoBoundedChunks()
        {
            // An uncapped monitor used to get one R# for however much the caller asked for, waited out
            // on a single fixed budget — so a whole-image verify travelled as one transfer and could
            // not finish inside it. Every R# must now be bounded whether the monitor caps or not.
            var (monitor, transport) = Connected();   // no Arduino ext -> only our own cap applies
            int chunk = SambaMonitor.DefaultBlockChunk;
            transport.AddResponse($"R00400000,{chunk:X8}#", new byte[chunk]);
            transport.AddResponse($"R{0x00400000 + chunk:X8},00000064#", new byte[100]);

            var buffer = new byte[chunk + 100];
            monitor.Read(0x00400000, buffer, 0, buffer.Length);

            // Two R# commands and nothing else: no byte-peel round-trip, because a full chunk is not
            // a multiple of the bulk max packet.
            Assert.Equal(
                $"R00400000,{chunk:X8}#|R{0x00400000 + chunk:X8},00000064#",
                transport.WrittenText);
        }

        [Fact]
        public void DefaultBlockChunk_IsNotAMultipleOfTheBulkMaxPacket()
        {
            // A chunk that is an exact multiple of 64 trips the byte-peel path on every full chunk,
            // paying an extra o# round-trip each time; one byte below the round number avoids it.
            Assert.NotEqual(0, SambaMonitor.DefaultBlockChunk % 64);
        }

        [Fact]
        public void Write_SendsCommandThenPayloadAsSeparateWrites()
        {
            var (monitor, transport) = Connected();
            byte[] payload = { 1, 2, 3, 4, 5, 6, 7, 8 };

            monitor.Write(0x20001000, payload, 0, payload.Length);

            Assert.Equal(2, transport.Writes.Count);
            Assert.Equal("S20001000,00000008#", Encoding.ASCII.GetString(transport.Writes[0]));
            Assert.Equal(payload, transport.Writes[1]);
        }

        [Fact]
        public void Write_LongerThanTheDefaultChunk_SplitsIntoBoundedStreams()
        {
            // Same defect the read had: an unbounded payload on a budget sized for one command. Only
            // reachable through WriteMemory to RAM, but SAM parts carry up to 384 KB of it.
            var (monitor, transport) = Connected();
            int chunk = SambaMonitor.DefaultBlockChunk;
            var payload = new byte[chunk + 100];

            monitor.Write(0x20000000, payload, 0, payload.Length);

            // Command and payload alternate, one pair per chunk, and no length is trimmed the way a
            // read's is — the monitor is told the count, so it never infers the end from a packet.
            Assert.Equal(4, transport.Writes.Count);
            Assert.Equal($"S20000000,{chunk:X8}#", Encoding.ASCII.GetString(transport.Writes[0]));
            Assert.Equal(chunk, transport.Writes[1].Length);
            Assert.Equal($"S{0x20000000 + chunk:X8},00000064#", Encoding.ASCII.GetString(transport.Writes[2]));
            Assert.Equal(100, transport.Writes[3].Length);
        }

        [Fact]
        public void Go_FormatsCommand()
        {
            var (monitor, transport) = Connected();

            monitor.Go(0x20000001);

            Assert.Equal("G20000001#", transport.WrittenText);
        }

        [Fact]
        public void ChipErase_FormatsCommand_ChecksReply()
        {
            var (monitor, transport) = Connected(ArduinoVersion);
            transport.AddResponse("X00002000#", "X\n\r");

            monitor.ChipErase(0x00002000);

            Assert.Equal("X00002000#", transport.WrittenText);
        }

        [Fact]
        public void ChipErase_WaitsTheReplyOutWithTheChipEraseBudget()
        {
            var (monitor, transport) = Connected(ArduinoVersion);
            transport.AddResponse("X00002000#", "X\n\r");

            monitor.ChipErase(0x00002000);

            // X# erases the whole chip in one command, which runs to tens of seconds on the
            // larger parts — the ordinary long-command budget abandons it part-way through.
            Assert.Equal(SambaMonitor.DefaultChipEraseTimeout, transport.LastReadTimeout);
        }

        [Fact]
        public void ChipErase_HonoursACustomChipEraseTimeout()
        {
            var (monitor, transport) = Connected(ArduinoVersion);
            transport.AddResponse("X00002000#", "X\n\r");
            monitor.ChipEraseTimeout = TimeSpan.FromMinutes(5);

            monitor.ChipErase(0x00002000);

            Assert.Equal(TimeSpan.FromMinutes(5), transport.LastReadTimeout);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ChipEraseTimeout_RejectsNonPositive_AndKeepsTheOldValue(int seconds)
        {
            var (monitor, _) = Connected();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => monitor.ChipEraseTimeout = TimeSpan.FromSeconds(seconds));
            Assert.Equal(SambaMonitor.DefaultChipEraseTimeout, monitor.ChipEraseTimeout);
        }

        [Fact]
        public void ChipErase_WithoutCapability_Throws()
        {
            var (monitor, _) = Connected();  // plain monitor, no X#

            Assert.Throws<SamBaTransportException>(() => monitor.ChipErase(0));
        }

        [Fact]
        public void WriteViaWords_ProducesTheSameTextAsARunOfWriteWords()
        {
            var (monitor, transport) = Connected();
            byte[] data = { 0x03, 0x00, 0x00, 0x5A, 0x0A, 0x00, 0x00, 0x00 };

            monitor.WriteViaWords(0x400E0A04, data, 0, data.Length);

            // Uppercase, zero-padded to 8 digits, little-endian values, and concatenated with
            // nothing between commands: the batch must be indistinguishable from a run of
            // individual WriteWord calls (pinned by WriteWord_FormatsCommand above) — and it all
            // travels as one transport write.
            Assert.Single(transport.Writes);
            Assert.Equal(
                "W400E0A04,5A000003#W400E0A08,0000000A#", Encoding.ASCII.GetString(transport.Writes[0]));
        }

        /// <summary>
        /// ReadViaWords needs replies computed from the commands it batches, and WriteViaWords'
        /// effect is only visible in a memory it lands in — the scripted transport can do
        /// neither, so these tests run against the memory-backed fake instead.
        /// </summary>
        private static (SambaMonitor monitor, FakeSamDevice fake) ConnectedFake()
        {
            var fake = new FakeSamDevice();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            fake.WriteSizes.Clear();
            return (monitor, fake);
        }

        [Fact]
        public void ReadViaWords_Aligned_PipelinesOneBatchAndSkipsTheBlockCommand()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] expected = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray();
            fake.SetBytes(0x00080000, expected);

            var buffer = new byte[expected.Length];
            monitor.ReadViaWords(0x00080000, buffer, 0, buffer.Length);

            Assert.Equal(expected, buffer);
            // 16 w# commands in one transport write; no R# anywhere.
            Assert.Single(fake.WriteSizes);
            Assert.Equal(16 * SambaMonitor.ReadWordCommandLength, fake.WriteSizes[0]);
            Assert.Empty(fake.ReadCommands);
        }

        [Fact]
        public void ReadViaWords_UnalignedHeadAndTail_ReturnsExactlyTheAskedBytes()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] pattern = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
            fake.SetBytes(0x00080000, pattern);

            // Bytes 3..12: a 1-byte head off the first word, two whole words, a 1-byte tail.
            var buffer = new byte[10];
            monitor.ReadViaWords(0x00080003, buffer, 0, buffer.Length);

            Assert.Equal(pattern.Skip(3).Take(10).ToArray(), buffer);
        }

        [Fact]
        public void ReadViaWords_SafeMode_PaysOneRoundTripPerWord()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] expected = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
            fake.SetBytes(0x00080000, expected);
            monitor.SafeMode = true;

            var buffer = new byte[expected.Length];
            monitor.ReadViaWords(0x00080000, buffer, 0, buffer.Length);

            Assert.Equal(expected, buffer);
            // Four words, four separate command writes — nothing travels back-to-back.
            Assert.Equal(4, fake.WriteSizes.Count);
            Assert.All(fake.WriteSizes, size => Assert.Equal(SambaMonitor.ReadWordCommandLength, size));
        }

        [Fact]
        public void ReadViaWords_LargerThanOneBatch_SplitsIntoBoundedBatches()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] expected = Enumerable.Range(0, 130 * 4).Select(i => (byte)(i * 13)).ToArray();
            fake.SetBytes(0x00080000, expected);

            var buffer = new byte[expected.Length];
            monitor.ReadViaWords(0x00080000, buffer, 0, buffer.Length);

            Assert.Equal(expected, buffer);
            // 130 words: a full 128-word batch, then the 2 left over — each one transport write,
            // so a stalled batch is bounded by what one reply wait covers.
            Assert.Equal(2, fake.WriteSizes.Count);
            Assert.Equal(128 * SambaMonitor.ReadWordCommandLength, fake.WriteSizes[0]);
            Assert.Equal(2 * SambaMonitor.ReadWordCommandLength, fake.WriteSizes[1]);
        }

        [Fact]
        public void ReadViaWords_RejectsTheArgumentsReadRejects()
        {
            var (monitor, _) = ConnectedFake();

            Assert.Throws<ArgumentNullException>(() => monitor.ReadViaWords(0, null!, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.ReadViaWords(0, new byte[4], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.ReadViaWords(0, new byte[4], 0, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.ReadViaWords(0, new byte[4], 0, -1));
        }

        [Fact]
        public void WriteViaWords_RoundTripsThroughTheFakeAsOneBatch()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] data = Enumerable.Range(0, 64).Select(i => (byte)(i * 5)).ToArray();

            monitor.WriteViaWords(0x20001000, data, 0, data.Length);

            Assert.Equal(data, fake.GetBytes(0x20001000, data.Length));
            // 16 W# commands in one transport write.
            Assert.Single(fake.WriteSizes);
            Assert.Equal(16 * SambaMonitor.WriteWordCommandLength, fake.WriteSizes[0]);
        }

        [Fact]
        public void WriteViaWords_LargerThanOneBatch_SplitsIntoBoundedBatches()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] data = Enumerable.Range(0, 130 * 4).Select(i => (byte)(i * 7)).ToArray();

            monitor.WriteViaWords(0x20001000, data, 0, data.Length);

            Assert.Equal(data, fake.GetBytes(0x20001000, data.Length));
            // 130 words: a full 128-word batch, then the 2 left over, mirroring the read side —
            // so one transport write never outgrows the fixed budget it is waited out on.
            Assert.Equal(2, fake.WriteSizes.Count);
            Assert.Equal(128 * SambaMonitor.WriteWordCommandLength, fake.WriteSizes[0]);
            Assert.Equal(2 * SambaMonitor.WriteWordCommandLength, fake.WriteSizes[1]);
        }

        [Fact]
        public void WriteViaWords_SafeMode_PaysOneWritePerWord()
        {
            var (monitor, fake) = ConnectedFake();
            byte[] data = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
            monitor.SafeMode = true;

            monitor.WriteViaWords(0x20001000, data, 0, data.Length);

            Assert.Equal(data, fake.GetBytes(0x20001000, data.Length));
            // Four words, four separate command writes — nothing travels back-to-back.
            Assert.Equal(4, fake.WriteSizes.Count);
            Assert.All(fake.WriteSizes, size => Assert.Equal(SambaMonitor.WriteWordCommandLength, size));
        }

        [Fact]
        public void WriteViaWords_RejectsWhatWordWritesCannotServe()
        {
            var (monitor, _) = ConnectedFake();

            Assert.Throws<ArgumentNullException>(() => monitor.WriteViaWords(0, null!, 0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.WriteViaWords(0, new byte[8], -1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.WriteViaWords(0, new byte[8], 0, 12));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.WriteViaWords(0, new byte[8], 0, -1));
            // Unlike ReadViaWords, misalignment is refused rather than served from containing
            // words: a word write cannot cover neighbouring bytes without clobbering them.
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.WriteViaWords(2, new byte[8], 0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => monitor.WriteViaWords(0, new byte[8], 0, 6));
        }
    }
}

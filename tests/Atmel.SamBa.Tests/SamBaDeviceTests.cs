using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Protocol;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    public class SamBaDeviceTests
    {
        private const uint Sam3xRegs = 0x400E0A00;
        private const uint Sam3xFlash = 0x80000;

        private static (SamBaDevice device, FakeSamDevice fake) NewSam3x8eDevice()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);
            fake.SetWord(0x4, 0x00800125);
            fake.SetWord(0x400E0740, 0x285E0A60);
            fake.SetWord(Sam3xRegs + 0x008, 1);
            fake.SetWord(Sam3xRegs + 0x208, 1);
            return (new SamBaDevice(fake), fake);
        }

        /// <summary>Fake wired as an ATSAMD21x18: NVMCTRL D2x, whose monitor serves block reads.</summary>
        private static (SamBaDevice device, FakeSamDevice fake) NewSamD21Device()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);       // Cortex-M0+ CPUID
            fake.SetWord(0x41002018, 0x10010005);       // DSU DID
            fake.SetWord(0x41004014, 1);                // NVMCTRL INTFLAG READY
            return (new SamBaDevice(fake), fake);
        }

        /// <summary>
        /// A Cortex-M4 part that identifies itself but matches no device-table row, so it is placed
        /// by family fallback and reports <see cref="SamBaChipFamily.Unknown"/>. Unlike
        /// <see cref="NewLegacyFallbackIdentifiedDevice"/>, this one's initial probe did read a CPUID
        /// (it takes the Cortex-M branch), so <c>DeviceResetter</c> still has a route for it — the
        /// core's own architectural AIRCR — even though its family could not be named. Its geometry
        /// comes from the queued flash descriptor.
        /// </summary>
        private static (SamBaDevice device, FakeSamDevice fake) NewFallbackIdentifiedDevice()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);           // Cortex-M4
            fake.SetWord(0x4, 0x00800125);                  // reset handler in the SAM-BA ROM
            fake.SetWord(0x400E0740, 0x28AB0000);           // unknown CHIPID in a known family
            fake.SetWord(Sam3xRegs + 0x008, 1);             // one EEFC, ready
            fake.EnqueueWordRead(Sam3xRegs + 0x00C, 0x00112233, 0x80000, 512, 1, 0x80000, 128);
            return (new SamBaDevice(fake), fake);
        }

        /// <summary>
        /// A legacy AT91SAM7S256-shaped part whose CHIPID (0x270B09A0, EPROC 5) does not match
        /// AT91SAM7S256's own row (0x270B0940, EPROC 2) or any other, so it is placed by family
        /// fallback and reports <see cref="SamBaChipFamily.Unknown"/> — genuinely so, this time: the
        /// legacy CHIPID branch never reads a CPUID, so <c>DeviceResetter</c> has no route for it at
        /// all. Opened with <see cref="SamBaChipIdentificationMode.ChipId"/> forced, since nothing at
        /// address 0 marks this as the legacy branch under <c>Auto</c>.
        /// </summary>
        private static (SamBaDevice device, FakeSamDevice fake) NewLegacyFallbackIdentifiedDevice()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xFFFFF240, 0x270B09A0);           // CHIPID: AT91SAM7S256's shape, EPROC 5
            fake.SetWord(0xFFFFFF68, 1);                    // EFC0 FSR ready
            return (new SamBaDevice(fake), fake);
        }

        [Fact]
        public void Open_IdentifiesChip_PopulatesInfo()
        {
            var (device, _) = NewSam3x8eDevice();

            device.Open();

            Assert.True(device.IsOpen);
            Assert.NotNull(device.ChipInfo);
            Assert.Equal("ATSAM3X8", device.ChipInfo.Name);
            Assert.Equal(SamBaChipFamily.Sam3X, device.ChipInfo.Family);
            Assert.Equal(512 * 1024, device.ChipInfo.FlashSize);
            Assert.StartsWith("v1.1", device.MonitorVersion);
            Assert.Contains("ATSAM3X8", device.DisplayName);
        }

        [Fact]
        public void Open_GeometryMismatch_RaisesTheEventAndKeepsTheTable()
        {
            // The part's own flash descriptor contradicts the table row. The event carries both
            // accounts so a handler can report which row to re-check; everything the device runs
            // on — ChipInfo included — stays the table's, because only hardware in hand can say
            // which source is wrong.
            var (device, fake) = NewSam3x8eDevice();
            uint frr0 = Sam3xRegs + 0x00C;
            uint frr1 = Sam3xRegs + 0x20C;
            fake.EnqueueWordRead(frr0, 0x00112233, 0x40000, 512, 1, 0x40000, 16);   // GETD reply
            fake.EnqueueWordRead(frr1, 0x00112233, 0x40000, 512, 1, 0x40000, 16);

            SamBaGeometryMismatchEventArgs mismatch = null!;
            device.GeometryMismatchDetected += (_, args) => mismatch = args;
            var progress = new List<string>();
            device.ProgressChanged += (_, args) => progress.Add(args.Message);

            device.Open();

            Assert.NotNull(mismatch);
            Assert.Equal("ATSAM3X8", mismatch.ChipName);
            Assert.Equal(256, mismatch.TablePageSize);
            Assert.Equal(512, mismatch.DevicePageSize);
            Assert.Equal(2048, mismatch.TablePageCount);
            Assert.Equal(1024, mismatch.DevicePageCount);
            Assert.Equal(256, device.ChipInfo.PageSize);        // the table stands
            Assert.Contains(progress, m => m.Contains("differs from the device table"));
            Assert.Contains(progress, m => m.Contains("using the table"));
        }

        [Fact]
        public void Open_GeometryMismatch_WithDevicePrecedence_AdoptsDeviceAndReportsBothCorrectly()
        {
            // Same disagreement as Open_GeometryMismatch_RaisesTheEventAndKeepsTheTable, but with
            // Device precedence: ChipInfo should adopt the device's numbers, while the mismatch
            // report's "table" figures must still be the table's real numbers (256 B, 2048 pages) —
            // not whatever ChipInfo ends up holding after the adoption overwrites it.
            var (device, fake) = NewSam3x8eDevice();
            uint frr0 = Sam3xRegs + 0x00C;
            uint frr1 = Sam3xRegs + 0x20C;
            fake.EnqueueWordRead(frr0, 0x00112233, 0x40000, 512, 1, 0x40000, 16);   // GETD reply
            fake.EnqueueWordRead(frr1, 0x00112233, 0x40000, 512, 1, 0x40000, 16);

            SamBaGeometryMismatchEventArgs mismatch = null!;
            device.GeometryMismatchDetected += (_, args) => mismatch = args;
            var progress = new List<string>();
            device.ProgressChanged += (_, args) => progress.Add(args.Message);

            device.Open(SamBaChipIdentificationMode.Auto, SamBaGeometryPrecedence.Device);

            Assert.NotNull(mismatch);
            Assert.Equal(256, mismatch.TablePageSize);           // the table's real figure, unchanged
            Assert.Equal(512, mismatch.DevicePageSize);
            Assert.Equal(2048, mismatch.TablePageCount);
            Assert.Equal(1024, mismatch.DevicePageCount);
            Assert.Equal(512, device.ChipInfo.PageSize);         // the device's account won
            Assert.Contains(progress, m => m.Contains("differs from the device table"));
            Assert.Contains(progress, m => m.Contains("using the device-reported geometry"));
            Assert.DoesNotContain(progress, m => m.Contains("using the table"));
        }

        [Fact]
        public void Open_PublishesTheIdentificationWordsThatApply()
        {
            // A CHIPID part: the CIDR and the whole CPUID register are readable off the public info,
            // the DSU is null because it was never consulted, and the EXID is 0 rather than null
            // because it was read and this part carries none.
            var (device, _) = NewSam3x8eDevice();

            device.Open();

            Assert.Equal(0x285E0A60u, device.ChipInfo.ChipId);
            Assert.Equal(0x410FC240u, device.ChipInfo.CpuId);
            Assert.Equal(0u, device.ChipInfo.ExtendedChipId);
            Assert.Null(device.ChipInfo.DeviceId);

            // ToString appends what applies and omits what does not, so it can be pasted whole into a
            // report about a part identified wrongly.
            string text = device.ChipInfo.ToString();
            Assert.Contains("CHIPID=0x285E0A60", text);
            Assert.Contains("CPUID=0x410FC240", text);
            Assert.DoesNotContain("DSU DID", text);
        }

        [Fact]
        public void Open_LegacyPart_ReadsTheCidrAloneAndLeavesTheExidNull()
        {
            // The counterpart of the Cortex-M case above, and the one place a read CIDR does not imply
            // a read EXID: the legacy probe takes the CIDR and stops, because no SAM7 or SAM9XE row is
            // told apart by the extension word. So ChipId carries a value while ExtendedChipId stays
            // null — null meaning "never read", never "read as zero". CpuId is null for the same
            // reason: an ARM7 has no CPUID register to consult.
            var fake = new FakeSamDevice();
            fake.SetWord(0x0, 0xEA000000);          // ARM7 reset-vector branch -> legacy probe path
            fake.SetWord(0xFFFFF240, 0x270B0940);   // DBGU CHIPID: AT91SAM7S256 rev B/C
            fake.SetWord(0xFFFFFF68, 1);            // MC_FSR ready
            var device = new SamBaDevice(fake);

            device.Open();

            Assert.Equal("AT91SAM7S256", device.ChipInfo.Name);
            Assert.Equal(0x270B0940u, device.ChipInfo.ChipId);
            Assert.Null(device.ChipInfo.ExtendedChipId);
            Assert.Null(device.ChipInfo.CpuId);
            Assert.Null(device.ChipInfo.DeviceId);

            // An absent word is omitted from the summary rather than printed as zero.
            string text = device.ChipInfo.ToString();
            Assert.Contains("CHIPID=0x270B0940", text);
            Assert.DoesNotContain("EXID", text);
            Assert.DoesNotContain("CPUID", text);
        }

        [Fact]
        public void DisplayName_BeforeOpen_DoesNotClaimSamBaWithoutAtmelVidPid()
        {
            // No PnP metadata (fake transport): unknown VID/PID and no friendly name, so the
            // label must not assert "SAM-BA device" for an unconfirmed port.
            var (device, _) = NewSam3x8eDevice();

            Assert.StartsWith("Serial device on ", device.DisplayName);
            Assert.DoesNotContain("SAM-BA device", device.DisplayName);
        }

        [Fact]
        public void Open_EmitsConnectAndIdentifyProgress()
        {
            var (device, _) = NewSam3x8eDevice();
            var stages = new List<string>();
            var messages = new List<string>();
            device.ProgressChanged += (_, e) =>
            {
                stages.Add(e.Stage);
                messages.Add(e.Message);
            };

            device.Open();

            Assert.Contains("Connecting", stages);
            Assert.Contains("Identifying", stages);
            Assert.Contains(messages, m => m == "Connecting to device");
            Assert.Contains(messages, m => m == "Chip identified: ATSAM3X8");
        }

        [Fact]
        public void Operations_BeforeOpen_Throw()
        {
            var (device, _) = NewSam3x8eDevice();

            Assert.Throws<SamBaDeviceNotOpenException>(() => device.ReadWord(0));
            Assert.Throws<SamBaDeviceNotOpenException>(() => device.EraseAllFlash());
            Assert.Throws<SamBaDeviceNotOpenException>(() => device.UpdateFirmware(new byte[4]));
        }

        [Fact]
        public void NotOpenException_NamesTheOperationItRefused()
        {
            // Carried as a property rather than only inside the message, so a caller logging the
            // failure does not have to parse one — the shape the other three exceptions already have.
            var (device, _) = NewSam3x8eDevice();

            var ex = Assert.Throws<SamBaDeviceNotOpenException>(() => device.GetUniqueId());

            Assert.Equal(nameof(SamBaDevice.GetUniqueId), ex.Operation);
        }

        [Fact]
        public void UpdateFirmware_FullFlow_WritesVerifiesAndSetsBootBit()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            var stages = new List<string>();
            var messages = new List<string>();
            device.ProgressChanged += (_, e) => { stages.Add(e.Stage); messages.Add(e.Message); };

            byte[] firmware = Enumerable.Range(0, 700).Select(i => (byte)(i ^ 0x5A)).ToArray();
            // 700 B over 256 B blocks: final block 2 is padded. BulkErase is opt-in (default false),
            // so it must be requested explicitly to exercise the full-erase path this test checks.
            // Reset defaults true, so it must be turned off explicitly to check IsOpen below.
            device.UpdateFirmware(firmware, new SamBaUpdateOptions { BulkErase = true, Reset = false });

            // Image landed in flash.
            Assert.Equal(firmware, fake.GetBytes(Sam3xFlash, firmware.Length));

            // Full erase ran (EA on both planes).
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000005u), fake.WordWrites);

            // Boot-to-flash GPNVM bit set (EEFC SGPB, bit index 1 — no brownout on SAM3X).
            Assert.Contains((Sam3xRegs + 0x004, 0x5A00010Bu), fake.WordWrites);

            Assert.Contains("Erasing", stages);
            Assert.Contains("Writing", stages);
            Assert.Contains("Verifying", stages);
            Assert.Contains("Options", stages);

            // The short final block is padded with 0xFF, and that is announced.
            Assert.Contains(messages, m => m.Contains("Padding block 2"));
            Assert.True(device.IsOpen);  // no reset requested
        }

        [Fact]
        public void UpdateFirmware_ImageTooLongForOffset_ThrowsBeforeErasing()
        {
            // The fit check must precede the erase. Reaching it only on the way into the write would
            // blank the part first, leaving the old firmware gone and the new one unwritten.
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            // Exactly fills flash at offset 0, so only the offset pushes it past the end. 0x2000 is
            // a multiple of the 2 KB erase group, so the erase would have been accepted and run.
            byte[] firmware = new byte[512 * 1024];

            Assert.Throws<ArgumentOutOfRangeException>(
                () => device.UpdateFirmware(firmware, new SamBaUpdateOptions { Offset = 0x2000 }));

            // No erase-all reached the controller (EEFC EA on plane 0).
            Assert.DoesNotContain((Sam3xRegs + 0x004, 0x5A000005u), fake.WordWrites);
        }

        [Fact]
        public void UpdateFirmware_NoEraseNoVerifyNoBoot_SkipsThose()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            byte[] firmware = new byte[256];
            device.UpdateFirmware(firmware, new SamBaUpdateOptions
            {
                BulkErase = false,
                Verify = false,
                SetBootToFlash = false,
            });

            // No EA, no SGPB; EWP (erase-auto) commit instead.
            Assert.DoesNotContain((Sam3xRegs + 0x004, 0x5A000005u), fake.WordWrites);
            Assert.DoesNotContain((Sam3xRegs + 0x004, 0x5A00010Bu), fake.WordWrites);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000003u), fake.WordWrites);
        }

        [Fact]
        public void UpdateFirmware_SubBlockImageNoErase_AnnouncesReadModifyWrite()
        {
            var (device, _) = NewSam3x8eDevice();
            device.Open();
            var messages = new List<string>();
            device.ProgressChanged += (_, e) => messages.Add(e.Message);

            // One byte, no erase => partial block merged into existing flash (read-modify-write).
            device.UpdateFirmware(new byte[1], new SamBaUpdateOptions { BulkErase = false, Verify = false });

            Assert.Contains(messages, m => m.Contains("Read-modify-write on block 0"));
        }

        [Fact]
        public void UpdateFirmware_UnlocksLockedRegions_BeforeWriting()
        {
            var (device, fake) = NewSam3x8eDevice();
            fake.SetWord(Sam3xRegs + 0x00C, 1);   // EEFC0 FRR reports lock region 0 locked
            device.Open();
            var stages = new List<string>();
            device.ProgressChanged += (_, e) => stages.Add(e.Stage);

            device.UpdateFirmware(
                new byte[128], new SamBaUpdateOptions { Verify = false, UnlockBeforeWrite = true });

            Assert.Contains("Unlock", stages);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000009u), fake.WordWrites);  // CLB region 0 (unlock)
        }

        [Fact]
        public void UpdateFirmware_UnlockDisabled_LeavesRegionsLocked()
        {
            var (device, fake) = NewSam3x8eDevice();
            fake.SetWord(Sam3xRegs + 0x00C, 1);   // region 0 locked
            device.Open();

            device.UpdateFirmware(
                new byte[128], new SamBaUpdateOptions { Verify = false, UnlockBeforeWrite = false });

            Assert.DoesNotContain((Sam3xRegs + 0x004, 0x5A000009u), fake.WordWrites);  // no CLB issued
        }

        [Fact]
        public void UpdateFirmware_NoLockedRegions_SkipsUnlockStage()
        {
            var (device, _) = NewSam3x8eDevice();
            device.Open();
            var stages = new List<string>();
            device.ProgressChanged += (_, e) => stages.Add(e.Stage);

            device.UpdateFirmware(new byte[128], new SamBaUpdateOptions { Verify = false });

            Assert.DoesNotContain("Unlock", stages);
        }

        [Fact]
        public void UpdateFirmware_Lock_LocksOnlyRegionsCoveredByImage()
        {
            // ATSAM3X8: 512 KB over 32 lock regions, so 16 KB and 64 pages per region. A 20 KB image
            // at offset 0 reaches into the second region and no further; the other 30 stay as they
            // were, because nothing the update wrote lives there.
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            // 20 KB is past the EEFC auto-erase-and-write limit (16 KB), so a full erase is required.
            device.UpdateFirmware(new byte[20 * 1024], new SamBaUpdateOptions
            {
                Verify = false,
                BulkErase = true,
                Lock = FlashLockScope.Written,
            });

            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8);
            Assert.Equal(2, slb.Count);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000008u), slb);  // region 0 -> page 0
            Assert.Contains((Sam3xRegs + 0x004, 0x5A004008u), slb);  // region 1 -> page 64
            Assert.Empty(fake.WordWrites.FindAll(  // plane 1 (regions 16..31) never asked to lock
                w => w.Address == Sam3xRegs + 0x204 && (w.Value & 0xFF) == 0x8));
        }

        [Fact]
        public void UpdateFirmware_LockWithOffset_LocksFromTheOffsetRegion()
        {
            // 0x8000 is region 2's first byte, and a multiple of the 2 KB erase group so the erase
            // accepts it. The bootloader the offset was left for sits in regions 0 and 1 and must
            // come out of this unlocked.
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            device.UpdateFirmware(new byte[256], new SamBaUpdateOptions
            {
                Verify = false,
                BulkErase = true,
                Offset = 0x8000,
                Lock = FlashLockScope.Written,
            });

            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8);
            Assert.Single(slb);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A008008u), slb);  // region 2 -> page 128
        }

        [Fact]
        public void UpdateFirmware_LockNotRequested_IssuesNoLockCommand()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            device.UpdateFirmware(new byte[256], new SamBaUpdateOptions { Verify = false });

            Assert.Empty(fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8));
        }

        [Fact]
        public void SetLockRegions_NullRegions_ThrowsArgumentNull()
        {
            var (device, _) = NewSam3x8eDevice();
            device.Open();

            var ex = Assert.Throws<ArgumentNullException>(() => device.SetLockRegions(null, true));
            Assert.Equal("regions", ex.ParamName);
        }

        [Fact]
        public void SetLockRegions_RegionOutOfRange_ThrowsWithIndexAndCount()
        {
            var (device, _) = NewSam3x8eDevice();  // ATSAM3X8: 32 lock regions
            device.Open();

            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => device.SetLockRegions(new[] { 32 }, true));

            Assert.Contains("32", ex.Message);
            Assert.Contains("0..31", ex.Message);
        }

        [Fact]
        public void SetLockRegions_EmptyList_TouchesNothing()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            fake.WordWrites.Clear();

            device.SetLockRegions(new int[0], true);

            Assert.Empty(fake.WordWrites);
        }

        [Fact]
        public void SetLockRegions_Duplicates_CollapseToOneCommand()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            fake.WordWrites.Clear();

            device.SetLockRegions(new[] { 3, 3 }, true);

            // The duplicate collapses: one read, one lock — region 3 -> page 192.
            Assert.Single(fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0xA));
            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8);
            Assert.Single(slb);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A00C008u), slb);
        }

        [Fact]
        public void SetLockRegions_Subset_NotOpen_Throws()
        {
            var (device, _) = NewSam3x8eDevice();

            Assert.Throws<SamBaDeviceNotOpenException>(() => device.SetLockRegions(new[] { 0 }, true));
        }

        [Fact]
        public void UpdateFirmware_WithReset_ResetsAndCloses()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            device.UpdateFirmware(new byte[128], new SamBaUpdateOptions { Verify = false, Reset = true });

            Assert.Contains((0x400E1A00u, 0xA500000Du), fake.WordWrites);  // RSTC key|PROCRST|PERRST|EXTRST
            Assert.False(device.IsOpen);
        }

        [Fact]
        public void ReadMemory_ReadsFlashContent()
        {
            // Reading flash is just a memory read at its absolute address (flash is memory-mapped).
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            byte[] pattern = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            fake.SetBytes(Sam3xFlash, pattern);

            byte[] read = device.ReadMemory(Sam3xFlash, 300);

            Assert.Equal(pattern, read);
        }

        [Fact]
        public void ReadMemory_EefcPart_UsesWordReadsEverywhere()
        {
            // These monitors return zeros for block reads of flash, and address 0 is the boot
            // memory remapping that same flash — so every read forks to w# words, not just the
            // flash window.
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            byte[] pattern = Enumerable.Range(0, 40).Select(i => (byte)(0x30 + i)).ToArray();
            fake.SetBytes(Sam3xFlash, pattern);
            fake.SetBytes(0x20001000, pattern);
            fake.SetBytes(0x0, pattern);

            Assert.Equal(pattern, device.ReadMemory(Sam3xFlash, pattern.Length));   // flash
            Assert.Equal(pattern, device.ReadMemory(0x20001000, pattern.Length));   // RAM
            Assert.Equal(pattern, device.ReadMemory(0x0, pattern.Length));          // boot alias

            Assert.Empty(fake.ReadCommands);
        }

        [Fact]
        public void ReadMemory_NvmPart_StillUsesTheBlockCommand()
        {
            var (device, fake) = NewSamD21Device();
            device.Open();
            byte[] pattern = Enumerable.Range(0, 40).Select(i => (byte)(0x60 + i)).ToArray();
            fake.SetBytes(0x0, pattern);   // NVMCTRL parts map flash at 0 and read it fine

            Assert.Equal(pattern, device.ReadMemory(0x0, pattern.Length));
            Assert.Single(fake.ReadCommands);
        }

        [Fact]
        public void ReadWriteMemory_Roundtrip()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
            device.WriteMemory(0x20001000, data);   // RAM -> raw write path

            Assert.Equal(data, device.ReadMemory(0x20001000, data.Length));
        }

        [Fact]
        public void WriteMemory_FlashAddress_ProgramsThroughController()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            byte[] data = Enumerable.Range(0, 256).Select(i => (byte)(i ^ 0x3C)).ToArray();
            device.WriteMemory(Sam3xFlash, data);   // start of flash -> flash path

            // Per-page auto-erase: an EWP (0x3) command committed page 0 through the EEFC0 FCR.
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000003u), fake.WordWrites);
            Assert.Equal(data, fake.GetBytes(Sam3xFlash, 256));
        }

        [Fact]
        public void WriteMemory_RamAddress_StaysRawWrite()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();

            device.WriteMemory(0x20001000, new byte[] { 1, 2, 3, 4 });

            // No flash-controller command issued (nothing written to the EEFC0 FCR) — beyond the
            // GETD (command 0x00) the open-time geometry probe writes there, which is a read of
            // the descriptor, not a flash operation.
            Assert.DoesNotContain(fake.WordWrites, w => w.Address == Sam3xRegs + 0x004 && (w.Value & 0xFF) != 0);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, fake.GetBytes(0x20001000, 4));
        }

        [Fact]
        public void WriteMemory_RangeStraddlingFlashBoundary_Throws()
        {
            var (device, _) = NewSam3x8eDevice();
            device.Open();

            // Starts 8 bytes below the flash base and runs into flash -> ambiguous.
            Assert.Throws<ArgumentException>(() => device.WriteMemory(Sam3xFlash - 8, new byte[32]));
        }

        [Fact]
        public void WriteMemory_UnalignedFlashStart_ReadModifyWritesFirstBlock()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            fake.SetBytes(Sam3xFlash, Enumerable.Repeat((byte)0xEE, 256).ToArray());

            var messages = new List<string>();
            device.ProgressChanged += (_, e) => messages.Add(e.Message);

            byte[] data = Enumerable.Repeat((byte)0x11, 10).ToArray();
            device.WriteMemory(Sam3xFlash + 4, data);   // offset 4 into block 0

            Assert.Equal(0xEE, fake.GetByte(Sam3xFlash + 0));    // preserved before the slice
            Assert.Equal(0x11, fake.GetByte(Sam3xFlash + 4));    // written
            Assert.Equal(0x11, fake.GetByte(Sam3xFlash + 13));   // written (bytes 4..13)
            Assert.Equal(0xEE, fake.GetByte(Sam3xFlash + 14));   // preserved after the slice

            // The non-aligned first block is read-modify-written, and that is announced.
            Assert.Contains(messages, m => m.Contains("Read-modify-write on block 0"));
        }

        [Fact]
        public void UpdateFirmware_PartialFinalPageNoErase_ReadModifyWritesLastPage()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            fake.SetBytes(Sam3xFlash, Enumerable.Repeat((byte)0xEE, 512).ToArray());

            // Page 0 fully covered, page 1 covered by only 4 bytes: the final page's read (after
            // page 0's write) goes through the unified ReadPage and must preserve the page tail.
            byte[] data = Enumerable.Repeat((byte)0x11, 260).ToArray();
            device.UpdateFirmware(data, new SamBaUpdateOptions { BulkErase = false, Verify = false });

            Assert.Equal(0x11, fake.GetByte(Sam3xFlash + 256));   // first byte of the partial final page: written
            Assert.Equal(0x11, fake.GetByte(Sam3xFlash + 259));   // last written byte
            Assert.Equal(0xEE, fake.GetByte(Sam3xFlash + 260));   // preserved (read-modify-write)
            Assert.Equal(0xEE, fake.GetByte(Sam3xFlash + 511));   // preserved to the page end
        }

        [Fact]
        public void ChipEraseTimeout_DefaultsToTheMonitorDefault_AndRoundTrips()
        {
            var (device, _) = NewSam3x8eDevice();

            Assert.Equal(SambaMonitor.DefaultChipEraseTimeout, device.ChipEraseTimeout);

            device.ChipEraseTimeout = TimeSpan.FromMinutes(2);

            Assert.Equal(TimeSpan.FromMinutes(2), device.ChipEraseTimeout);
            Assert.Throws<ArgumentOutOfRangeException>(() => device.ChipEraseTimeout = TimeSpan.Zero);
        }

        [Fact]
        public void SetBootSource_OnFixedSourceFamily_AcceptsFlashAndRefusesTheRom()
        {
            // A SAMD21 has no boot-mode bit and always boots flash. Asking for flash is not a request
            // to change anything, so it succeeds without touching the device; only the unreachable
            // source throws. GetBootSource is what says which is which, and CanSelectBootSource lets a
            // caller know the question is settled before asking it.
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);       // SAMD21
            fake.SetWord(0x41002018, 0x10010005);
            fake.SetWord(0x41004014, 1);
            var device = new SamBaDevice(fake);
            device.Open();
            Assert.False(device.ChipInfo.CanSelectBootSource);
            Assert.Equal(SamBaChipBootSource.Flash, device.GetBootSource());
            fake.WordWrites.Clear();

            device.SetBootSource(SamBaChipBootSource.Flash);

            Assert.Empty(fake.WordWrites);
            var ex = Assert.Throws<SamBaFlashCommandException>(
                () => device.SetBootSource(SamBaChipBootSource.Rom));
            Assert.True(ex.IsUnsupported);
        }

        [Fact]
        public void ChipInfo_CanSelectBootSource_TracksWhetherThePartHasABootModeBit()
        {
            // True where there is a bit to move, false where the source is fixed — and fixed always
            // means fixed at flash, which is the distinction the old CanBootFlash name lost.
            var (eefc, _) = NewSam3x8eDevice();       // ATSAM3X8: GPNVM1
            eefc.Open();

            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);       // SAMD21: no boot-mode bit at all
            fake.SetWord(0x41002018, 0x10010005);
            fake.SetWord(0x41004014, 1);
            var nvm = new SamBaDevice(fake);
            nvm.Open();

            Assert.True(eefc.ChipInfo.CanSelectBootSource);
            Assert.False(nvm.ChipInfo.CanSelectBootSource);
            Assert.Equal(SamBaChipBootSource.Flash, nvm.GetBootSource());
        }

        [Fact]
        public void Open_GeometryMismatch_WhenAHandlerThrows_StillOpens()
        {
            // The mismatch report is the one raise here that accompanies no work: the table's figures
            // are already in force and the device is already open. A logging handler that throws must
            // not turn that into a failed open — which it would, since the raise sits inside Open's
            // try and its catch closes the port.
            var (device, fake) = NewSam3x8eDevice();
            fake.EnqueueWordRead(Sam3xRegs + 0x00C, 0x00112233, 0x40000, 512, 1, 0x40000, 16);
            fake.EnqueueWordRead(Sam3xRegs + 0x20C, 0x00112233, 0x40000, 512, 1, 0x40000, 16);
            device.GeometryMismatchDetected += (_, _) => throw new InvalidOperationException("handler");

            device.Open();

            Assert.True(device.IsOpen);
            Assert.Equal(256, device.ChipInfo.PageSize);
        }

        [Fact]
        public void UpdateFirmware_OnFixedSourceFamily_SkipsTheBootStepSilently()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);       // SAMD21
            fake.SetWord(0x41002018, 0x10010005);
            fake.SetWord(0x41004014, 1);
            var device = new SamBaDevice(fake);
            device.Open();

            // Default options request SetBootToFlash — must not throw on SAMD21. A whole 256-byte row,
            // because without a bulk erase the write auto-erases and NVMCTRL erases a row at a time.
            device.UpdateFirmware(new byte[256], new SamBaUpdateOptions { BulkErase = false, Verify = true });

            Assert.Equal(new byte[256], fake.GetBytes(0, 256));
        }

        [Fact]
        public void UpdateFirmware_WhenTheFamilyHasNoResetRoute_SaysSoInsteadOfClaimingAReset()
        {
            // A part placed only by family fallback reports SamBaChipFamily.Unknown; when its initial
            // probe also never read a CPUID (the legacy CHIPID branch), DeviceResetter has no route
            // for it at all. The update itself succeeds; the reset does not happen, and the log has to
            // say that rather than end on "Resetting device".
            var (device, _) = NewLegacyFallbackIdentifiedDevice();
            var messages = new List<string>();
            device.ProgressChanged += (_, e) => messages.Add(e.Message);
            device.Open(SamBaChipIdentificationMode.ChipId);
            Assert.Equal(SamBaChipFamily.Unknown, device.ChipInfo.Family);
            Assert.Null(device.ChipInfo.CpuId);

            device.UpdateFirmware(
                new byte[512],
                new SamBaUpdateOptions
                {
                    BulkErase = false,
                    Verify = false,
                    UnlockBeforeWrite = false,
                    Reset = true,
                });

            Assert.Contains(messages, m => m.Contains("No reset route for Unknown"));
            Assert.Contains(messages, m => m.Contains("Power-cycle"));
            Assert.False(device.IsOpen);
        }

        [Fact]
        public void UpdateFirmware_WhenFamilyFallbackButCpuIdConfirmed_StillResets()
        {
            // The counterpart to the test above: a part placed by family fallback whose initial probe
            // did read a CPUID (the Cortex-M branch) is not left stranded just because its family
            // could not be named — every Cortex-M carries AIRCR at the same fixed address, so the
            // update completes and the device still resets and closes normally.
            var (device, fake) = NewFallbackIdentifiedDevice();
            var messages = new List<string>();
            device.ProgressChanged += (_, e) => messages.Add(e.Message);
            device.Open();
            Assert.Equal(SamBaChipFamily.Unknown, device.ChipInfo.Family);
            Assert.NotNull(device.ChipInfo.CpuId);

            device.UpdateFirmware(
                new byte[512],
                new SamBaUpdateOptions
                {
                    BulkErase = false,
                    Verify = false,
                    UnlockBeforeWrite = false,
                    Reset = true,
                });

            Assert.DoesNotContain(messages, m => m.Contains("No reset route"));
            Assert.Contains((0xE000ED0Cu, 0x05FA0004u), fake.WordWrites);
            Assert.False(device.IsOpen);
        }

        [Fact]
        public void GetUniqueId_ReadsTheDeviceOnceAndRemembersTheAnswer()
        {
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            fake.SetWord(Sam3xFlash + 0, 0x11111111);
            fake.SetWord(Sam3xFlash + 4, 0x22222222);
            fake.SetWord(Sam3xFlash + 8, 0x33333333);
            fake.SetWord(Sam3xFlash + 12, 0x44444444);

            var first = device.GetUniqueId();
            var second = device.GetUniqueId();

            Assert.Equal(new uint[] { 0x11111111, 0x22222222, 0x33333333, 0x44444444 }, first);
            Assert.Same(first, second);
            // The STUI/SPUI pair ran once: the second call went nowhere near the flash controller.
            Assert.Single(fake.WordWrites.FindAll(w => w.Address == Sam3xRegs + 0x004 && w.Value == 0x5A00000Eu));
        }

        [Fact]
        public void GetUniqueId_AfterReopen_ReadsTheDeviceAgain()
        {
            // The cache belongs to one Open, not to the SamBaDevice: the same COM port can present a
            // different board on the next open, and its unique id is a different number.
            var (device, fake) = NewSam3x8eDevice();
            device.Open();
            fake.SetWord(Sam3xFlash + 0, 0x11111111);
            var first = device.GetUniqueId();

            device.Close();
            fake.SetWord(Sam3xFlash + 0, 0xAAAAAAAA);
            device.Open();
            var second = device.GetUniqueId();

            Assert.Equal(0x11111111u, first[0]);
            Assert.Equal(0xAAAAAAAAu, second[0]);
        }

        [Fact]
        public void HasUniqueId_TellsWhetherTheReadIsWorthMaking()
        {
            var (eefc, _) = NewSam3x8eDevice();
            eefc.Open();

            // The SAMD21 has no unique-id command either, and an id all the same: it publishes one at
            // fixed read-only addresses, so the answer here is yes and the read is four words.
            var nvmFake = new FakeSamDevice();
            nvmFake.SetWord(0xE000ED00, 0x410CC600);
            nvmFake.SetWord(0x41002018, 0x10010005);
            nvmFake.SetWord(0x41004014, 1);
            nvmFake.SetWord(0x0080A00C, 0x0F0F0F0F);
            var nvm = new SamBaDevice(nvmFake);
            nvm.Open();

            // The legacy EFC is where there is genuinely nothing to read.
            var efcFake = new FakeSamDevice();
            efcFake.SetWord(0x0, 0xEA000000);           // reset vector branch -> SAM7/9 probe path
            efcFake.SetWord(0xFFFFF240, 0x270B0940);    // CHIPID (AT91SAM7S256 B/C)
            efcFake.SetWord(0xFFFFFF68, 1);             // FSR ready
            var efc = new SamBaDevice(efcFake);
            efc.Open();

            Assert.True(eefc.ChipInfo.HasUniqueId);
            Assert.True(nvm.ChipInfo.HasUniqueId);
            Assert.Equal(0x0F0F0F0Fu, nvm.GetUniqueId()[0]);
            Assert.False(efc.ChipInfo.HasUniqueId);
            Assert.Empty(efc.GetUniqueId());
        }

        [Fact]
        public void Dispose_ClosesDevice_FurtherUseThrows()
        {
            var (device, _) = NewSam3x8eDevice();
            device.Open();

            device.Dispose();

            Assert.Throws<ObjectDisposedException>(() => device.Open());
        }

        [Fact]
        public void Dispose_AlsoGuardsTheTwoSettableProperties()
        {
            // These two forward to the monitor rather than to the device, which is how they came to be
            // the only members that answered after the Dispose that had already torn it down.
            var (device, _) = NewSam3x8eDevice();
            device.Open();

            device.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = device.SafeMode);
            Assert.Throws<ObjectDisposedException>(() => device.SafeMode = true);
            Assert.Throws<ObjectDisposedException>(() => _ = device.ChipEraseTimeout);
            Assert.Throws<ObjectDisposedException>(() => device.ChipEraseTimeout = TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void Reset_WhenTheFamilyHasNoResetRoute_ThrowsInsideTheHierarchyAndStillCloses()
        {
            // The counterpart to UpdateFirmware meeting the same condition: an update reports it and
            // completes, because the programming did succeed, whereas Reset has nothing else it could
            // have done. Closing either way is deliberate — a reset is the last thing a session does.
            // Needs a part whose CPUID was never read either (see NewLegacyFallbackIdentifiedDevice):
            // one with a confirmed Cortex-M CPUID has a route via AIRCR regardless of family.
            var (device, _) = NewLegacyFallbackIdentifiedDevice();
            device.Open(SamBaChipIdentificationMode.ChipId);
            Assert.Equal(SamBaChipFamily.Unknown, device.ChipInfo.Family);
            Assert.Null(device.ChipInfo.CpuId);

            var ex = Assert.Throws<SamBaUnsupportedOperationException>(() => device.Reset());

            Assert.IsAssignableFrom<SamBaException>(ex);    // one catch covers every device failure
            Assert.Equal(nameof(SamBaDevice.Reset), ex.Operation);
            Assert.Contains("power-cycle", ex.Message);
            Assert.False(device.IsOpen);
        }

        [Fact]
        public void Reset_WhenFamilyFallbackButCpuIdConfirmed_WritesAircrAndCloses()
        {
            // A part placed by family fallback with a confirmed Cortex-M CPUID resets through AIRCR
            // like any other Cortex-M part, even with no named family and so no family-specific route.
            var (device, fake) = NewFallbackIdentifiedDevice();
            device.Open();
            Assert.Equal(SamBaChipFamily.Unknown, device.ChipInfo.Family);
            Assert.NotNull(device.ChipInfo.CpuId);

            device.Reset();

            Assert.Contains((0xE000ED0Cu, 0x05FA0004u), fake.WordWrites);
            Assert.False(device.IsOpen);
        }

        [Fact]
        public void ToString_ContainsKeyProperties()
        {
            var (device, _) = NewSam3x8eDevice();
            device.Open();

            string text = device.ToString();

            Assert.Contains("ATSAM3X8", text);
            Assert.Contains("IsOpen: True", text);
        }
    }
}

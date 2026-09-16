using Anp.Atmel.SamBa.Chips;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using Anp.Atmel.SamBa.Flash;
using Anp.Atmel.SamBa.Protocol;
using System.Diagnostics;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    public class FlashControllerTests
    {
        private const uint Sam3xRegs = 0x400E0A00;
        private const uint Sam3xFlash = 0x80000;

        /// <summary>Fake wired as an ATSAM3X8E (Arduino Due): EEFC, 2 planes, 256-byte pages.</summary>
        private static FakeSamDevice NewSam3x8e()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);       // Cortex-M4 CPUID
            fake.SetWord(0x4, 0x00800125);              // reset vector into the SAM-BA ROM
            fake.SetWord(0x400E0740, 0x285E0A60);       // CHIPID CIDR
            fake.SetWord(Sam3xRegs + 0x008, 1);         // EEFC0 FSR ready
            fake.SetWord(Sam3xRegs + 0x208, 1);         // EEFC1 FSR ready
            return fake;
        }

        private const uint Efc0Fmr = 0xFFFFFF60;
        private const uint Efc0Fsr = 0xFFFFFF68;
        private const uint Efc1Fmr = 0xFFFFFF70;

        /// <summary>Fake wired as an AT91SAM7S256: legacy MC/EFC, 1 plane, 256-byte pages.</summary>
        private static FakeSamDevice NewSam7s256()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0x0, 0xEA000000);          // reset vector branch -> SAM7/9 probe path
            fake.SetWord(0xFFFFF240, 0x270B0940);   // CHIPID (AT91SAM7S256 B/C)
            fake.SetWord(Efc0Fsr, 1);               // FSR ready
            return fake;
        }

        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSam7s256()
        {
            FakeSamDevice fake = NewSam7s256();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);
            return (monitor, flash, fake);
        }

        /// <summary>Fake wired as an AT91SAM7S512: legacy MC/EFC, 2 planes (EFC0 + EFC1).</summary>
        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSam7s512()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0x0, 0xEA000000);          // reset vector branch -> SAM7/9 probe path
            fake.SetWord(0xFFFFF240, 0x270B0A40);   // CHIPID (AT91SAM7S512, 2 planes)
            fake.SetWord(0xFFFFFF68, 1);            // EFC0 FSR ready
            fake.SetWord(0xFFFFFF78, 1);            // EFC1 FSR ready
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);
            return (monitor, flash, fake);
        }

        /// <summary>Fake wired as an ATSAMD21x18 (Arduino Zero): NVMCTRL D2x, 64-byte pages.</summary>
        private static FakeSamDevice NewSamD21()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);       // Cortex-M0+ CPUID
            fake.SetWord(0x41002018, 0x10010005);       // DSU DID
            fake.SetWord(0x41004014, 1);                // NVMCTRL INTFLAG READY
            return fake;
        }

        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSamD21()
        {
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);
            return (monitor, flash, fake);
        }

        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSam3x8e()
        {
            FakeSamDevice fake = NewSam3x8e();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);
            return (monitor, flash, fake);
        }

        [Fact]
        public void Identify_Sam3x8e_FindsEefcRecord()
        {
            FakeSamDevice fake = NewSam3x8e();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            ChipIdentity identity = ChipIdentifier.Identify(monitor);

            Assert.Equal("ATSAM3X8", identity.Record.Name);
            Assert.Equal(0x285E0A60u, identity.ChipId);
            Assert.Equal(SamBaChipFamily.Sam3X, identity.Record.Family);
        }

        [Fact]
        public void Identify_SamD21_ViaDsuDeviceId()
        {
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            ChipIdentity identity = ChipIdentifier.Identify(monitor);

            Assert.Equal("ATSAMD21x18", identity.Record.Name);
            Assert.Equal(0x10010005u, identity.DeviceId);
        }

        [Fact]
        public void Identify_UnknownChip_Throws()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);
            fake.SetWord(0x4, 0x00800125);
            fake.SetWord(0x400E0740, 0x12345678);       // unknown CHIPID
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            var ex = Assert.Throws<SamBaUnsupportedDeviceException>(() => ChipIdentifier.Identify(monitor));
            Assert.Equal(0x12345678u, ex.ChipId);

            // A CHIPID part never gets its DSU read, and that is reported as "no value" rather than as
            // a DSU that answered zero — the two say different things about the part, and this branch
            // is where somebody diagnosing a misidentification starts.
            Assert.Null(ex.DeviceId);
            Assert.Equal(0x410FC240u, ex.CpuId);        // whole register, not just the PARTNO field
            Assert.Contains("DSU DID=not read", ex.Message);
            Assert.Contains("CHIPID=0x12345678", ex.Message);
            Assert.Contains("CPUID=0x410FC240", ex.Message);
        }

        [Fact]
        public void Identify_DsuAnsweringZero_ReportsZeroNotAbsent()
        {
            // The case the nullable words exist for. A Cortex-M0+ part has its DSU DID read
            // unconditionally; if it answers 0 no row matches, and the report has to say the register
            // was asked and returned zero. Reported as 0 it would be indistinguishable from the CHIPID
            // branch above, where the DSU is never touched at all.
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);       // Cortex-M0+ -> DSU path
            fake.SetWord(0x41002018, 0x00000000);       // DSU answers zero
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            var ex = Assert.Throws<SamBaUnsupportedDeviceException>(() => ChipIdentifier.Identify(monitor));

            Assert.Equal(0u, ex.DeviceId);
            Assert.Contains("DSU DID=0x00000000", ex.Message);
            Assert.Null(ex.ChipId);
            Assert.Contains("CHIPID=not read", ex.Message);
        }

        [Fact]
        public void Identify_ForcedChipId_SkipsResetVectorProbeAndReachesLegacyBranch()
        {
            // No reset-vector word set at all (reads back as 0), which is not the ARM7 branch opcode —
            // exactly the "blank flash" case where a genuine AT91SAM7/9 part has no branch opcode at
            // address 0. Under Auto this sends the part down the Cortex-M branch and fails to identify;
            // SamBaChipIdentificationMode.ChipId exists to route around that missing opcode.
            var fake = new FakeSamDevice();
            fake.SetWord(0xFFFFF240, 0x270B0940);   // CHIPID (AT91SAM7S256 B/C)
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            Assert.Throws<SamBaUnsupportedDeviceException>(
                () => ChipIdentifier.Identify(monitor, SamBaChipIdentificationMode.Auto));

            ChipIdentity identity = ChipIdentifier.Identify(monitor, SamBaChipIdentificationMode.ChipId);

            Assert.Equal("AT91SAM7S256", identity.Record.Name);
            Assert.Equal(0x270B0940u, identity.ChipId);
            Assert.Null(identity.CpuId);
        }

        [Fact]
        public void Identify_ForcedCpuId_SkipsResetVectorProbeAndReachesCortexMBranch()
        {
            // Reset vector wired to look like an ARM7 branch opcode even though this is the Cortex-M4
            // Sam3x8e fixture — the same unreliable detection in the other direction. Under Auto this
            // misreads as the legacy core and fails to identify; SamBaChipIdentificationMode.CpuId
            // exists to route around it.
            FakeSamDevice fake = NewSam3x8e();
            fake.SetWord(0x0, 0xEA000000);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            Assert.Throws<SamBaUnsupportedDeviceException>(
                () => ChipIdentifier.Identify(monitor, SamBaChipIdentificationMode.Auto));

            ChipIdentity identity = ChipIdentifier.Identify(monitor, SamBaChipIdentificationMode.CpuId);

            Assert.Equal("ATSAM3X8", identity.Record.Name);
            Assert.Equal(0x285E0A60u, identity.ChipId);
            Assert.Equal(0x410FC240u, identity.CpuId);
        }

        [Fact]
        public void Eefc_Init_SetsFlashWaitStates()
        {
            var (_, _, fake) = OpenSam3x8e();

            Assert.Contains((Sam3xRegs + 0x000, 0x600u), fake.WordWrites);
            Assert.Contains((Sam3xRegs + 0x200, 0x600u), fake.WordWrites);
        }

        [Fact]
        public void EraseAll_IssuesEaOnBothPlanes_DisablesEraseAuto()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            programmer.EraseAll(0);

            Assert.Contains((Sam3xRegs + 0x004, 0x5A000005u), fake.WordWrites);  // EEFC0 FCR EA
            Assert.Contains((Sam3xRegs + 0x204, 0x5A000005u), fake.WordWrites);  // EEFC1 FCR EA
            Assert.False(flash.AutoEraseEnabled);
        }

        [Fact]
        public void Write_ProgramsPages_DataLandsInFlash_VerifyPasses()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var progress = new List<SamBaProgressEventArgs>();
            var programmer = new FlashProgrammer(flash, progress.Add);

            // 2.5 pages of patterned data.
            byte[] data = Enumerable.Range(0, 640).Select(i => (byte)(i * 7)).ToArray();

            programmer.EraseAll(0);
            programmer.Write(data, 0, assumeErased: true);
            programmer.Verify(data, 0);

            // Data landed at the flash base address.
            Assert.Equal(data, fake.GetBytes(Sam3xFlash, data.Length));

            // Erase-auto was off, so plain WP (0x1) commits with page numbers 0..2.
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000001u), fake.WordWrites);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000101u), fake.WordWrites);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000201u), fake.WordWrites);

            // Partial final page was padded with 0xFF.
            Assert.Equal(0xFF, fake.GetByte(Sam3xFlash + 640));

            Assert.Contains(progress, p => p.Stage == "Writing" && p.Value == 3 && p.Maximum == 3);
            Assert.Contains(progress, p => p.Stage == "Verifying");
        }

        [Fact]
        public void Verify_LargeImage_ReadsInOneStreamAndRoundTrips()
        {
            // An NVMCTRL part on purpose: its monitor serves block reads, so this pins the R#
            // stream properties. The EEFC counterpart (word reads) is Eefc_Verify_UsesWordReads.
            var (monitor, flash, fake) = OpenSamD21();
            var progress = new List<SamBaProgressEventArgs>();
            var programmer = new FlashProgrammer(flash, progress.Add);

            // 8000 = 64*125: a large image whose length is a multiple of the USB max packet, so the
            // read-back must be trimmed to avoid the multiple-of-64 hang.
            byte[] data = Enumerable.Range(0, 8000).Select(i => (byte)(i * 31)).ToArray();

            fake.SetBytes(flash.FlashAddress, data);
            programmer.Verify(data, 0);   // round-trips without a mismatch

            // One R# stream over (almost) the whole image; its length is not a multiple of 64.
            Assert.Single(fake.ReadCommands);
            Assert.NotEqual(0, fake.ReadCommands[0].Size % 64);
            Assert.Contains(progress, p => p.Stage == "Verifying");
        }

        [Fact]
        public void Write_UsesEwp_WhenAutoEraseEnabled()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            byte[] page = new byte[256];
            programmer.Write(page, 0, assumeErased: true);

            Assert.Contains((Sam3xRegs + 0x004, 0x5A000003u), fake.WordWrites);  // EWP page 0
        }

        [Fact]
        public void Write_EefcAutoErasePast16k_ThrowsBeforeWriting()
        {
            // EEFC EWP auto-erase only covers the first two 8 KB sectors, so an
            // auto-erasing write (no prior full erase) crossing 16 KB must fail fast, not part-way.
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            Assert.True(flash.AutoEraseEnabled);  // no EraseAll -> auto-erase (EWP) path
            fake.WordWrites.Clear();

            byte[] data = new byte[16 * 1024 + 256];  // one page past the 16 KB EWP limit
            var ex = Assert.Throws<SamBaFlashCommandException>(
                () => programmer.Write(data, 0, assumeErased: false));

            Assert.Contains("16 KB", ex.Message);
            // A family limitation, not a controller failure — nothing was asked of the hardware.
            Assert.True(ex.IsUnsupported);
            Assert.False(ex.IsCommandError);
            // Fail-fast: no page-latch load or WP command was issued.
            Assert.DoesNotContain(fake.WordWrites, w => w.Address == Sam3xRegs + 0x004);
        }

        [Fact]
        public void Write_EefcWithin16k_AutoEraseAllowed()
        {
            var (monitor, flash, _) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            // Exactly 16 KB with auto-erase on is still within the EWP-capable sectors.
            programmer.Write(new byte[16 * 1024], 0, assumeErased: false);
        }

        [Fact]
        public void Write_EefcPast16k_AllowedAfterFullErase()
        {
            // The guard is scoped to auto-erase: once a full erase turns it off, plain WP works
            // across the whole flash (the default UpdateFirmware path).
            var (monitor, flash, _) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            programmer.EraseAll(0);
            Assert.False(flash.AutoEraseEnabled);

            programmer.Write(new byte[16 * 1024 + 256], 0, assumeErased: true);
        }

        [Fact]
        public void Write_MergingAfterAnEarlierEraseAll_RestoresAutoErase()
        {
            // EraseAll turns auto-erase off, and that outlives the call: the controller lives as long
            // as the connection. A later merge-based write programs over content that is not blank, so
            // it must bring the erase back or the write silently lands on top of the old data.
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            programmer.EraseAll(0);
            Assert.False(flash.AutoEraseEnabled);
            fake.WordWrites.Clear();

            programmer.Write(new byte[256], 0, assumeErased: false);

            Assert.True(flash.AutoEraseEnabled);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A000003u), fake.WordWrites);       // EWP
            Assert.DoesNotContain((Sam3xRegs + 0x004, 0x5A000001u), fake.WordWrites); // not plain WP
        }

        [Fact]
        public void Verify_UnalignedOffset_IsAllowed()
        {
            // Verify only reads, so it carries no alignment requirement — otherwise an image written
            // by WriteBytes at an arbitrary offset could not be checked. One page into a 256-byte row.
            var (monitor, flash, fake) = OpenSamD21();
            var programmer = new FlashProgrammer(flash, null);

            byte[] data = Enumerable.Repeat((byte)0x5A, 64).ToArray();
            fake.SetBytes(64, data);

            programmer.Verify(data, 64);
        }

        [Fact]
        public void Write_UpperPlanePage_GoesThroughFcr1()
        {
            var (monitor, flash, fake) = OpenSam3x8e();

            // ATSAM3X8: 2048 pages, 2 planes -> page 1024 is plane 1 page 0.
            flash.WriteBlock(1024, new byte[256], 0);

            Assert.Contains((Sam3xRegs + 0x204, 0x5A000003u), fake.WordWrites);  // EEFC1 EWP page 0
        }

        [Fact]
        public void Write_PageLatch_IsSingleBatchedWrite()
        {
            var (monitor, flash, fake) = OpenSam3x8e();

            fake.WriteSizes.Clear();
            flash.WriteBlock(0, new byte[256], 0);

            // 256-byte page = 64 W# commands of 19 bytes = 1216 bytes in ONE transport write.
            Assert.Contains(64 * SambaMonitor.WriteWordCommandLength, fake.WriteSizes);
        }

        [Fact]
        public void Efc_Init_SetsFmcnWhenUnconfigured()
        {
            var (_, _, fake) = OpenSam7s256();

            // FMR started at 0, so initialization must program a safe FMCN
            // (0x34 in bits 16-23) with NEBP (bit 7) cleared -> 0x00340000.
            Assert.Contains((Efc0Fmr, 0x00340000u), fake.WordWrites);
        }

        [Fact]
        public void Efc_EraseAll_TwoPlanes_IssuesEaOnBothControllers()
        {
            var (_, flash, fake) = OpenSam7s512();

            flash.EraseAll(0);

            Assert.Contains((0xFFFFFF64u, 0x5A000008u), fake.WordWrites);  // EFC0 FCR EA
            Assert.Contains((0xFFFFFF74u, 0x5A000008u), fake.WordWrites);  // EFC1 FCR EA (upper plane)
        }

        [Fact]
        public void Efc_ProgrammingError_ThrowsCommandError()
        {
            var (_, flash, fake) = OpenSam7s256();
            fake.SetWord(Efc0Fsr, 0x8);   // FSR PROGE (bit 3) set

            var ex = Assert.Throws<SamBaFlashCommandException>(() => flash.WriteBlock(0, new byte[256], 0));

            Assert.True(ex.IsCommandError);
            Assert.False(ex.IsLockError);
        }

        [Fact]
        public void Efc_LockError_ThrowsLockError()
        {
            var (_, flash, fake) = OpenSam7s256();
            fake.SetWord(Efc0Fsr, 0x4);   // FSR LOCKE (bit 2) set

            var ex = Assert.Throws<SamBaFlashCommandException>(() => flash.WriteBlock(0, new byte[256], 0));

            Assert.True(ex.IsLockError);
        }

        [Fact]
        public void SafeMode_LoadsLatchWordByWord_NotBatched()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            monitor.SafeMode = true;

            fake.WriteSizes.Clear();
            flash.WriteBlock(0, new byte[256], 0);

            // No single 1216-byte batch; the 64-word latch is loaded as individual W# writes.
            Assert.DoesNotContain(64 * SambaMonitor.WriteWordCommandLength, fake.WriteSizes);
            Assert.True(fake.WriteSizes.Count(s => s == SambaMonitor.WriteWordCommandLength) >= 64);
        }

        [Fact]
        public void Verify_Mismatch_ReportsOffsetAndAddress()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            byte[] data = Enumerable.Repeat((byte)0xAA, 256).ToArray();
            programmer.Write(data, 0, assumeErased: true);
            fake.SetByte(Sam3xFlash + 5, 0x55);  // corrupt one byte

            var ex = Assert.Throws<SamBaVerificationException>(() => programmer.Verify(data, 0));

            Assert.Equal(5, ex.MismatchOffset);
            Assert.Equal(Sam3xFlash + 5, ex.MismatchAddress);
        }

        [Fact]
        public void Verify_AllZerosReadback_ExplainsTheSymptom()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            byte[] data = Enumerable.Repeat((byte)0xAA, 256).ToArray();
            programmer.Write(data, 0, assumeErased: true);
            fake.SetBytes(Sam3xFlash, new byte[256]);  // a write that did not take effect

            var ex = Assert.Throws<SamBaVerificationException>(() => programmer.Verify(data, 0));

            Assert.Contains("all zeros", ex.Message);
        }

        [Fact]
        public void Eefc_ReadRange_UsesWordReads()
        {
            // These ROM monitors answer a block read of flash with all zeros, so the flash layer
            // must not issue one; the same bytes come back one w# word at a time instead.
            var (monitor, flash, fake) = OpenSam3x8e();
            byte[] pattern = Enumerable.Range(0, 300).Select(i => (byte)(i * 11)).ToArray();
            fake.SetBytes(Sam3xFlash, pattern);

            var buffer = new byte[pattern.Length];
            flash.ReadRange(0, buffer, 0, buffer.Length);

            Assert.Equal(pattern, buffer);
            Assert.Empty(fake.ReadCommands);
        }

        [Fact]
        public void Eefc_Verify_UsesWordReads()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);
            byte[] data = Enumerable.Range(0, 700).Select(i => (byte)(i * 5)).ToArray();
            fake.SetBytes(Sam3xFlash, data);

            programmer.Verify(data, 0);   // round-trips without a mismatch

            Assert.Empty(fake.ReadCommands);
        }

        [Fact]
        public void Efc_ReadRange_StillUsesTheBlockCommand()
        {
            var (monitor, flash, fake) = OpenSam7s512();
            byte[] pattern = Enumerable.Range(0, 64).Select(i => (byte)(i + 2)).ToArray();
            fake.SetBytes(flash.FlashAddress, pattern);

            var buffer = new byte[pattern.Length];
            flash.ReadRange(0, buffer, 0, buffer.Length);

            Assert.Equal(pattern, buffer);
            Assert.Single(fake.ReadCommands);
        }

        [Fact]
        public void D2x_ReadRange_StillUsesTheBlockCommand()
        {
            var (monitor, flash, fake) = OpenSamD21();
            byte[] pattern = Enumerable.Range(0, 64).Select(i => (byte)(i + 7)).ToArray();
            fake.SetBytes(flash.FlashAddress, pattern);

            var buffer = new byte[pattern.Length];
            flash.ReadRange(0, buffer, 0, buffer.Length);

            Assert.Equal(pattern, buffer);
            Assert.Single(fake.ReadCommands);
        }

        [Fact]
        public void WriteBlock_LockError_Throws()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            fake.SetWord(Sam3xRegs + 0x008, 0x4);  // FSR lock error

            var ex = Assert.Throws<SamBaFlashCommandException>(() => flash.WriteBlock(0, new byte[256], 0));

            Assert.True(ex.IsLockError);
        }

        [Fact]
        public void Write_UnalignedOffset_Throws()
        {
            var (monitor, flash, _) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            Assert.Throws<ArgumentOutOfRangeException>(() => programmer.Write(new byte[16], 3, true));
        }

        [Fact]
        public void Write_PartialPage_ReadModifyWrite_PreservesExistingBytes()
        {
            var (monitor, flash, fake) = OpenSam3x8e();
            var programmer = new FlashProgrammer(flash, null);

            // Existing content in page 0.
            fake.SetBytes(Sam3xFlash, Enumerable.Repeat((byte)0xEE, 256).ToArray());

            byte[] data = Enumerable.Repeat((byte)0x11, 100).ToArray();
            programmer.Write(data, 0, assumeErased: false);

            Assert.Equal(0x11, fake.GetByte(Sam3xFlash + 99));
            Assert.Equal(0xEE, fake.GetByte(Sam3xFlash + 100));  // merged, not padded
        }

        [Fact]
        public void D2x_WriteBlock_ErasesRowOnce_ThenCommitsItsFourPages()
        {
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            // A write block here is a 256-byte row: four 64-byte hardware pages.
            Assert.Equal(256, flash.WriteBlockSize);
            byte[] block = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            flash.WriteBlock(0, block, 0);

            const uint ctrlA = 0x41004000;
            const uint addrReg = 0x4100401C;
            // PROGE | LOCKE | NVME written back to clear whatever an earlier operation left set.
            Assert.Contains((0x41004018u, 0x1Cu), fake.WordWrites);
            // One erase for the row, then a page-buffer clear and a write per hardware page.
            Assert.Single(fake.WordWrites.FindAll(w => w.Address == ctrlA && w.Value == 0xA502u));  // ER
            Assert.Equal(4, fake.WordWrites.FindAll(w => w.Address == ctrlA && w.Value == 0xA544u).Count);  // PBC
            Assert.Equal(4, fake.WordWrites.FindAll(w => w.Address == ctrlA && w.Value == 0xA504u).Count);  // WP
            // ADDR points at each page in turn, in 16-bit word units.
            Assert.Contains((addrReg, 0x0u), fake.WordWrites);
            Assert.Contains((addrReg, 0x20u), fake.WordWrites);   // byte 64
            Assert.Contains((addrReg, 0x60u), fake.WordWrites);   // byte 192
            Assert.Equal(block, fake.GetBytes(0, 256));           // page buffer = flash-mapped
        }

        [Fact]
        public void Eefc_GetUniqueId_ReadsFlashWindowBetweenStuiAndSpui()
        {
            var (_, flash, fake) = OpenSam3x8e();

            // While the unique-id read is active the id is mapped into the flash region.
            fake.SetWord(Sam3xFlash + 0, 0x11111111);
            fake.SetWord(Sam3xFlash + 4, 0x22222222);
            fake.SetWord(Sam3xFlash + 8, 0x33333333);
            fake.SetWord(Sam3xFlash + 12, 0x44444444);

            var id = flash.GetUniqueId();

            Assert.Equal(new uint[] { 0x11111111, 0x22222222, 0x33333333, 0x44444444 }, id);
            Assert.Contains((Sam3xRegs + 0x004, 0x5A00000Eu), fake.WordWrites);  // STUI
            Assert.Contains((Sam3xRegs + 0x004, 0x5A00000Fu), fake.WordWrites);  // SPUI
        }

        [Fact]
        public void Efc_GetUniqueId_EmptyOnControllersWithoutSupport()
        {
            var (_, flash, _) = OpenSam7s256();
            Assert.Empty(flash.GetUniqueId());
        }

        [Fact]
        public void D2x_GetUniqueId_ReadsTheSerialNumberWordsWithoutACommand()
        {
            var (_, flash, fake) = OpenSamD21();

            // Word 0 lies apart from the other three, which is why the addresses are listed rather
            // than strided.
            fake.SetWord(0x0080A00C, 0x11111111);
            fake.SetWord(0x0080A040, 0x22222222);
            fake.SetWord(0x0080A044, 0x33333333);
            fake.SetWord(0x0080A048, 0x44444444);
            int writesBefore = fake.WordWrites.Count;   // manual-write mode was configured at Create

            var id = flash.GetUniqueId();

            Assert.Equal(new uint[] { 0x11111111, 0x22222222, 0x33333333, 0x44444444 }, id);
            // Four reads and nothing else: no command register touched, no bracket like the EEFC's.
            Assert.Equal(writesBefore, fake.WordWrites.Count);
        }

        /// <summary>Fake wired as an ATSAME54x20: NVMCTRL D5x, DSU-identified Cortex-M4.</summary>
        /// <summary>Fake wired as an ATSAME54x20 (Adafruit Grand Central): NVMCTRL D5x, 512-byte pages.</summary>
        private static FakeSamDevice NewSamE54()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC241);       // Cortex-M4 CPUID
            fake.SetWord(0x4, 0x00000101);              // reset vector not in SAM-BA ROM range -> DSU path
            fake.SetWord(0x41002018, 0x61840000);       // DSU DID (ATSAME54x20)
            fake.SetByte(0x41004012, 1);                // NVMCTRL STATUS.READY (a 16-bit register)
            return fake;
        }

        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSame54()
        {
            FakeSamDevice fake = NewSamE54();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);
            return (monitor, flash, fake);
        }

        [Fact]
        public void D5x_GetSecurity_ReadsDsuStatusBProtBit()
        {
            var (_, flash, fake) = OpenSame54();

            Assert.False(flash.GetSecurity());       // PROT clear

            fake.SetByte(0x41002002, 0x01);          // DSU STATUSB.PROT set
            Assert.True(flash.GetSecurity());
        }

        [Fact]
        public void D5x_GetUniqueId_ReadsItsOwnSerialNumberAddresses()
        {
            var (_, flash, fake) = OpenSame54();

            // Same shape as the D2x generation, different region and different offsets in it.
            fake.SetWord(0x008061FC, 0xAAAAAAAA);
            fake.SetWord(0x00806010, 0xBBBBBBBB);
            fake.SetWord(0x00806014, 0xCCCCCCCC);
            fake.SetWord(0x00806018, 0xDDDDDDDD);

            Assert.Equal(
                new uint[] { 0xAAAAAAAA, 0xBBBBBBBB, 0xCCCCCCCC, 0xDDDDDDDD }, flash.GetUniqueId());
        }

        private const uint D2xCtrlA = 0x41004000;
        private const uint D2xStatus = 0x41004018;
        private const uint D2xUserRow = 0x804000;
        private const uint D2xUserRowLockByte = D2xUserRow + 0x6;

        private const uint D5xCtrlB = 0x41004004;
        private const uint D5xAddr = 0x41004014;
        private const uint D5xUserPage = 0x804000;
        private const uint D5xUserPageLockByte = D5xUserPage + 0x8;

        [Fact]
        public void D2x_GetLockRegions_ReadsInvertedUserRowLockBits()
        {
            var (_, flash, fake) = OpenSamD21();

            // The user row holds the 16 lock bits at offset 6; a clear bit means locked.
            fake.SetByte(D2xUserRowLockByte, 0xFE);
            fake.SetByte(D2xUserRowLockByte + 1, 0xFF);

            bool[] regions = flash.GetLockRegions();

            Assert.Equal(16, regions.Length);
            Assert.True(regions[0]);
            Assert.False(regions[1]);
            Assert.False(regions[8]);
        }

        [Fact]
        public void D2x_GetSecurity_ReadsNvmctrlStatusSecurityBit()
        {
            var (_, flash, fake) = OpenSamD21();

            Assert.False(flash.GetSecurity());

            fake.SetWord(D2xStatus, 0x100);  // STATUS.SB

            Assert.True(flash.GetSecurity());
        }

        [Fact]
        public void D2x_ApplyOptions_LockAll_RewritesUserRowThroughAuxCommands()
        {
            var (_, flash, fake) = OpenSamD21();

            fake.SetByte(D2xUserRowLockByte, 0xFF);      // all 16 regions currently unlocked
            fake.SetByte(D2xUserRowLockByte + 1, 0xFF);
            fake.WordWrites.Clear();

            flash.ApplyOptions(new FlashOptionState { Lock = true });

            // Lock bits live in the user row, so it is erased and written back whole.
            Assert.Contains((D2xCtrlA, 0xA505u), fake.WordWrites);  // EAR (erase aux row)
            Assert.Contains((D2xCtrlA, 0xA544u), fake.WordWrites);  // PBC
            Assert.Contains((D2xCtrlA, 0xA506u), fake.WordWrites);  // WAP (write aux page)
            Assert.Equal((byte)0x00, fake.GetByte(D2xUserRowLockByte));
            Assert.Equal((byte)0x00, fake.GetByte(D2xUserRowLockByte + 1));
        }

        [Fact]
        public void D2x_SetLockRegions_Subset_MasksOnlyRequestedBits()
        {
            var (_, flash, fake) = OpenSamD21();

            fake.SetByte(D2xUserRow, 0xA5);              // an unrelated fuse byte in the row
            fake.SetByte(D2xUserRowLockByte, 0xFF);      // all 16 regions currently unlocked
            fake.SetByte(D2xUserRowLockByte + 1, 0xFF);
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 0, 9 }, true);

            // One erase-and-rewrite of the row, clearing only the two requested bits; the other
            // fourteen regions and the rest of the row ride through the rewrite untouched.
            Assert.Single(fake.WordWrites.FindAll(w => w.Address == D2xCtrlA && w.Value == 0xA505u));
            Assert.Equal((byte)0xFE, fake.GetByte(D2xUserRowLockByte));
            Assert.Equal((byte)0xFD, fake.GetByte(D2xUserRowLockByte + 1));
            Assert.Equal((byte)0xA5, fake.GetByte(D2xUserRow));
        }

        [Fact]
        public void D2x_SetLockRegions_NoChangeNeeded_SkipsRmw()
        {
            var (_, flash, fake) = OpenSamD21();

            fake.SetByte(D2xUserRowLockByte, 0xFC);      // regions 0 and 1 already locked
            fake.SetByte(D2xUserRowLockByte + 1, 0xFF);
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 0, 1 }, true);

            // Nothing differs, so the user row is neither erased nor rewritten.
            Assert.Empty(fake.WordWrites.FindAll(w => w.Address == D2xCtrlA && w.Value == 0xA505u));
            Assert.Empty(fake.WordWrites.FindAll(w => w.Address == D2xCtrlA && w.Value == 0xA506u));
        }

        [Fact]
        public void D2x_EraseAll_OffsetOffRowBoundary_Throws()
        {
            // 64-byte pages, 4 pages to a row, so the erase unit is 256 bytes.
            var (_, flash, _) = OpenSamD21();

            Assert.Throws<ArgumentOutOfRangeException>(() => flash.EraseAll(100));
        }

        /// <summary>Fake wired as an Arduino Zero whose bootloader advertises the X# extension.</summary>
        private static (FlashController flash, FakeSamDevice fake) OpenSamD21WithChipErase()
        {
            FakeSamDevice fake = NewSamD21();
            fake.Version = "v2.0 [Arduino:XYZ] Apr 19 2019 14:38:48";
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            return (FlashController.Create(monitor, identity), fake);
        }

        [Fact]
        public void D2x_SubRowWrite_MergesTheWholeRow_PreservingItsOtherPages()
        {
            // The row is the write block here: erasing to write 64 bytes clears all 256, so the
            // other three pages have to be read back and rewritten or they would come back blank.
            var (monitor, flash, fake) = OpenSamD21();
            var programmer = new FlashProgrammer(flash, _ => { });
            fake.SetBytes(0, Enumerable.Repeat((byte)0xEE, 256).ToArray());

            programmer.WriteBytes(Enumerable.Repeat((byte)0x11, 64).ToArray(), 0);

            // The row was read before being rewritten (as one R# less the byte the monitor peels off
            // a multiple-of-64 transfer — the shape is SambaMonitor's business, not this test's)...
            Assert.NotEmpty(fake.ReadCommands);
            // ...one erase for the row, and all four of its pages committed.
            Assert.Single(fake.WordWrites.FindAll(w => w.Address == 0x41004000u && w.Value == 0xA502u));
            Assert.Equal(4, fake.WordWrites.FindAll(w => w.Address == 0x41004000u && w.Value == 0xA504u).Count);
            // ...so the written slice landed and the rest of the row survived.
            Assert.Equal(0x11, fake.GetByte(0));
            Assert.Equal(0x11, fake.GetByte(63));
            Assert.Equal(0xEE, fake.GetByte(64));
            Assert.Equal(0xEE, fake.GetByte(255));
        }

        [Fact]
        public void D2x_AutoErasingWrite_CoveringAWholeRow_NeedsNoReadBack()
        {
            var (monitor, flash, fake) = OpenSamD21();
            var programmer = new FlashProgrammer(flash, _ => { });

            programmer.WriteBytes(new byte[256], 0);   // exactly one row

            Assert.Contains((0x41004000u, 0xA502u), fake.WordWrites);   // ER for that row
            Assert.Empty(fake.ReadCommands);                           // nothing to preserve
        }

        [Fact]
        public void D2x_WriteAfterBulkErase_SkipsTheAlignmentGuard()
        {
            // A bulk erase turns auto-erase off (which is all this needs — the erase itself would be
            // a 1024-row loop), so WriteBlock issues no erase and a sub-block write blanks nothing.
            var (monitor, flash, _) = OpenSamD21();
            var programmer = new FlashProgrammer(flash, _ => { });
            flash.SetAutoErase(false);

            programmer.Write(new byte[64], 0, assumeErased: true);
        }

        [Fact]
        public void D2x_EraseAll_WithChipEraseExtension_IssuesXFromTheOffset()
        {
            var (flash, fake) = OpenSamD21WithChipErase();

            flash.EraseAll(0x2000);

            // One command instead of a row-erase loop, and it carries the offset so a bootloader
            // living below it survives.
            Assert.Equal(new[] { 0x2000u }, fake.ChipEraseCommands);
        }

        [Fact]
        public void D2x_EraseAll_WithChipEraseExtension_StillRejectsAMisalignedOffset()
        {
            var (flash, fake) = OpenSamD21WithChipErase();

            // X# hands the offset straight to the bootloader, which validates nothing — so the
            // alignment rule must hold on this route too, or the same call would behave one way on
            // an Arduino board and another on a stock ROM monitor.
            Assert.Throws<ArgumentOutOfRangeException>(() => flash.EraseAll(100));
            Assert.Empty(fake.ChipEraseCommands);
        }

        [Fact]
        public void D5x_WriteBlock_ErasesBlockOnce_ThenCommitsIts16Pages()
        {
            var (_, flash, fake) = OpenSame54();

            // A write block here is an 8 KB erase block: sixteen 512-byte hardware pages.
            Assert.Equal(8192, flash.WriteBlockSize);
            byte[] block = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
            flash.WriteBlock(0, block, 0);

            Assert.Single(fake.WordWrites.FindAll(w => w.Address == D5xCtrlB && w.Value == 0xA501u));  // EB
            Assert.Equal(16, fake.WordWrites.FindAll(w => w.Address == D5xCtrlB && w.Value == 0xA515u).Count);  // PBC
            Assert.Equal(16, fake.WordWrites.FindAll(w => w.Address == D5xCtrlB && w.Value == 0xA503u).Count);  // WP
            Assert.Contains((D5xAddr, 0x0u), fake.WordWrites);      // ADDR takes byte addresses here
            Assert.Contains((D5xAddr, 0x200u), fake.WordWrites);    // second page
            Assert.Equal(block, fake.GetBytes(0, 8192));            // page buffer is flash-mapped
        }

        [Fact]
        public void D5x_GetLockRegions_ReadsInvertedUserPageLockBits()
        {
            var (_, flash, fake) = OpenSame54();

            // The user page holds the 32 lock bits at offset 8; a clear bit means locked.
            fake.SetByte(D5xUserPageLockByte, 0xFE);
            for (uint i = 1; i < 4; i++)
                fake.SetByte(D5xUserPageLockByte + i, 0xFF);

            bool[] regions = flash.GetLockRegions();

            Assert.Equal(32, regions.Length);
            Assert.True(regions[0]);
            Assert.False(regions[1]);
            Assert.False(regions[31]);
        }

        [Fact]
        public void D5x_ApplyOptions_LockAll_RewritesUserPageInQuadWords()
        {
            var (_, flash, fake) = OpenSame54();

            for (uint i = 0; i < 4; i++)
                fake.SetByte(D5xUserPageLockByte + i, 0xFF);  // all 32 regions unlocked
            fake.WordWrites.Clear();

            flash.ApplyOptions(new FlashOptionState { Lock = true });

            Assert.Contains((D5xCtrlB, 0xA500u), fake.WordWrites);  // EP (erase the user page)
            Assert.Contains((D5xCtrlB, 0xA515u), fake.WordWrites);  // PBC
            // A 512-byte user page goes back 16 bytes at a time.
            Assert.Equal(32, fake.WordWrites.FindAll(w => w.Address == D5xCtrlB && w.Value == 0xA504u).Count);
            for (uint i = 0; i < 4; i++)
                Assert.Equal((byte)0x00, fake.GetByte(D5xUserPageLockByte + i));
        }

        [Fact]
        public void D5x_SetLockRegions_Subset_MasksOnlyRequestedBits()
        {
            var (_, flash, fake) = OpenSame54();

            for (uint i = 0; i < 4; i++)
                fake.SetByte(D5xUserPageLockByte + i, 0xFF);  // all 32 regions unlocked
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 0, 31 }, true);

            // One erase-and-rewrite of the user page, clearing only the first and last lock bits.
            Assert.Single(fake.WordWrites.FindAll(w => w.Address == D5xCtrlB && w.Value == 0xA500u));
            Assert.Equal((byte)0xFE, fake.GetByte(D5xUserPageLockByte));
            Assert.Equal((byte)0xFF, fake.GetByte(D5xUserPageLockByte + 1));
            Assert.Equal((byte)0xFF, fake.GetByte(D5xUserPageLockByte + 2));
            Assert.Equal((byte)0x7F, fake.GetByte(D5xUserPageLockByte + 3));
        }

        [Fact]
        public void D5x_EraseAll_OffsetOffBlockBoundary_Throws()
        {
            // 512-byte pages, 16 pages to a block, so the erase unit is 8 KB.
            var (_, flash, _) = OpenSame54();

            Assert.Throws<ArgumentOutOfRangeException>(() => flash.EraseAll(100));
        }

        [Fact]
        public void D2x_CommandError_NamesTheFailedCommand_AndClearsTheFlag()
        {
            var (_, flash, fake) = OpenSamD21();

            fake.SetWord(0x41004014, 0x3);  // INTFLAG: READY | ERROR
            fake.WordWrites.Clear();

            var ex = Assert.Throws<SamBaFlashCommandException>(
                () => flash.WriteBlock(0, new byte[256], 0));

            // The row erase auto-erase does is the first command to run, so it is the one blamed —
            // and it is named, not reported as a bare opcode.
            Assert.Equal("row erase (ER)", ex.Operation);
            Assert.True(ex.IsCommandError);
            Assert.Contains((0x41004014u, 0x2u), fake.WordWrites);  // INTFLAG.ERROR written back to clear it
        }

        [Fact]
        public void D5x_CommandError_NamesTheFailedCommand_AndClearsTheFlags()
        {
            var (_, flash, fake) = OpenSame54();

            fake.SetByte(0x41004010, 0x2);  // INTFLAG.ADDRE
            fake.WordWrites.Clear();

            var ex = Assert.Throws<SamBaFlashCommandException>(
                () => flash.WriteBlock(0, new byte[8192], 0));

            Assert.Equal("block erase (EB)", ex.Operation);
            Assert.True(ex.IsCommandError);
            Assert.Contains((0x41004010u, 0xCEu), fake.WordWrites);  // whole error mask written back
        }

        [Fact]
        public void Nvm_BootSourceIsFixedToFlash()
        {
            var (_, d2x, _) = OpenSamD21();
            var (_, d5x, _) = OpenSame54();

            Assert.Equal(SamBaChipBootSource.Flash, d2x.GetBootSource());
            Assert.Equal(SamBaChipBootSource.Flash, d5x.GetBootSource());
            Assert.False(d2x.CanSelectBootSource);
            Assert.False(d5x.CanSelectBootSource);
            Assert.Throws<SamBaFlashCommandException>(
                () => d2x.ApplyOptions(new FlashOptionState { BootSource = SamBaChipBootSource.Rom }));
            Assert.Throws<SamBaFlashCommandException>(
                () => d5x.ApplyOptions(new FlashOptionState { BootSource = SamBaChipBootSource.Rom }));
        }

        [Fact]
        public void Reset_Sam3x_WritesRstc()
        {
            var (monitor, _, fake) = OpenSam3x8e();

            bool supported = DeviceResetter.TryReset(monitor, SamBaChipFamily.Sam3X, cpuId: null);

            Assert.True(supported);
            Assert.Contains((0x400E1A00u, 0xA500000Du), fake.WordWrites);
        }

        [Fact]
        public void Reset_CortexPart_WritesAircr()
        {
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            bool supported = DeviceResetter.TryReset(monitor, SamBaChipFamily.SamD21, cpuId: null);

            Assert.True(supported);
            Assert.Contains((0xE000ED0Cu, 0x05FA0004u), fake.WordWrites);
        }

        [Fact]
        public void Reset_EveryFamilyTheChipTableCanReport_HasARoute()
        {
            // DeviceResetter's lookup mirrors the chip table. A family added to one and not the
            // other makes SamBaDevice.Reset throw InvalidOperationException at a caller who did
            // nothing wrong, so the agreement is pinned here rather than left to a manual diff.
            // Unknown is excluded: it has a route only when the initial probe also confirmed a
            // Cortex-M CPUID (see Reset_UnknownFamilyWithConfirmedCortexM_StillWritesAircr below),
            // which this per-family sweep does not supply.
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            SamBaChipFamily[] unroutable = Enum.GetValues<SamBaChipFamily>()
                .Where(family => family != SamBaChipFamily.Unknown)
                .Where(family => !DeviceResetter.TryReset(monitor, family, cpuId: null))
                .ToArray();

            Assert.True(unroutable.Length == 0, $"No reset route for: {string.Join(", ", unroutable)}");
        }

        [Fact]
        public void Reset_UnknownFamilyWithConfirmedCortexM_StillWritesAircr()
        {
            // A part placed by family fallback (Unknown) is not nameless about its core: if the
            // initial probe read a CPUID, that alone confirms Cortex-M, and every Cortex-M carries
            // AIRCR at the same fixed architectural address regardless of family — so this part is
            // not left unresettable just because its family could not be named.
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            bool supported = DeviceResetter.TryReset(monitor, SamBaChipFamily.Unknown, cpuId: 0x412FC231);

            Assert.True(supported);
            Assert.Contains((0xE000ED0Cu, 0x05FA0004u), fake.WordWrites);
        }

        [Fact]
        public void Reset_UnknownFamilyWithNoCpuId_HasNoRoute()
        {
            // The legacy CHIPID-only branch never reads CPUID, so a fallback-placed part identified
            // that way (family Unknown, CpuId null) is not confirmed to be Cortex-M at all — nothing
            // justifies guessing AIRCR for it, so it must still report no route.
            FakeSamDevice fake = NewSamD21();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            bool supported = DeviceResetter.TryReset(monitor, SamBaChipFamily.Unknown, cpuId: null);

            Assert.False(supported);
        }

        [Fact]
        public void D2x_WaitReady_ToleratesTransientReadFailures()
        {
            // A busy part (SAMC21 ~6 ms page erase) can briefly fail to answer the status read; a
            // few consecutive failures should be retried, not fatal. Exercised through WriteBlock,
            // whose opening WaitReady is exactly the poll such a stall hits: the failed reads land
            // in the retry loop, then the write must go on to complete.
            FakeSamDevice fake = NewSamD21();
            var flaky = new FlakyReadTransport(fake);
            var monitor = new SambaMonitor(flaky);
            monitor.Connect();
            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            flaky.Armed = true;
            flaky.FailReadsRemaining = 2;  // below the 3-strike limit

            flash.WriteBlock(0, new byte[256], 0);  // must recover and complete

            // Recovered, not skipped: the row erase went through after the failed polls.
            Assert.Contains((0x41004000u, 0xA502u), fake.WordWrites);
        }

        [Fact]
        public void D2x_WaitReady_GivesUpAfterConsecutiveFailures()
        {
            var flaky = new FlakyReadTransport(NewSamD21());
            var monitor = new SambaMonitor(flaky);
            monitor.Connect();
            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            flaky.Armed = true;
            flaky.FailReadsRemaining = 3;  // hits the 3-strike limit

            Assert.Throws<SamBaTransportException>(() => flash.WriteBlock(0, new byte[256], 0));
        }

        [Fact]
        public void D5x_WaitReady_ToleratesTransientReadFailures()
        {
            // Sharing the NVMCTRL wait gives the D5x generation the retry tolerance that only the
            // D2x had: a few consecutive failed status reads are retried, not fatal.
            FakeSamDevice fake = NewSamE54();
            var flaky = new FlakyReadTransport(fake);
            var monitor = new SambaMonitor(flaky);
            monitor.Connect();
            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            flaky.Armed = true;
            flaky.FailReadsRemaining = 2;  // below the 3-strike limit

            flash.WriteBlock(0, new byte[8192], 0);  // must recover and complete

            // Recovered, not skipped: the block erase went through after the failed polls.
            Assert.Contains((0x41004004u, 0xA501u), fake.WordWrites);
        }

        [Fact]
        public void D5x_WaitReady_GivesUpAfterConsecutiveFailures()
        {
            var flaky = new FlakyReadTransport(NewSamE54());
            var monitor = new SambaMonitor(flaky);
            monitor.Connect();
            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            flaky.Armed = true;
            flaky.FailReadsRemaining = 3;  // hits the 3-strike limit

            Assert.Throws<SamBaTransportException>(() => flash.WriteBlock(0, new byte[8192], 0));
        }

        private const uint Sam3xFcr0 = Sam3xRegs + 0x004;
        private const uint Sam3xFcr1 = Sam3xRegs + 0x204;
        private const uint Sam3xFrr0 = Sam3xRegs + 0x00C;
        private const uint Sam3xFrr1 = Sam3xRegs + 0x20C;

        [Fact]
        public void Eefc_GetLockRegions_IssuesGlbPerPlane_AndDecodesFrrBits()
        {
            var (_, flash, fake) = OpenSam3x8e();

            // ATSAM3X8: 32 lock regions over 2 planes, so 16 per plane -> one FRR word each.
            fake.SetWord(Sam3xFrr0, 0x5);   // plane 0 regions 0 and 2 locked
            fake.SetWord(Sam3xFrr1, 0x2);   // plane 1 region 1, i.e. global region 17
            fake.WordWrites.Clear();

            bool[] regions = flash.GetLockRegions();

            Assert.Equal(32, regions.Length);
            Assert.True(regions[0]);
            Assert.False(regions[1]);
            Assert.True(regions[2]);
            Assert.False(regions[16]);
            Assert.True(regions[17]);
            Assert.Contains((Sam3xFcr0, 0x5A00000Au), fake.WordWrites);  // GLB on plane 0
            Assert.Contains((Sam3xFcr1, 0x5A00000Au), fake.WordWrites);  // GLB on plane 1
        }

        [Fact]
        public void Eefc_GetSecurityAndBootSource_ReadGpnvmBitsViaGgpb()
        {
            var (_, flash, fake) = OpenSam3x8e();

            // GGPB reports every GPNVM bit in FRR: bit 0 is security, bit 1 the ATSAM3X8 boot bit.
            fake.SetWord(Sam3xFrr0, 0x1);
            fake.WordWrites.Clear();

            Assert.True(flash.GetSecurity());
            Assert.Equal(SamBaChipBootSource.Rom, flash.GetBootSource());
            Assert.Contains((Sam3xFcr0, 0x5A00000Du), fake.WordWrites);  // GGPB

            fake.SetWord(Sam3xFrr0, 0x2);

            Assert.False(flash.GetSecurity());
            Assert.Equal(SamBaChipBootSource.Flash, flash.GetBootSource());
        }

        [Fact]
        public void Eefc_ApplyOptions_LockAll_IssuesSlbForEveryRegionFirstPage()
        {
            var (_, flash, fake) = OpenSam3x8e();

            fake.SetWord(Sam3xFrr0, 0);  // every region currently unlocked
            fake.SetWord(Sam3xFrr1, 0);
            fake.WordWrites.Clear();

            flash.ApplyOptions(new FlashOptionState { Lock = true });

            // 2048 pages / 32 regions = 64 pages per region, numbered within each plane.
            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8);
            Assert.Equal(32, slb.Count);
            Assert.Contains((Sam3xFcr0, 0x5A000008u), slb);  // region 0  -> page 0
            Assert.Contains((Sam3xFcr0, 0x5A03C008u), slb);  // region 15 -> page 960
            Assert.Contains((Sam3xFcr1, 0x5A000008u), slb);  // region 16 -> plane 1, page 0
            Assert.Contains((Sam3xFcr1, 0x5A03C008u), slb);  // region 31 -> plane 1, page 960
        }

        [Fact]
        public void Eefc_SetLockRegions_Subset_IssuesSlbOnlyForRequestedRegions()
        {
            var (_, flash, fake) = OpenSam3x8e();

            fake.SetWord(Sam3xFrr0, 0);  // every region currently unlocked
            fake.SetWord(Sam3xFrr1, 0);
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 1, 17 }, true);

            // Only the two named regions are read (one GLB each) and locked — not all 32.
            var glb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0xA);
            Assert.Equal(2, glb.Count);
            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8);
            Assert.Equal(2, slb.Count);
            Assert.Contains((Sam3xFcr0, 0x5A004008u), slb);  // region 1  -> page 64
            Assert.Contains((Sam3xFcr1, 0x5A004008u), slb);  // region 17 -> plane 1, page 64
        }

        [Fact]
        public void Eefc_SetLockRegions_RegionAlreadyInState_IssuesNoLockCommand()
        {
            var (_, flash, fake) = OpenSam3x8e();

            fake.SetWord(Sam3xFrr0, 0x2);  // region 1 already locked
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 1 }, true);

            Assert.Single(fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0xA));  // one GLB read
            Assert.Empty(fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8));   // no SLB issued
        }

        [Fact]
        public void Eefc_SetLockRegions_UnlockSubset_IssuesClb()
        {
            var (_, flash, fake) = OpenSam3x8e();

            fake.SetWord(Sam3xFrr0, 0x1);  // region 0 locked
            fake.SetWord(Sam3xFrr1, 0x1);  // region 16 locked, and not asked about below
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 0 }, false);

            var clb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x9);
            Assert.Single(clb);
            Assert.Contains((Sam3xFcr0, 0x5A000009u), clb);  // region 0 -> page 0
            Assert.Empty(fake.WordWrites.FindAll(w => w.Address == Sam3xFcr1));  // plane 1 untouched
        }

        [Fact]
        public void Eefc_ApplyOptions_LockSubset_LocksOnlyTheNamedRegions()
        {
            var (_, flash, fake) = OpenSam3x8e();

            fake.SetWord(Sam3xFrr0, 0);  // every region currently unlocked
            fake.SetWord(Sam3xFrr1, 0);
            fake.WordWrites.Clear();

            flash.ApplyOptions(new FlashOptionState { Lock = true, LockRegions = new[] { 1, 2 } });

            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x8);
            Assert.Equal(2, slb.Count);
            Assert.Contains((Sam3xFcr0, 0x5A004008u), slb);  // region 1 -> page 64
            Assert.Contains((Sam3xFcr0, 0x5A008008u), slb);  // region 2 -> page 128
        }

        [Fact]
        public void Eefc_ApplyOptions_LockSubsetOutOfRange_ThrowsBeforeAnyCommand()
        {
            // The option path routes a named subset through the same validation the public setter
            // uses, so a region index that arithmetic produced cannot become a command.
            var (_, flash, fake) = OpenSam3x8e();
            fake.WordWrites.Clear();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => flash.ApplyOptions(new FlashOptionState { Lock = true, LockRegions = new[] { 32 } }));

            Assert.Empty(fake.WordWrites);
        }

        [Fact]
        public void LockRegionsCovering_MapsByteRangesOntoWholeRegions()
        {
            var (_, flash, _) = OpenSam3x8e();  // 512 KB over 32 regions = 16 KB (0x4000) each

            Assert.Equal(new[] { 0 }, flash.LockRegionsCovering(0, 1));
            Assert.Equal(new[] { 0 }, flash.LockRegionsCovering(0, 0x4000));      // fills region 0
            Assert.Equal(new[] { 0, 1 }, flash.LockRegionsCovering(0, 0x4001));   // one byte over
            Assert.Equal(new[] { 1 }, flash.LockRegionsCovering(0x4000, 0x4000));

            // A tail landing part-way into a region still locks that whole region, and a range may
            // run across the plane boundary at region 16 without the arithmetic caring.
            Assert.Equal(new[] { 2 }, flash.LockRegionsCovering(0x9000, 0x100));
            Assert.Equal(new[] { 15, 16, 17 }, flash.LockRegionsCovering(0x3F000, 0x9000));
        }

        [Fact]
        public void LockRegionsCovering_EmptyRangeCoversNothing_AndTheEndClamps()
        {
            var (_, flash, _) = OpenSam3x8e();

            Assert.Empty(flash.LockRegionsCovering(0, 0));
            Assert.Equal(new[] { 31 }, flash.LockRegionsCovering(0x7FF00, 0x100));  // ends at flash end
            Assert.Equal(new[] { 31 }, flash.LockRegionsCovering(0x7FF00, 0x1000)); // past it: clamped
        }

        [Fact]
        public void LockRegionsCovering_NvmctrlPart_UsesItsOwnRegionSize()
        {
            var (_, flash, _) = OpenSamD21();  // 256 KB over 16 regions = 16 KB each, one plane

            Assert.Equal(new[] { 0 }, flash.LockRegionsCovering(0, 0x2000));
            Assert.Equal(new[] { 2, 3 }, flash.LockRegionsCovering(0x8000, 0x4001));
            Assert.Equal(new[] { 15 }, flash.LockRegionsCovering(0x3FF00, 0x100));
        }

        /// <summary>
        /// Fake wired as an ATSAM4S16: a single EEFC serving 128 lock regions — the shape where a
        /// region's lock bit lives past the first GLB result word, so reading it pages the FRR.
        /// </summary>
        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSam4s16()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);       // Cortex-M4 CPUID
            fake.SetWord(0x4, 0x00800125);              // reset vector into the SAM-BA ROM
            fake.SetWord(0x400E0740, 0x288C0CE0);       // CHIPID CIDR (ATSAM4S16, rev A)
            fake.SetWord(Sam3xRegs + 0x008, 1);         // the single EEFC's FSR ready
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));
            return (monitor, flash, fake);
        }

        [Fact]
        public void Eefc_SetLockRegions_RegionInSecondFrrWord_PagesForward()
        {
            // ATSAM4S16: 128 regions on one plane, four GLB result words. Region 40 is bit 8 of
            // the second word, so its read must pop the FRR twice before deciding.
            var (_, flash, fake) = OpenSam4s16();

            fake.WordWrites.Clear();
            fake.EnqueueWordRead(Sam3xFrr0, 0x0, 1u << 8);  // word 0, then word 1: region 40 locked

            flash.SetLockRegions(new[] { 40 }, false);

            // The bit decoded as locked, so the unlock went out: 2048 pages / 128 regions = 16
            // pages per region, region 40 -> page 640.
            var clb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x9);
            Assert.Single(clb);
            Assert.Contains((Sam3xFcr0, 0x5A028009u), clb);
        }

        [Fact]
        public void Eefc_EraseAll_PartialOffset_IssuesEpaPerPageGroup()
        {
            var (_, flash, fake) = OpenSam3x8e();
            fake.WordWrites.Clear();

            // 8 pages x 256 B = one 2 KB erase group; start one group in.
            flash.EraseAll(2048);

            var epa = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x7);
            Assert.Equal(255, epa.Count);                     // pages 8..2040, eight at a time
            Assert.Contains((Sam3xFcr0, 0x5A000907u), epa);   // page 8, group-of-8 selector in bits [1:0]
            Assert.Contains((Sam3xFcr1, 0x5A000107u), epa);   // page 1024 -> page 0 of plane 1
        }

        [Fact]
        public void Eefc_EraseAll_OffsetOffEraseGroupBoundary_Throws()
        {
            var (_, flash, _) = OpenSam3x8e();

            Assert.Throws<ArgumentOutOfRangeException>(() => flash.EraseAll(256));
        }

        [Fact]
        public void Eefc_EraseAll_OffsetPastFlashEnd_Throws()
        {
            // Aligned but out of range: without the check the EPA loop would silently erase
            // nothing. The ATSAM3X8E has 512 KB of flash; ask one erase group past it.
            var (_, flash, _) = OpenSam3x8e();

            Assert.Throws<ArgumentOutOfRangeException>(() => flash.EraseAll(512 * 1024 + 2048));
        }

        [Fact]
        public void Efc_GetLockRegions_DecodesFsrLockStatusBits()
        {
            var (_, flash, fake) = OpenSam7s512();

            // MC_FSR reports lock region N at bit 16+N, numbered within each plane.
            fake.SetWord(0xFFFFFF68, (1u << 16) | 1u);  // plane 0 region 0 locked (+ FRDY)
            fake.SetWord(0xFFFFFF78, (1u << 17) | 1u);  // plane 1 region 1, i.e. global region 17

            bool[] regions = flash.GetLockRegions();

            Assert.Equal(32, regions.Length);
            Assert.True(regions[0]);
            Assert.False(regions[1]);
            Assert.False(regions[16]);
            Assert.True(regions[17]);
        }

        [Fact]
        public void Efc_SetLockRegions_Subset_IssuesSlbWithPlaneLocalPage()
        {
            var (_, flash, fake) = OpenSam7s512();

            fake.SetWord(0xFFFFFF68, (1u << 16) | 1u);  // plane 0: region 0 already locked (+ FRDY)
            fake.SetWord(0xFFFFFF78, 1u);               // plane 1: nothing locked
            fake.WordWrites.Clear();

            flash.SetLockRegions(new[] { 0, 17 }, true);

            // Region 0 is already locked, so only region 17 draws a command: SLB on EFC1 with the
            // region's first page counted within its plane — 2048 pages / 32 regions = 64.
            var slb = fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x2);
            Assert.Single(slb);
            Assert.Contains((0xFFFFFF74u, 0x5A004002u), slb);
            Assert.Empty(fake.WordWrites.FindAll(w => (w.Value & 0xFF) == 0x4));  // and no CLB
        }

        [Fact]
        public void Efc_GetSecurity_ReadsFsrSecurityBit()
        {
            var (_, flash, fake) = OpenSam7s256();

            Assert.False(flash.GetSecurity());

            fake.SetWord(Efc0Fsr, (1u << 4) | 1u);

            Assert.True(flash.GetSecurity());
        }

        [Fact]
        public void Efc_FixedSourcePart_ReportsFlash_AndRefusesOnlyTheRom()
        {
            // The AT91SAM7S512 has no boot-mode GPNVM bit because it has nothing to select: it always
            // boots flash, an erase having copied SAM-BA into flash for it to relocate into RAM.
            //
            // This test asserted the reverse until the rename — Rom, and a throw for Flash. That is the
            // reading the old CanBootFlash name invited ("cannot boot flash"), and it inverted all three
            // boot members on these parts while looking deliberate here.
            var (_, flash, fake) = OpenSam7s512();

            Assert.False(flash.CanSelectBootSource);
            Assert.Equal(SamBaChipBootSource.Flash, flash.GetBootSource());

            // The source it already uses: nothing to reach, nothing to refuse, no command issued.
            fake.WordWrites.Clear();
            flash.ApplyOptions(new FlashOptionState { BootSource = SamBaChipBootSource.Flash });
            Assert.Empty(fake.WordWrites);

            // The ROM is the one it cannot be pointed at. Same rule as every family: only the
            // unreachable source throws.
            var ex = Assert.Throws<SamBaFlashCommandException>(
                () => flash.ApplyOptions(new FlashOptionState { BootSource = SamBaChipBootSource.Rom }));
            Assert.Contains("SAM-BA ROM", ex.Message);
            Assert.True(ex.IsUnsupported);
            Assert.Equal("boot-source change", ex.Operation);
        }

        [Fact]
        public void Efc_And_Nvm_AgreeOnWhatNoBootModeBitMeans()
        {
            // The regression guard for the inversion. Two families reach the no-selectable-bit case for
            // different hardware reasons — a SAM7S boots the SAM-BA copy an erase put in flash, an
            // NVMCTRL part has no boot ROM at all — and both must answer Flash. They disagreed before,
            // one Rom and one Flash, which is what made the wrong answer look intentional.
            var (_, sam7s, _) = OpenSam7s512();
            var (_, d2x, _) = OpenSamD21();
            var (_, d5x, _) = OpenSame54();

            foreach (FlashController flash in new FlashController[] { sam7s, d2x, d5x })
            {
                Assert.False(flash.CanSelectBootSource);
                Assert.Equal(SamBaChipBootSource.Flash, flash.GetBootSource());
            }
        }

        [Fact]
        public void Efc_BootSource_ReadsGpnvmStatusBit_AndIssuesCgpbToPointAtTheRom()
        {
            // The AT91SAM7SE512 selects its boot source with GPNVM2, reported in MC_FSR bit 8+2.
            var (_, flash, fake) = OpenSam7se512();

            fake.SetWord(0xFFFFFF68, (1u << 10) | 1u);  // GPNVM2 set (+ FRDY)
            Assert.True(flash.CanSelectBootSource);
            Assert.Equal(SamBaChipBootSource.Flash, flash.GetBootSource());
            fake.WordWrites.Clear();

            flash.ApplyOptions(new FlashOptionState { BootSource = SamBaChipBootSource.Rom });

            Assert.Contains((0xFFFFFF64u, 0x5A00020Du), fake.WordWrites);  // CGPB, GPNVM bit 2
        }

        [Fact]
        public void Efc_SetAutoEraseOff_SetsNoEraseBeforeProgramBit()
        {
            var (_, flash, fake) = OpenSam7s256();
            fake.WordWrites.Clear();

            flash.SetAutoErase(false);

            // NEBP is FMR bit 7; initialization already programmed the FMCN timing (0x00340000),
            // so the timing must survive the mode change untouched.
            Assert.Contains((Efc0Fmr, 0x00340080u), fake.WordWrites);
            Assert.False(flash.AutoEraseEnabled);
        }

        [Fact]
        public void Efc_Init_ProgramsEveryPlaneFmrIdentically()
        {
            var (_, _, fake) = OpenSam7s512();

            // Initialization programs the FMCN timing with NEBP clear, and
            // the second plane's controller must receive the same value as the first.
            Assert.Contains((Efc0Fmr, 0x00340000u), fake.WordWrites);
            Assert.Contains((Efc1Fmr, 0x00340000u), fake.WordWrites);
        }

        [Fact]
        public void Efc_EraseAll_NonZeroOffset_Throws()
        {
            // The legacy EFC has no partial-erase command, so only a full chip erase exists.
            var (_, flash, _) = OpenSam7s256();

            Assert.Throws<ArgumentOutOfRangeException>(() => flash.EraseAll(0x1000));
        }

        [Fact]
        public void WaitFsr_FsrNeverReady_ThrowsTimeoutReportingTheBudgetItWaited()
        {
            var (_, flash, fake) = OpenSam3x8e();

            fake.SetWord(Sam3xRegs + 0x008, 0);  // EEFC0 FSR: FRDY never comes up
            fake.SetWord(Sam3xRegs + 0x208, 0);  // EEFC1 FSR

            var stopwatch = Stopwatch.StartNew();
            var ex = Assert.Throws<SamBaFlashTimeoutException>(
                () => flash.WriteBlock(0, new byte[256], 0));
            stopwatch.Stop();

            Assert.Equal(nameof(FlashController.WriteBlock), ex.Operation);
            Assert.Equal(SambaMonitor.NormalTimeout, ex.Timeout);
            // The budget is wall-clock, not a poll count, so the reported figure is the time
            // actually spent waiting rather than a number of iterations.
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(15));
        }

        /// <summary>
        /// Scripts one EEFC's GETD reply on its FRR: FL_ID, FL_SIZE, FL_PAGE_SIZE, FL_NB_PLANE (1 —
        /// each EEFC describes its own bank), that one plane's size, FL_NB_LOCK.
        /// </summary>
        private static void EnqueueDescriptor(FakeSamDevice fake, uint frr, uint size, uint pageSize, uint locks)
            => fake.EnqueueWordRead(frr, 0x00112233, size, pageSize, 1, size, locks);

        [Fact]
        public void Eefc_GeometryProbe_MatchingDescriptor_AgreesSilently()
        {
            // The device's own account of its geometry (GETD, one descriptor per EEFC) matches the
            // table row, so nothing is flagged — the common case on healthy listed parts.
            FakeSamDevice fake = NewSam3x8e();
            EnqueueDescriptor(fake, Sam3xFrr0, size: 0x40000, pageSize: 256, locks: 16);
            EnqueueDescriptor(fake, Sam3xFrr1, size: 0x40000, pageSize: 256, locks: 16);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            Assert.Null(flash.MismatchedDeviceGeometry);
            Assert.Equal(2048, flash.PageCount);
            Assert.Contains((Sam3xFcr0, 0x5A000000u), fake.WordWrites);  // GETD on plane 0
            Assert.Contains((Sam3xFcr1, 0x5A000000u), fake.WordWrites);  // GETD on plane 1
        }

        [Fact]
        public void Eefc_GeometryProbe_DisagreeingDescriptor_KeepsTheTableAndFlagsIt()
        {
            // A sane descriptor that contradicts the table row. The table wins — the descriptor
            // read is unverified on real silicon and geometry decides what gets erased — but the
            // disagreement is kept so the device layer can warn instead of it vanishing.
            FakeSamDevice fake = NewSam3x8e();
            EnqueueDescriptor(fake, Sam3xFrr0, size: 0x40000, pageSize: 512, locks: 16);
            EnqueueDescriptor(fake, Sam3xFrr1, size: 0x40000, pageSize: 512, locks: 16);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            Assert.NotNull(flash.MismatchedDeviceGeometry);
            Assert.Equal(512, flash.MismatchedDeviceGeometry.Value.PageSize);
            Assert.Equal(1024, flash.MismatchedDeviceGeometry.Value.PageCount);
            Assert.Equal(256, flash.PageSize);      // the table stands
            Assert.Equal(2048, flash.PageCount);
        }

        [Fact]
        public void Eefc_GeometryProbe_UnreadableDescriptor_FallsBackToTheTableSilently()
        {
            // Nothing scripted on FRR, so the probe reads a descriptor of zeros — what a fake, a
            // dead part or a monitor without GETD produces. The gate rejects it and the table
            // stands with nothing flagged; every pre-existing fixture in this file rides this path.
            var (_, flash, _) = OpenSam3x8e();

            Assert.Null(flash.MismatchedDeviceGeometry);
            Assert.Equal(2048, flash.PageCount);
            Assert.Equal(256, flash.PageSize);
        }

        [Fact]
        public void LegacyEfc_GeometryProbe_WithDevicePrecedence_FlagsThatPrecedenceHadNoEffect()
        {
            // The legacy MC/EFC (pre-GETD) never overrides ReadDeviceGeometry, so it always
            // returns null — there is nothing for Device precedence to adopt. That must be
            // distinguished from Eefc_GeometryProbe_UnreadableDescriptor_FallsBackToTheTableSilently,
            // where something did answer (with zeros); this is the "nothing answered at all" case.
            FakeSamDevice fake = NewSam7s256();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);

            FlashController flash = FlashController.Create(monitor, identity, SamBaGeometryPrecedence.Device);

            Assert.True(flash.DevicePrecedenceHadNoEffect);
            Assert.Null(flash.MismatchedDeviceGeometry);
            Assert.Null(flash.UnusableDeviceGeometry);
            Assert.Equal(1024, flash.PageCount);    // the table stands, same as under Table precedence
            Assert.Equal(256, flash.PageSize);
        }

        [Fact]
        public void Eefc_GeometryProbe_MatchingDescriptor_WithDevicePrecedence_DoesNotFlagNoEffect()
        {
            // Contrast case: the part does answer and agrees, so Device precedence had a real (if
            // uneventful) chance to apply — DevicePrecedenceHadNoEffect must stay false here, not
            // just whenever precedence is Device.
            FakeSamDevice fake = NewSam3x8e();
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            EnqueueDescriptor(fake, Sam3xFrr0, size: 0x80000, pageSize: 256, locks: 128);
            EnqueueDescriptor(fake, Sam3xFrr1, size: 0x80000, pageSize: 256, locks: 128);

            FlashController flash = FlashController.Create(monitor, identity, SamBaGeometryPrecedence.Device);

            Assert.False(flash.DevicePrecedenceHadNoEffect);
        }

        [Fact]
        public void Eefc_GeometryProbe_FrrStuckAtOne_IsRejectedByTheGate()
        {
            // Two firmware-update tests preset FRR0 = 1 before opening (a lock-bit fixture). The
            // probe then reads a "descriptor" of all ones — internally consistent (1 plane of 1
            // byte, 1 lock region) but with a 1-byte page size the gate must reject. The open has
            // to survive on table geometry, or those fixtures would start failing in Create.
            FakeSamDevice fake = NewSam3x8e();
            fake.SetWord(Sam3xFrr0, 1);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            Assert.Null(flash.MismatchedDeviceGeometry);
            Assert.Equal(256, flash.PageSize);
        }

        [Fact]
        public void Eefc_GeometryProbe_PlanesThatDisagree_AreRefused()
        {
            // Each descriptor is sane on its own and their sizes even sum to the table's 512 KB, but
            // the two controllers describe different banks. The flash layer reaches a second
            // controller by halving the totals, so an asymmetric pair has no representation at all —
            // summing it would name the wrong page for every write above the plane boundary.
            FakeSamDevice fake = NewSam3x8e();
            EnqueueDescriptor(fake, Sam3xFrr0, size: 0x60000, pageSize: 256, locks: 24);
            EnqueueDescriptor(fake, Sam3xFrr1, size: 0x20000, pageSize: 256, locks: 8);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            Assert.Null(flash.MismatchedDeviceGeometry);     // refused, so there is nothing to report
            Assert.Equal(2048, flash.PageCount);             // the table stands
            Assert.Equal(32, flash.LockRegionCount);
        }

        [Fact]
        public void ReadRange_ReadsFromAnyOffset_AndRefusesToRunPastTheEnd()
        {
            // The single flash read path: unaligned and unblocked, because a read disturbs nothing —
            // which is what lets a verify start wherever the image was written.
            var (_, flash, fake) = OpenSam3x8e();
            fake.SetBytes(Sam3xFlash + 300, new byte[] { 1, 2, 3, 4 });

            var buffer = new byte[6];
            flash.ReadRange(299, buffer, 1, 4);

            Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 0 }, buffer);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => flash.ReadRange((uint)flash.FlashSize - 2, new byte[4], 0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => flash.ReadRange(0, new byte[4], 0, 5));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nvm_GeometryProbe_MatchingParam_AgreesSilently(bool d5x)
        {
            // NVMCTRL PARAM layout is shared by both generations: NVMP pages in bits 15:0, page
            // size 8 << PSZ in bits 18:16. SAMD21x18 = 4096 x 64 B (PSZ 3); SAME54x20 = 2048 x
            // 512 B (PSZ 6).
            FakeSamDevice fake = d5x ? NewSamE54() : NewSamD21();
            uint param = d5x ? 2048u | (6u << 16) : 4096u | (3u << 16);
            fake.SetWord(0x41004008, param);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            Assert.Null(flash.MismatchedDeviceGeometry);
            Assert.Equal(d5x ? 512 : 64, flash.PageSize);
        }

        [Fact]
        public void Nvm_GeometryProbe_DisagreeingParam_KeepsTheTableAndFlagsIt()
        {
            // PARAM claims half the pages the table row carries — a sane answer, so it survives
            // the gate and is flagged; the table still decides what the controller runs on.
            FakeSamDevice fake = NewSamD21();
            fake.SetWord(0x41004008, 2048u | (3u << 16));
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(monitor, ChipIdentifier.Identify(monitor));

            Assert.NotNull(flash.MismatchedDeviceGeometry);
            Assert.Equal(2048, flash.MismatchedDeviceGeometry.Value.PageCount);
            Assert.Equal(4096, flash.PageCount);    // the table stands
            Assert.NotNull(flash.MismatchedTableGeometry);
            Assert.Equal(4096, flash.MismatchedTableGeometry.Value.PageCount);
        }

        [Fact]
        public void Nvm_GeometryProbe_DisagreeingParam_WithDevicePrecedence_KeepsTableSnapshot()
        {
            // Same disagreement as Nvm_GeometryProbe_DisagreeingParam_KeepsTheTableAndFlagsIt, but
            // with Device precedence: the running geometry (PageCount/Record) adopts the device's
            // account, while MismatchedTableGeometry must still hold the table's real figure —
            // it must not silently track whatever Chip/Record ends up holding.
            FakeSamDevice fake = NewSamD21();
            fake.SetWord(0x41004008, 2048u | (3u << 16));
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            FlashController flash = FlashController.Create(
                monitor, ChipIdentifier.Identify(monitor), SamBaGeometryPrecedence.Device);

            Assert.NotNull(flash.MismatchedDeviceGeometry);
            Assert.Equal(2048, flash.MismatchedDeviceGeometry.Value.PageCount);
            Assert.Equal(2048, flash.PageCount);    // the device's account won
            Assert.NotNull(flash.MismatchedTableGeometry);
            Assert.Equal(4096, flash.MismatchedTableGeometry.Value.PageCount);    // the table's real figure
        }

        [Fact]
        public void Identify_UnlistedEefcPart_AdoptsTheDeviceReportedGeometry()
        {
            // A CIDR no row matches, in a family the table knows (ARCH 0x8A — the SAM3S/SAM4S rev C
            // rows, which agree on the EEFC base and flash address). Identification hands back a
            // provisional record and Create completes it from the part's own flash descriptor, so
            // the unlisted part opens instead of throwing.
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);       // Cortex-M4 CPUID
            fake.SetWord(0x4, 0x00800125);              // reset vector into the SAM-BA ROM
            fake.SetWord(0x400E0740, 0x28AB0000);       // unknown CHIPID, known family
            fake.SetWord(Sam3xRegs + 0x008, 1);         // EEFC0 FSR ready; no EEFC1 -> single plane
            EnqueueDescriptor(fake, Sam3xFrr0, size: 0x80000, pageSize: 512, locks: 128);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);

            Assert.Equal(SamBaChipFamily.Unknown, flash.Record.Family);
            Assert.Equal("Unknown SAM (CHIPID 0x28AB0000)", flash.Name);
            Assert.Equal(0x400000u, flash.FlashAddress);    // the family's flash base
            Assert.Equal(1024, flash.PageCount);            // everything below: the device's account
            Assert.Equal(512, flash.PageSize);
            Assert.Equal(1, flash.PlaneCount);
            Assert.Equal(128, flash.LockRegionCount);
            Assert.Null(flash.MismatchedDeviceGeometry);    // adopted, so nothing to disagree with
        }

        [Fact]
        public void Identify_UnlistedEfcPart_DecodesFlashSizeFromItsOwnChipId()
        {
            // The legacy EFC has no descriptor command, but its CIDR carries NVPSIZ — which agrees
            // with every listed EFC row — and the family's rows pin the shape that goes with that
            // size. ARCH 0x70 is the SAM7S family; NVPSIZ 9 is 256 KB, which its rows carry as
            // 1024 x 256 B, 1 plane, 16 lock regions.
            var fake = new FakeSamDevice();
            fake.SetWord(0x0, 0xEA000000);          // reset vector branch -> SAM7/9 probe path
            fake.SetWord(0xFFFFF240, 0x27000900);   // unknown CHIPID: ARCH 0x70, NVPTYP 2, NVPSIZ 9
            fake.SetWord(0xFFFFFF68, 1);            // EFC0 FSR ready
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);

            Assert.Equal(SamBaChipFamily.Unknown, flash.Record.Family);
            Assert.Equal(0x100000u, flash.FlashAddress);    // the SAM7 family's flash base
            Assert.Equal(256 * 1024, flash.FlashSize);      // decoded from NVPSIZ
            Assert.Equal(256, flash.PageSize);              // the shape its siblings carry at 256 KB
            Assert.Equal(1024, flash.PageCount);
            Assert.Equal(1, flash.PlaneCount);
            Assert.Equal(16, flash.LockRegionCount);
        }

        [Theory]
        // NVPSIZ 4 is reserved — not a size at all.
        [InlineData(0x27000400u)]
        // NVPSIZ 1 is 8 KB: a real encoding, but no SAM7S row carries it, so no shape to borrow.
        [InlineData(0x27000100u)]
        // NVPTYP 3 is ROM beside flash, where NVPSIZ describes the ROM rather than the flash.
        [InlineData(0x37000900u)]
        // NVPTYP 7 with ARCH 0x14 — the shape of a bootloader's synthetic id.
        [InlineData(0x714E3100u)]
        public void Identify_UnlistedEfcPart_WithNoTrustworthySize_IsStillUnsupported(uint chipId)
        {
            // Each of these could only be served by inventing geometry, so the fallback refuses and
            // the part stays unsupported rather than being programmed on a guess.
            var fake = new FakeSamDevice();
            fake.SetWord(0x0, 0xEA000000);
            fake.SetWord(0xFFFFF240, chipId);
            fake.SetWord(0xFFFFFF68, 1);
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            Assert.Throws<SamBaUnsupportedDeviceException>(() => ChipIdentifier.Identify(monitor));
        }

        [Fact]
        public void Identify_UnlistedDsuPart_AdoptsTheParamGeometry()
        {
            // Same story on the NVMCTRL side: an unknown DID on a Cortex-M0+ part gets a
            // provisional D2x record — the generation follows from the core, the lock-region count
            // is architectural — and PARAM supplies the page geometry.
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410CC600);           // Cortex-M0+ CPUID
            fake.SetWord(0x41002018, 0x11223344);           // unknown DSU DID
            fake.SetWord(0x41004008, 1024u | (3u << 16));   // PARAM: 1024 pages x 64 B
            fake.SetWord(0x41004014, 1);                    // NVMCTRL INTFLAG READY
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);

            Assert.Equal(SamBaChipFamily.Unknown, flash.Record.Family);
            Assert.Equal("Unknown SAM (DSU DID 0x11223344)", flash.Name);
            Assert.Equal(0u, flash.FlashAddress);
            Assert.Equal(1024, flash.PageCount);
            Assert.Equal(64, flash.PageSize);
            Assert.Equal(16, flash.LockRegionCount);        // architectural on the D2x generation

            // The serial-number addresses are not adopted the way the lock-region count is: a part
            // no row matches has not said its id lives where the listed D2x parts keep theirs, so
            // reading there would report whatever is at those addresses as this device's identity.
            Assert.Empty(flash.GetUniqueId());
        }

        [Fact]
        public void Identify_UnlistedPart_WithNoUsableDescriptor_IsStillUnsupported()
        {
            // The family fallback only defers the verdict: a part that matches no row AND cannot
            // describe itself has nothing to run on, and must be rejected before initialization
            // divides by the zero geometry.
            var fake = new FakeSamDevice();
            fake.SetWord(0xE000ED00, 0x410FC240);
            fake.SetWord(0x4, 0x00800125);
            fake.SetWord(0x400E0740, 0x28AB0000);           // unknown CHIPID, known family
            fake.SetWord(Sam3xRegs + 0x008, 1);             // FSR ready, but FRR reads zeros
            var monitor = new SambaMonitor(fake);
            monitor.Connect();

            ChipIdentity identity = ChipIdentifier.Identify(monitor);

            var ex = Assert.Throws<SamBaUnsupportedDeviceException>(
                () => FlashController.Create(monitor, identity));
            Assert.Contains("0x28AB0000", ex.Message);
            Assert.Contains("no usable geometry", ex.Message);

            // The words the probe read travel with the rejection, exactly as they do when no family
            // matches either: this is the report someone files about a part that would not open, and
            // null on these properties means the register went unread — which is not what happened.
            Assert.Equal(0x28AB0000u, ex.ChipId);
            Assert.Equal(0x410FC240u, ex.CpuId);
            Assert.Equal(0u, ex.ExtendedChipId);
            Assert.Null(ex.DeviceId);
        }

        /// <summary>Fake wired as an AT91SAM7SE512: legacy MC/EFC, 2 planes, boot GPNVM bit 2.</summary>
        private static (SambaMonitor monitor, FlashController flash, FakeSamDevice fake) OpenSam7se512()
        {
            var fake = new FakeSamDevice();
            fake.SetWord(0x0, 0xEA000000);          // reset vector branch -> SAM7/9 probe path
            fake.SetWord(0xFFFFF240, 0x272A0A40);   // CHIPID (AT91SAM7SE512, 2 planes)
            fake.SetWord(0xFFFFFF68, 1);            // EFC0 FSR ready
            fake.SetWord(0xFFFFFF78, 1);            // EFC1 FSR ready
            var monitor = new SambaMonitor(fake);
            monitor.Connect();
            ChipIdentity identity = ChipIdentifier.Identify(monitor);
            FlashController flash = FlashController.Create(monitor, identity);
            return (monitor, flash, fake);
        }

        /// <summary>
        /// Wraps a <see cref="FakeSamDevice"/> and, once <see cref="Armed"/>, throws a transport
        /// error from the next <see cref="FailReadsRemaining"/> word-reply reads to simulate a
        /// briefly unresponsive monitor.
        /// </summary>
        private sealed class FlakyReadTransport : ISambaTransport
        {
            private readonly FakeSamDevice _inner;

            public FlakyReadTransport(FakeSamDevice inner) => _inner = inner;

            public bool Armed { get; set; }
            public int FailReadsRemaining { get; set; }

            public string DevicePath => _inner.DevicePath;
            public bool IsOpen => _inner.IsOpen;
            public Action<string>? StatusReporter
            {
                get => _inner.StatusReporter;
                set => _inner.StatusReporter = value;
            }
            public void Open() => _inner.Open();
            public void Close() => _inner.Close();
            public void Write(byte[] buffer, int offset, int count, TimeSpan timeout) => _inner.Write(buffer, offset, count, timeout);
            public int Read(byte[] buffer, int offset, int count, TimeSpan timeout) => _inner.Read(buffer, offset, count, timeout);
            public void Purge() => _inner.Purge();
            public void Dispose() => _inner.Dispose();

            public void ReadExact(byte[] buffer, int offset, int count, TimeSpan timeout)
            {
                if (Armed && FailReadsRemaining > 0)
                {
                    FailReadsRemaining--;
                    throw new SamBaTransportException(DevicePath, "ReadExact", "Simulated transient read failure.");
                }

                _inner.ReadExact(buffer, offset, count, timeout);
            }
        }
    }
}

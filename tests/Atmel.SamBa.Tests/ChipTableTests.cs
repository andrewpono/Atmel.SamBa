using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Chips;
using Xunit;

namespace Anp.Atmel.SamBa.Tests
{
    public class ChipTableTests
    {
        [Fact]
        public void Rows_MatchExpectedCount()
        {
            // 170 base rows (a part with several geometry-sharing variants contributes several
            // rows; SAM4E's 4 rows come from its nested EXID switch), plus 23 later additions:
            // 14 SAMC21 and 9 extra SAMD21 D/L package variants, less the 14 DBGU/UART-only rows
            // disabled in the table (12 SAM3N, 2 SAM7L), plus the 2 48-pin
            // ATSAM3S8A/ATSAM3SD8A rows the SAM3S datasheet lists and the original table omitted,
            // plus the 2 ATSAME51G18A/G19A rows that only the 2023 family errata identifies, plus
            // the 2 UUT-packaged ATSAMC21J17AU/J18AU rows from the SAM C20/C21 errata. The
            // emulated-bootloader row is disabled too, so like the SAM3N and SAM7L rows it never
            // enters this count.
            // A change to this count should be a deliberate edit to the table, not an accident.
            Assert.Equal(185, ChipTable.Rows.Count);
        }

        [Fact]
        public void Rows_HaveUniqueMatchKeys()
        {
            var seen = new HashSet<(ChipKeyKind, uint, uint)>();
            foreach (ChipRecord row in ChipTable.Rows)
                Assert.True(seen.Add((row.KeyKind, row.Key, row.ExtendedKey)), $"Duplicate key: {row.Name} 0x{row.Key:X8}/0x{row.ExtendedKey:X8}");
        }

        [Fact]
        public void Rows_HaveSaneGeometry()
        {
            foreach (ChipRecord row in ChipTable.Rows)
            {
                Assert.False(string.IsNullOrEmpty(row.Name));
                Assert.True(row.PageCount > 0, row.Name);
                Assert.True(row.PageSize > 0 && (row.PageSize & (row.PageSize - 1)) == 0, row.Name);
                Assert.True(row.PlaneCount == 1 || row.PlaneCount == 2, row.Name);
                Assert.True(row.LockRegionCount > 0, row.Name);
                if (row.ControllerKind == FlashControllerKind.Eefc)
                    Assert.True(row.FlashControllerBaseAddress != 0, row.Name);
                if (row.ControllerKind == FlashControllerKind.D2xNvm
                    || row.ControllerKind == FlashControllerKind.D5xNvm)
                    Assert.Equal(0u, row.FlashAddress);
            }
        }

        [Fact]
        public void Rows_HaveKeysAlreadyMasked()
        {
            // Every key is masked by hand where it is written down, and TryFind masks the probe value
            // the same way so that matching can be a plain equality. A key entered with any of the
            // dropped bits left in can therefore never match — silently, because no masked probe
            // value can carry them — and neither the uniqueness nor the geometry check above would
            // notice. Only about a dozen rows have a probe test of their own; this covers all 186.
            foreach (ChipRecord row in ChipTable.Rows)
            {
                uint mask = row.KeyKind == ChipKeyKind.ChipId
                    ? ChipTable.ChipIdKeyMask
                    : ChipTable.DeviceIdKeyMask;

                Assert.True(
                    (row.Key & mask) == row.Key,
                    $"{row.Name}: key 0x{row.Key:X8} carries bits outside mask 0x{mask:X8}.");
            }
        }

        [Fact]
        public void Rows_HaveGeometryTheFlashLayerCanDivideEvenly()
        {
            // Three integer divisions in the flash layer take their divisibility from this table and
            // truncate silently without it.
            foreach (ChipRecord row in ChipTable.Rows)
            {
                // WriteBlockCount = PageCount / PagesPerWriteBlock. A remainder leaves a tail of pages
                // inside FlashSize but outside every block, so FlashProgrammer accepts a whole-image
                // write on the size check and then throws on the last block — after programming the
                // rest. Half-written flash from a validation that passed.
                Assert.True(
                    row.PageCount % row.PagesPerWriteBlock == 0,
                    $"{row.Name}: {row.PageCount} pages is not a multiple of the " +
                    $"{row.PagesPerWriteBlock}-page write block.");

                // EfcFamilyController.FirstPageOfRegion = region * PageCount / LockRegionCount. A
                // remainder drifts the region boundaries away from the hardware's.
                Assert.True(
                    row.PageCount % row.LockRegionCount == 0,
                    $"{row.Name}: {row.PageCount} pages does not divide evenly into " +
                    $"{row.LockRegionCount} lock regions.");

                // LockRegionsPerPlane = LockRegionCount / 2 on a two-plane part, which loses a region
                // if the count is odd.
                if (row.PlaneCount == 2)
                    Assert.True(
                        row.LockRegionCount % 2 == 0,
                        $"{row.Name}: {row.LockRegionCount} lock regions cannot split across 2 planes.");
            }
        }

        [Fact]
        public void FamilyDescriptors_MatchTheDatasheets()
        {
            // A second copy of the values, on purpose, for the same reason NonVolatileSizeKb has one:
            // the table under test is now the only place each family's controller facts are written, so
            // checking it against rows built from it would prove nothing. Before this, only the SAM3S/
            // 3X/3A/4S/4E base (0x400E0A00) was asserted anywhere — one typo in any of the others moves
            // an entire family's flash controller, and the SAM3N and SAM7L entries have no enabled rows
            // to notice through at all.
            var expected = new (
                SamBaChipFamily Family, FlashControllerKind ControllerKind,
                uint BaseAddress, int? BootBit, int UniqueIdWords)[]
            {
                // Legacy EFC: fixed controller address the EFC driver knows, no unique-id command,
                // GPNVM2 boot select except on the SAM7S, which cannot boot from flash.
                (SamBaChipFamily.Sam7L, FlashControllerKind.Efc, 0, 1, 0),
                (SamBaChipFamily.Sam7S, FlashControllerKind.Efc, 0, null, 0),
                (SamBaChipFamily.Sam7Se, FlashControllerKind.Efc, 0, 2, 0),
                (SamBaChipFamily.Sam7X, FlashControllerKind.Efc, 0, 2, 0),
                (SamBaChipFamily.Sam7Xc, FlashControllerKind.Efc, 0, 2, 0),

                // EEFC on GPNVM1, unique id where the controller implements the command.
                (SamBaChipFamily.Sam3A, FlashControllerKind.Eefc, 0x400E0A00, 1, 4),
                (SamBaChipFamily.Sam3N, FlashControllerKind.Eefc, 0x400E0A00, 1, 4),
                (SamBaChipFamily.Sam3S, FlashControllerKind.Eefc, 0x400E0A00, 1, 4),
                (SamBaChipFamily.Sam3U, FlashControllerKind.Eefc, 0x400E0800, 1, 4),
                (SamBaChipFamily.Sam3X, FlashControllerKind.Eefc, 0x400E0A00, 1, 4),
                (SamBaChipFamily.Sam4E, FlashControllerKind.Eefc, 0x400E0A00, 1, 4),
                (SamBaChipFamily.Sam4S, FlashControllerKind.Eefc, 0x400E0A00, 1, 4),
                (SamBaChipFamily.SamE70, FlashControllerKind.Eefc, 0x400E0C00, 1, 4),
                (SamBaChipFamily.SamS70, FlashControllerKind.Eefc, 0x400E0C00, 1, 4),
                (SamBaChipFamily.SamV70, FlashControllerKind.Eefc, 0x400E0C00, 1, 4),
                (SamBaChipFamily.SamV71, FlashControllerKind.Eefc, 0x400E0C00, 1, 4),

                // The one EEFC family that reads back no unique id and boots from GPNVM3 instead.
                (SamBaChipFamily.Sam9Xe, FlashControllerKind.Eefc, 0xFFFFFA00, 3, 0),

                // NVMCTRL: no EEFC-equivalent base, no boot GPNVM bit, four unique-id words on either
                // generation — nothing here varies by family, only by controller generation, which
                // FamilyDescriptors_ArchEprocDidPatternsMatchTheTable checks separately.
                (SamBaChipFamily.SamC21, FlashControllerKind.D2xNvm, 0, null, 4),
                (SamBaChipFamily.SamD21, FlashControllerKind.D2xNvm, 0, null, 4),
                (SamBaChipFamily.SamL21, FlashControllerKind.D2xNvm, 0, null, 4),
                (SamBaChipFamily.SamR21, FlashControllerKind.D2xNvm, 0, null, 4),
                (SamBaChipFamily.SamD51, FlashControllerKind.D5xNvm, 0, null, 4),
                (SamBaChipFamily.SamE51, FlashControllerKind.D5xNvm, 0, null, 4),
                (SamBaChipFamily.SamE53, FlashControllerKind.D5xNvm, 0, null, 4),
                (SamBaChipFamily.SamE54, FlashControllerKind.D5xNvm, 0, null, 4),
            };

            foreach (var e in expected)
            {
                FamilyDescriptor d = Families.Of(e.Family);
                Assert.Equal(e.ControllerKind, d.ControllerKind);
                Assert.Equal(e.BaseAddress, d.FlashControllerBaseAddress);
                Assert.Equal(e.BootBit, d.BootGpnvmBitIndex);
                Assert.Equal(e.UniqueIdWords, d.UniqueIdWords);
            }

            // Every family the enum names has to appear above, bar Unknown — Families now covers
            // every family, EFC/EEFC and NVMCTRL alike (TraitsOf used to cover only the former), so
            // an enum member added without teaching Families about it fails here rather than
            // throwing InvalidOperationException on the first part that reports it.
            foreach (SamBaChipFamily family in Enum.GetValues(typeof(SamBaChipFamily)))
            {
                if (family == SamBaChipFamily.Unknown)
                    continue;

                Assert.Contains(family, expected.Select(e => e.Family));
            }
        }

        [Fact]
        public void FamilyDescriptors_ArchEprocDidPatternsMatchTheTable()
        {
            // The mechanical-derivation safety net: recompute each family's (ARCH, EPROC) set or DID
            // upper word straight from ChipTable.Rows (falling back to the disabled Sam3N/Sam7L rows'
            // literal ids for those two, since they have none active) and check it against what
            // Families hand-carries — the only way to trust 20-plus hex groups without eyeballing them.
            const int ArchShift = 20, EprocShift = 5;
            const uint ArchMask = 0xFF, EprocMask = 0x7;

            var disabledChipIdRows = new (SamBaChipFamily Family, uint ChipId)[]
            {
                (SamBaChipFamily.Sam3N, 0x29340960), (SamBaChipFamily.Sam3N, 0x29440960),
                (SamBaChipFamily.Sam3N, 0x29540960), (SamBaChipFamily.Sam3N, 0x29390760),
                (SamBaChipFamily.Sam3N, 0x29490760), (SamBaChipFamily.Sam3N, 0x29590760),
                (SamBaChipFamily.Sam3N, 0x29380560), (SamBaChipFamily.Sam3N, 0x29480560),
                (SamBaChipFamily.Sam3N, 0x29580560), (SamBaChipFamily.Sam3N, 0x29380360),
                (SamBaChipFamily.Sam3N, 0x29480360), (SamBaChipFamily.Sam3N, 0x29580360),
                (SamBaChipFamily.Sam3N, 0x29350260), (SamBaChipFamily.Sam3N, 0x29450260),
                (SamBaChipFamily.Sam7L, 0x27330740), (SamBaChipFamily.Sam7L, 0x27330540),
            };

            var chipIdRows = ChipTable.Rows
                .Where(r => r.KeyKind == ChipKeyKind.ChipId)
                .Select(r => (r.Family, ChipId: r.Key))
                .Concat(disabledChipIdRows);

            foreach (var group in chipIdRows.GroupBy(r => r.Family))
            {
                byte[] archValues = group
                    .Select(r => (byte)((r.ChipId >> ArchShift) & ArchMask))
                    .Distinct().OrderBy(a => a).ToArray();
                byte[] eprocValues = group
                    .Select(r => (byte)((r.ChipId >> EprocShift) & EprocMask))
                    .Distinct().ToArray();

                Assert.True(eprocValues.Length == 1, $"{group.Key}: rows disagree on EPROC.");

                FamilyDescriptor d = Families.Of(group.Key);
                Assert.Equal(eprocValues[0], d.ChipIdEproc);
                Assert.Equal(archValues, d.ChipIdArchValues.OrderBy(a => a).ToArray());
            }

            var deviceIdFamilies = ChipTable.Rows
                .Where(r => r.KeyKind == ChipKeyKind.DeviceId)
                .GroupBy(r => r.Family)
                .Select(g => (g.Key, UpperWord: (uint)((g.First().Key >> 16) & 0xFFFF)));

            foreach (var (family, upperWord) in deviceIdFamilies)
            {
                // SamR21 shares SamD21's DID upper word and deliberately carries no pattern of its
                // own — see FamilyDescriptor's remarks on that entry.
                if (family == SamBaChipFamily.SamR21)
                {
                    Assert.Null(Families.Of(family).DeviceIdUpperWord);
                    continue;
                }

                Assert.Equal(upperWord, Families.Of(family).DeviceIdUpperWord);
            }
        }

        [Fact]
        public void Rows_SharingAName_DifferOnlyByIdentificationWord()
        {
            // SupportedChips.Get() collapses same-name rows by keeping the first one it meets, so a
            // group that disagreed about anything else would publish one variant's geometry under
            // every variant's name — and publish it quietly, since programming uses the row the probe
            // matched rather than the collapsed one, leaving only the catalog wrong.
            foreach (IGrouping<string, ChipRecord> group in ChipTable.Rows.GroupBy(row => row.Name))
            {
                string[] shapes = group.Select(Shape).Distinct().ToArray();

                Assert.True(
                    shapes.Length == 1,
                    $"{group.Key}: rows sharing this name disagree beyond their identification word:" +
                    Environment.NewLine + string.Join(Environment.NewLine, shapes));
            }
        }

        /// <summary>Everything a row carries except the identification word it is matched on.</summary>
        private static string Shape(ChipRecord row)
        {
            return $"{row.Family} {row.ControllerKind} flash=0x{row.FlashAddress:X8} " +
                $"pages={row.PageCount}x{row.PageSize} planes={row.PlaneCount} " +
                $"locks={row.LockRegionCount} eefc=0x{row.FlashControllerBaseAddress:X8} " +
                $"gpnvm={row.BootGpnvmBitIndex?.ToString() ?? "none"} uniqueId={row.UniqueIdWords}";
        }

        /// <summary>
        /// CIDR NVPSIZ / NVPSIZ2 sizes in KB, indexed by field value. -1 marks the values the
        /// datasheet leaves reserved — holes in the encoding rather than sizes.
        /// </summary>
        private static readonly int[] NonVolatileSizeKb =
            { 0, 8, 16, 32, -1, 64, -1, 128, -1, 256, 512, -1, 1024, -1, 2048, -1 };

        [Fact]
        public void Rows_FlashSizeAgreesWithItsChipId()
        {
            // A CIDR encodes its own flash size, so hand-written geometry can be checked against the
            // silicon's account of itself — an independent source, the way the key mask is checked
            // against the keys. NVPSIZ sits at bits 11:8, except where NVPTYP says the part carries
            // ROM beside its flash, and NVPSIZ is then describing the ROM while NVPSIZ2 at 15:12 has
            // the flash.
            //
            // The two excluded rows, both named ATSAM4E8, are the reason this checks the table
            // instead of replacing it: they share CIDR 0x23CC0CE0 with the ATSAM4E16 and so report
            // the larger sibling's 1024 KB. One id, two flash sizes — which is what ExtendedKey
            // exists to resolve and what no decoder can get right. (A third exclusion, an emulated
            // bootloader's synthetic id with NVPTYP 7 describing no flash at all, went away with the
            // row itself.) That leaves 82 of the 84 CHIPID rows checked here.
            const uint RomAndFlash = 3;

            foreach (ChipRecord row in ChipTable.Rows)
            {
                if (row.KeyKind != ChipKeyKind.ChipId)
                    continue;       // a DSU DID carries no memory size at all
                if (row.Name == "ATSAM4E8")
                    continue;

                uint nvpTyp = (row.Key >> 28) & 0x7;
                int field = nvpTyp == RomAndFlash
                    ? (int)((row.Key >> 12) & 0xF)      // NVPSIZ2 — the flash beside the ROM
                    : (int)((row.Key >> 8) & 0xF);      // NVPSIZ  — the flash itself

                int keyKb = NonVolatileSizeKb[field];
                Assert.True(keyKb > 0, $"{row.Name}: key 0x{row.Key:X8} decodes to a reserved size.");
                Assert.True(
                    row.FlashSize == keyKb * 1024L,
                    $"{row.Name}: table says {row.FlashSize / 1024} KB, " +
                    $"key 0x{row.Key:X8} says {keyKb} KB.");
            }
        }

        [Fact]
        public void Rows_SharingAName_ShareGeometry()
        {
            // SupportedChips.Get() groups rows by name and reports the first of each group, so a name
            // reused for a part with different geometry would be listed with whichever row the table
            // happened to put first. Sharing a name is deliberate — package and revision variants
            // that differ only in the identification word — so this states the precondition of that
            // dedup where breaking it fails loudly instead of quietly mislisting a part.
            string Describe(ChipRecord r) =>
                $"{r.Name} {r.ControllerKind} {r.Family} flash@0x{r.FlashAddress:X8} " +
                $"{r.PageCount}x{r.PageSize} planes={r.PlaneCount} locks={r.LockRegionCount} " +
                $"eefc@0x{r.FlashControllerBaseAddress:X8} gpnvm={r.BootGpnvmBitIndex} uidWords={r.UniqueIdWords}";

            foreach (var group in ChipTable.Rows.GroupBy(row => row.Name))
            {
                string expected = Describe(group.First());
                foreach (ChipRecord row in group)
                    Assert.Equal(expected, Describe(row));
            }
        }

        [Theory]
        [InlineData(0x29340960u)]   // ATSAM3N4 rev A — boot program waits on UART0 alone
        [InlineData(0x29380360u)]   // ATSAM3N0 rev A
        [InlineData(0x27330740u)]   // AT91SAM7L128 — ROM SAM-BA offers the DBGU only
        [InlineData(0x27330540u)]   // AT91SAM7L64
        public void TryFind_DbguOnlyParts_AreNotMatched(uint chipId)
        {
            // Their rows are commented out of the table: with no UART transport the library cannot
            // talk to these parts at all, so identification must reject them up front rather than
            // succeed and fail later in the flash sequence. Re-enable the rows only together with
            // a DBGU transport.
            Assert.False(ChipTable.TryFind(chipId, 0, 0, out _));
        }

        [Fact]
        public void TryFind_Sam3x8_ArduinoDue()
        {
            // CHIPID for ATSAM3X8E is 0x285E0A60; table matches on chipId & 0x7FFFFFE0.
            Assert.True(ChipTable.TryFind(0x285E0A60, 0, 0, out ChipRecord record));

            Assert.Equal("ATSAM3X8", record.Name);
            Assert.Equal(SamBaChipFamily.Sam3X, record.Family);
            Assert.Equal(FlashControllerKind.Eefc, record.ControllerKind);
            Assert.Equal(0x80000u, record.FlashAddress);
            Assert.Equal(2048, record.PageCount);
            Assert.Equal(256, record.PageSize);
            Assert.Equal(2, record.PlaneCount);
            Assert.Equal(32, record.LockRegionCount);
            Assert.Equal(0x400E0A00u, record.FlashControllerBaseAddress);
        }

        [Fact]
        public void TryFind_Sam4E_DisambiguatesByExtendedId()
        {
            Assert.True(ChipTable.TryFind(0x23CC0CE0, 0x00120200, 0, out ChipRecord e16));
            Assert.True(ChipTable.TryFind(0x23CC0CE0, 0x00120208, 0, out ChipRecord e8));

            Assert.Equal("ATSAM4E16", e16.Name);
            Assert.Equal(2048, e16.PageCount);
            Assert.Equal("ATSAM4E8", e8.Name);
            Assert.Equal(1024, e8.PageCount);
        }

        [Fact]
        public void TryFind_SamD21_ByDsuDeviceId()
        {
            // ATSAMD21G18A (Arduino Zero): DSU DID 0x10010005; table matches on did & 0xFFFF00FF.
            Assert.True(ChipTable.TryFind(0, 0, 0x10010005, out ChipRecord record));

            Assert.Equal("ATSAMD21x18", record.Name);
            Assert.Equal(SamBaChipFamily.SamD21, record.Family);
            Assert.Equal(FlashControllerKind.D2xNvm, record.ControllerKind);
            Assert.Equal(4096, record.PageCount);
            Assert.Equal(64, record.PageSize);
        }

        [Fact]
        public void TryFind_UnknownIds_ReturnsFalse()
        {
            Assert.False(ChipTable.TryFind(0x12345678, 0, 0, out _));
            Assert.False(ChipTable.TryFind(0, 0, 0xDEAD00FF, out _));
            Assert.False(ChipTable.TryFind(0, 0, 0, out _));
        }

        [Fact]
        public void TryFind_Same54_ByDsuDeviceId()
        {
            Assert.True(ChipTable.TryFind(0, 0, 0x61840000, out ChipRecord record));

            Assert.Equal("ATSAME54x20", record.Name);
            Assert.Equal(FlashControllerKind.D5xNvm, record.ControllerKind);
            Assert.Equal(2048, record.PageCount);
            Assert.Equal(512, record.PageSize);
        }

        [Fact]
        public void TryFind_Sam7Se512_LegacyEfc()
        {
            Assert.True(ChipTable.TryFind(0x272A0A40, 0, 0, out ChipRecord record));

            Assert.Equal("AT91SAM7SE512", record.Name);
            Assert.Equal(FlashControllerKind.Efc, record.ControllerKind);
            Assert.Equal(0x100000u, record.FlashAddress);
            Assert.Equal(2, record.PlaneCount);
            Assert.Equal(2, record.BootGpnvmBitIndex);   // bootable SAM7 -> GPNVM2
        }

        [Fact]
        public void TryFind_Sam4SD16_UsesCorrectedChipId()
        {
            // The ATSAM4SD16 ids end in 0xCE0, not 0xC30 (SAM4S datasheet p.561).
            Assert.True(ChipTable.TryFind(0x29870CE0, 0, 0, out ChipRecord record));
            Assert.Equal("ATSAM4SD16", record.Name);
            Assert.Equal(2, record.PlaneCount);

            // The old (wrong) id must no longer resolve.
            Assert.False(ChipTable.TryFind(0x29870C30, 0, 0, out _));
        }

        [Fact]
        public void TryFind_SamC21_ByDsuDeviceId()
        {
            // SAMC21 shares the D2x controller with SAMD21.
            Assert.True(ChipTable.TryFind(0, 0, 0x1101000A, out ChipRecord record));

            Assert.Equal("ATSAMC21x18", record.Name);
            Assert.Equal(SamBaChipFamily.SamC21, record.Family);
            Assert.Equal(FlashControllerKind.D2xNvm, record.ControllerKind);
            Assert.Equal(4096, record.PageCount);
        }

        [Fact]
        public void TryFind_SamL21E15_FamilyCorrectedToSamL21()
        {
            // The L21 E15A/E15B must be tagged as the SamL21 family, not SamD21.
            Assert.True(ChipTable.TryFind(0, 0, 0x1081000D, out ChipRecord record));
            Assert.Equal("ATSAML21x15", record.Name);
            Assert.Equal(SamBaChipFamily.SamL21, record.Family);
        }

        [Fact]
        public void TryFind_SamD21_NewDVariant()
        {
            // ATSAMD21E17D, a later D-package variant.
            Assert.True(ChipTable.TryFind(0, 0, 0x10010094, out ChipRecord record));
            Assert.Equal("ATSAMD21x17", record.Name);
            Assert.Equal(SamBaChipFamily.SamD21, record.Family);
            Assert.Equal(2048, record.PageCount);
        }

        [Fact]
        public void TryFind_EmulatedBootloaderId_IsNoLongerListed()
        {
            // The one emulated-bootloader row the table carried claimed 32 lock regions on a single
            // plane, which is twice what MC_FSR can report, so identifying that device only ever
            // ended in an exception out of Open. The row is disabled; this pins that it stays that
            // way, and that the id now falls through to the same rejection as any other unplaceable
            // part — its ARCH byte (0x14) belongs to no family, so not even the fallback claims it.
            Assert.False(ChipTable.TryFind(0x714E3000, 0, 0, out _));
            Assert.False(ChipTable.TryFindChipIdFamilyFallback(0x714E3000, out _));
        }

        [Fact]
        public void BootGpnvmBitIndex_IsGpnvm1Normally_Gpnvm3OnSam9xe_Gpnvm2OnEfc()
        {
            // Per the SAM4S datasheet GPNVM0 = security, GPNVM1 = boot mode; the SAM9XE moves
            // boot to GPNVM3 because its lower GPNVM bits configure the brown-out detector;
            // legacy EFC parts boot from GPNVM2, and fixed-boot parts report 0.
            Assert.True(ChipTable.TryFind(0x285E0A60, 0, 0, out ChipRecord sam3x8));   // standard EEFC
            Assert.Equal(1, sam3x8.BootGpnvmBitIndex);

            Assert.True(ChipTable.TryFind(0x329AA3A0, 0, 0, out ChipRecord sam9xe));   // SAM9XE
            Assert.Equal(3, sam9xe.BootGpnvmBitIndex);

            Assert.True(ChipTable.TryFind(0x275C0A40, 0, 0, out ChipRecord sam7x512)); // bootable EFC
            Assert.Equal(2, sam7x512.BootGpnvmBitIndex);

            Assert.True(ChipTable.TryFind(0x270B0A40, 0, 0, out ChipRecord sam7s512)); // fixed-boot SAM7S
            Assert.Null(sam7s512.BootGpnvmBitIndex);
        }

        [Fact]
        public void UniqueIdWords_SetWhereverThePartPublishesAnId()
        {
            // 4 words on modern EEFC parts and on both NVMCTRL generations, 0 on SAM9XE and legacy
            // EFC. SAM7L was the other 0-word EEFC part; its rows are disabled, so SAM9XE now covers
            // that case alone.
            Assert.True(ChipTable.TryFind(0x285E0A60, 0, 0, out ChipRecord sam3x8));   // EEFC
            Assert.Equal(4, sam3x8.UniqueIdWords);

            Assert.True(ChipTable.TryFind(0x329AA3A0, 0, 0, out ChipRecord sam9xe));   // EEFC, no id
            Assert.Equal(0, sam9xe.UniqueIdWords);

            Assert.True(ChipTable.TryFind(0x272A0A40, 0, 0, out ChipRecord sam7se));   // legacy EFC
            Assert.Equal(0, sam7se.UniqueIdWords);

            // The NVMCTRL parts carry no unique-id command, yet publish the same 128 bits at fixed
            // read-only addresses. The count has to match the address list each controller holds, or
            // HasUniqueId would promise a different number of words than GetUniqueId reads.
            Assert.All(
                ChipTable.Rows.Where(r => r.ControllerKind == FlashControllerKind.D2xNvm
                    || r.ControllerKind == FlashControllerKind.D5xNvm),
                row => Assert.Equal(4, row.UniqueIdWords));
        }
    }
}

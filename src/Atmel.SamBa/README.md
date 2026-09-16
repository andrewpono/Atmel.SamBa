# Anp.Atmel.SamBa

.NET library for Atmel/Microchip **SAM-BA** device programming over USB CDC — device discovery, chip identification, memory read/write, flash erase/write/verify, GPNVM boot configuration, lock regions, and security bit.

Inspired by [BOSSA](https://github.com/shumatech/BOSSA) 1.9.1, but applet-free — unlike BOSSA, flash pages are programmed directly through the flash controller registers over the SAM-BA monitor protocol. Nothing is uploaded to or executed on the target, which makes the
process transparent and robust; throughput is recovered by optional batching each page's `W#` word
writes into a single USB transfer.

**Cross-platform.** On the Windows targets (`net48`, `net8.0-windows`) the transport is
[Anp.Serial.Win32](https://www.nuget.org/packages/Anp.Serial.Win32) (overlapped Win32 serial I/O,
CfgMgr32 discovery, CM_Register_Notification hot-plug). On the portable targets
(`netstandard2.0`, `net8.0`) the transport is System.IO.Ports and devices are constructed by port
path — discovery and the watcher are Windows-only. Asset selection follows the consuming
project's TFM: a plain `net8.0` app gets the portable surface even on Windows, so target a
`-windows` TFM to keep discovery. Targets `net48`, `netstandard2.0`, `net8.0`, `net8.0-windows`.

## Install

```
dotnet add package Anp.Atmel.SamBa
```

## Quick start

On the Windows targets, discovery finds the ports:

```csharp
using Anp.Atmel.SamBa;

// Enumerate SAM-BA USB CDC ports (VID 0x03EB, PID 0x6124 by default).
var devices = SamBaDeviceDiscovery.Enumerate();

using (var device = devices[0])
{
    device.ProgressChanged += (s, e) => Console.WriteLine(e);  // "128/2048 pages (6%) - Writing"
    device.Open();                                             // handshake + chip identification
    Console.WriteLine(device.ChipInfo);
    // ATSAM3X8 (Sam3X): 512 KB flash at 0x00080000, 2048 pages x 256 B, 2 plane(s), 32 lock regions

    byte[] firmware = File.ReadAllBytes("firmware.bin");
    device.UpdateFirmware(firmware);   // defaults: write + verify + boot-to-flash + reset
}
```

On the portable targets (`netstandard2.0`, `net8.0` — Linux, macOS, or Windows without
discovery), construct the device from the OS port name instead:

```csharp
using var device = new SamBaDevice("/dev/ttyACM0");   // "COMx" on Windows
device.Open();
```

On Linux, opening the port needs read/write permission on the tty (the `dialout` group on most
distributions). On macOS, prefer the `/dev/cu.*` path over `/dev/tty.*` — the latter blocks
waiting for a carrier the USB CDC port never raises.

### Update options

```csharp
using Anp.Atmel.SamBa.Configuration;

device.UpdateFirmware(firmware, new SamBaUpdateOptions
{
    BulkErase = false,            // full erase first (default false)
    Verify = true,                // read the region back and compare it against the image (default true)
    UnlockBeforeWrite = false,    // unlock any locked regions before erase/write (default false)
    Offset = 0x2000,              // write-block-aligned offset, e.g. preserve a SAMD21 bootloader, (default 0)
    SetBootToFlash = true,        // point the boot source at flash so the new firmware runs (default true)
    Lock = FlashLockScope.None,   // Written locks the regions the image covers; All locks everything
    SetSecurity = SecurityAction.Leave, // SetPermanently is WARNING: irreversible
    Reset = true,                 // reset the device when done (default true)
});
```

If a device drops data mid-programming (some bootloaders can't consume a batched command
stream back-to-back), set `device.SafeMode = true` to load the flash latch — and, on parts
that read word-by-word (see the EEFC read note under *Notes and limitations*), to read —
one word per exchange, slower but robust:

```csharp
device.SafeMode = true;
device.UpdateFirmware(firmware);
```

### Watching for devices (Windows targets)

```csharp
using var watcher = new SamBaDeviceWatcher();   // event-driven PnP notifications (Win8+)
watcher.DeviceArrived += (s, e) => Console.WriteLine($"Arrived: {e.Device.DisplayName}");
watcher.DeviceRemoved += (s, e) => Console.WriteLine($"Removed: {e.DevicePath}");
watcher.Start();
```

Events are raised on thread-pool threads. The VID/PID filter comes from
`SamBaDeviceDiscovery.ConfigureDefaults(...)`, read once at `Start()` and held for as long as the
watcher runs — so a later change does not reach a watcher that is already running, and until you stop
and start it the two disagree: `Enumerate()` matches the new filter while the watcher goes on matching
the one it started with. Expect that as a port `Enumerate()` lists that never raises an arrival, or
arrivals for ports it has stopped returning. Restart the watcher whenever you change the defaults.

Every subscriber receives an event even if an earlier one throws — the failure is reported
through `Anp.Serial.Win32.Diagnostics.SerialDiag.Error` instead of reaching the pool thread that
raised it. A notification already under way can still arrive after `Stop()` has returned, so a
handler that tears down state on stop has to tolerate one late event, including between the
`Stop()` / `Start()` pair a filter change needs. Stopping or disposing the watcher from inside a
handler is allowed.

### External discovery

`SamBaDevice` can be constructed directly from a serial device-interface path obtained from
any source (e.g. [PnpDeviceToolkit](https://github.com/andrewpono/PnpDeviceToolkit),
`RegisterDeviceNotification`, WMI):

```csharp
using var device = new SamBaDevice(devicePath);
device.Open();
```

## API overview

| Member | Description |
|--------|-------------|
| `SamBaDeviceDiscovery.Enumerate(options?)` | Unopened `SamBaDevice` per matching COM port (Windows targets). |
| `SamBaDeviceDiscovery.ConfigureDefaults(o => ...)` | Shared default VID/PID filter (Windows targets). |
| `SamBaDevice(string)` / `SamBaDevice(ISambaTransport)` | Construct from a port path, or from a custom transport implementing the public `ISambaTransport` seam (the device owns and disposes it). |
| `SupportedChips.Get()` | Distinct chip types the library recognizes (`SamBaChipInfo` per part). |
| `SamBaDevice.Open()` / `Close()` | Port open, `N#` binary mode, `V#` version, chip probe. |
| `ChipInfo`, `MonitorVersion`, `DisplayName` | Identification results (`DisplayName` reserves "SAM-BA device" for the Atmel VID/PID; other unopened ports show their PnP friendly name). |
| `PortName`, `FriendlyName`, `VendorId`, `ProductId` | PnP port metadata (available before `Open()`; populated on the Windows targets — elsewhere `PortName` echoes the path and the rest stay empty/null). |
| `UpdateFirmware(data, options?)` | Erase → write → verify → options → reset workflow (use this for full images). |
| `EraseAllFlash()` | Stand-alone full flash erase (destructive whole-device wipe). |
| `ReadMemory(address, count)` | Reads any address — RAM, registers, or flash (flash is memory-mapped). |
| `WriteMemory(address, data)` | Writes by address: flash range → per-block erase + program (read-modify-write for partial blocks); elsewhere → raw write. |
| `ReadWord` / `WriteWord` / `Go` | Raw 32-bit word access / jump at any address. |
| `GetBootSource()` / `SetBootSource(SamBaChipBootSource)` | Which memory the part boots — `Rom` or `Flash`, via the boot-mode GPNVM bit. Where the source is fixed it is fixed at `Flash`, the getter answers without a command, and only `Rom` is refused. `ChipInfo.CanSelectBootSource` says which parts can move. |
| `GetLockRegions()` / `SetLockRegions(bool)` / `SetLockRegions(IReadOnlyList<int>, bool)` | Region lock bits: read them all, set them all, or lock/unlock just the named regions leaving the rest untouched. |
| `GetSecurity()` / `SetSecurity()` | Security bit (**irreversible**). Readable on SAMD51/E5x via the DSU. |
| `GetUniqueId()` | Factory unique id: the EEFC's command pair on parts that implement it, the serial-number addresses on NVMCTRL parts. Empty on legacy EFC, SAM7L/SAM9XE and fallback-identified parts. Read once per `Open()` and cached — `ChipInfo.HasUniqueId` says whether a read is worth making. |
| `Reset()` | Per-family RSTC/AIRCR reset; closes the device. Throws `SamBaUnsupportedOperationException` on a family with no route (only `Unknown`, i.e. a part placed by fallback) — power-cycle it instead. |
| `SafeMode` | Load the flash latch — and read, on parts read word-by-word — one word per exchange instead of batching (robustness fallback). |
| `ProgressChanged` | Progress for connect/identification and long operations (erase/write/verify/read). |
| `GeometryMismatchDetected` | Raised by `Open()` when the part's own account of its flash geometry contradicts the device table's row for it. Fires either way — which geometry then takes effect is `Open()`'s `geometryPrecedence` parameter (see *Chips outside the table*). |

Every device failure derives from `SamBaException`, so one catch covers them all:
`SamBaTransportException` (serial/monitor I/O), `SamBaDeviceNotOpenException`,
`SamBaUnsupportedDeviceException` (the chip could not be placed at all — it carries every
identification word the probe read), `SamBaFlashCommandException`
(a controller command error, a lock error, or an operation the chip family cannot do — say which
via `IsCommandError` / `IsLockError` / `IsUnsupported`), `SamBaFlashTimeoutException`,
`SamBaUnsupportedOperationException` (the part cannot do it and no command was issued to find out —
today only a reset with no route for the family), `SamBaVerificationException` (with mismatch
offset/address). The four that name an operation expose it as `Operation`.

Caller mistakes stay as the BCL exceptions you would expect — `ArgumentNullException` and
`ArgumentException` for a missing or empty argument, `ArgumentOutOfRangeException` for a bad offset
or length, `ObjectDisposedException` after `Dispose`. One more is neither: `Open()` raises
`NotSupportedException` when a chip's geometry is one its flash controller cannot drive (more lock
regions per plane than the legacy `MC_FSR` reports, an NVMCTRL user page too small for its own lock
bits) — a claim about the part, not about the call, and not reachable from a healthy listed part.

Every public member documents what it throws, including the two that reach almost all of them:
`SamBaDeviceNotOpenException` before `Open()` and `ObjectDisposedException` after `Dispose()`.

## Supported chips

The full BOSSA 1.9.1 device table — SAM7S/SE/X/XC (legacy EFC), SAM3S/U/X/A, SAM4S/E,
SAM9XE, SAME70/S70/V70/V71 (EEFC), SAMC21/D21/R21/L21 (NVMCTRL, row erase), and
SAMD51/E51/E53/E54 (NVMCTRL, block erase) — plus device-table corrections and additions
backported from later BOSSA pull requests (SAMC21 support, extra SAMD21 D/L variants, and a
corrected ATSAM4SD16 chip id). Arduino-extended
bootloaders (Due, Zero, M0) are detected via the `[Arduino:XYZ]` version tag and use the
faster chip-erase (`X#`) path automatically.

All 185 rows have since been checked field by field against the vendor datasheets, and the DSU
rows' device-identification bytes against the family silicon errata — turning up seven
transcription defects and eight missing parts, listed under *Differences from BOSSA* below.
`docs/DESIGN.md` records the rules that pass established and the documents it used.

**SAM3N and SAM7L are excluded**: their ROM SAM-BA answers on a single serial channel — UART0 on the
SAM3N, the DBGU on the SAM7L — and neither family has a USB device port on any variant, so nothing
here can reach them. Their rows are commented out of
the device table, so they are rejected as unsupported at identification rather than failing later.

**The qNimble Quarto bootloader is excluded as well**, for a different reason: its row claimed 32 lock
regions on a single flash plane, twice what the legacy `MC_FSR` register can report, so opening such a
device only ever raised an exception — it was never usable. Which of the two figures is wrong cannot be
settled from documents, the part being a bootloader emulating hardware rather than silicon, so the row
is disabled rather than adjusted on a guess, and a Quarto now reports as unsupported.

## Chips outside the table

The table is not the limit of what can be programmed. At `Open()` the flash controller is also asked
to describe itself — the EEFC's `GETD` flash descriptor, the NVMCTRL's `PARAM` register — and the
answer is sanity-checked before anything is done with it:

- **It agrees with the row that matched** — nothing happens. The common case on a listed part.
- **No row matched.** The part is placed by family instead (the CIDR `ARCH` field, or the core behind
  a DSU), which settles the controller and its addresses, and the geometry it reported for itself is
  adopted — so an unlisted variant of a known family programs, erases and verifies normally.
  `ChipInfo.Family` reads `Unknown`, and the one thing such a part cannot do is reset itself:
  `Reset()` reports the missing route, and `UpdateFirmware` completes without resetting and says so
  in its progress log. A legacy EFC part has no descriptor command, so its size is decoded from its
  own CIDR `NVPSIZ` field and the remaining geometry taken from the listed rows of the same family —
  refused rather than guessed wherever those rows disagree.
- **It contradicts the row that matched** — `GeometryMismatchDetected` fires with both sets, and
  which one the rest of the run uses depends on `Open()`'s `geometryPrecedence` parameter: `Table`
  (the default) keeps the datasheet-sourced row, since the descriptor read is unverified against
  real silicon; `Device` adopts the part's own reading instead — only worth choosing when the table
  row itself is known to be wrong for the connected part and no corrected release is available yet.
  Either way the disagreement is reported — a caller stuck on `Table` still learns about it, and a
  caller on `Device` still learns it is trusting an unverified read, and the progress message names
  whichever side actually took effect. A handler that throws cannot fail the open.
- **`geometryPrecedence: Device` has no effect** on a part whose controller reports no geometry at
  all — a legacy EFC with no descriptor command, or a probe that failed outright — since there is
  nothing to adopt; the part runs on the table exactly as it would under `Table`, and the progress
  log says so rather than leaving the choice looking silently honored.
- **Neither a row nor a usable self-description** — `SamBaUnsupportedDeviceException`, carrying every
  identification word the probe read.

The probe costs a handful of extra USB round trips at `Open()` on EEFC parts, one on NVMCTRL parts,
and none on legacy EFC.

## Differences from BOSSA

The flash-controller logic is inspired by BOSSA 1.9.1, but a few points diverge
deliberately — as a design choice, to fix a defect (verified against the relevant datasheet
and Atmel's own AT91 SAM-BA library), or to backport a correction or feature merged/proposed
in BOSSA after the 1.9.1 release:

- **No on-target applet, no SRAM staging.** BOSSA uploads a word-copy applet into SRAM to
  fill the flash latch and to read flash. This library never executes code on the target:
  each page's `W#` word writes are batched into a single USB transfer to recover throughput,
  and reads on the parts whose ROM cannot serve flash over `R#` travel as batched `w#` word
  reads instead of through SRAM (the EEFC read note below). The Arduino
  `Y#` SRAM-to-flash write-buffer extension and the per-chip staging (`user`) SRAM address
  are consequently unused and not carried — the device table stores only flash geometry.
- **SAM7 2-plane erase (fix).** BOSSA's `EfcFlash::eraseAll` issues the second erase to
  `FCR0` with a page argument, which never reaches the upper plane's controller on a
  two-EFC part (SAM7x512). This port issues erase-all to `FCR0` **and** `FCR1`, so plane 1
  is actually erased — consistent with how page writes are already routed.
- **SAM7 status bits (fix).** The legacy MC flash status register reports lock errors on
  bit 2 and programming errors on bit 3. BOSSA reused the EEFC bit layout for the EFC and
  so never flagged a SAM7 programming error; this port checks the correct bits.
- **SAM7 flash timing.** `FMCN` (flash microsecond cycle number) is programmed to a safe
  value if the ROM left it unconfigured, rather than assumed already set.
- **USB read length trim (fix).** BOSSA peels one byte off a read whose length is a *power of
  two over 32 bytes*, attributing the corruption to the SAM firmware. The real culprit is the
  USB layer: a bulk IN transfer whose length is an exact multiple of the 64-byte full-speed
  max packet is not terminated by a short packet, so the read hangs. Powers of two ≥ 64 are
  merely the subset BOSSA happened to hit (it always reads a page at a time). This library trims
  any `R#` whose length is a multiple of 64 — so a large, non-page-aligned read (e.g. a 93568-
  byte verify) no longer stalls where BOSSA's narrower check would miss it.
- **Bounded NVMCTRL waits.** BOSSA polls the SAMD/E5x `NVMCTRL` ready flag forever; this
  port applies a timeout and raises `SamBaFlashTimeoutException` instead of hanging.
- **EEFC 16 KB auto-erase guard (fix).** The EEFC erase-and-write-page command (EWP) only
  erases within the first two 8 KB flash sectors; past 16 KB a page must be erased first. A
  write without a prior erase therefore fails part-way through on SAM4 / SAMx7x parts once it
  crosses 16 KB (BOSSA #130, #180). This port checks the range up front and throws a
  descriptive `SamBaFlashCommandException` pointing at `BulkErase = true`, rather than aborting
  mid-programming with a bare controller error. The default `UpdateFirmware` (per-page
  auto-erase, same as `WriteMemory`) hits the same limit unless `BulkErase` is set.
- **ATSAM4SD16 chip id (fix).** BOSSA 1.9.1's table matches the wrong CHIPID (`0x298x0C30`);
  the datasheet value is `0x298x0CE0`, so a real SAM4SD16 goes unrecognized. Corrected here
  (matches BOSSA PR #170).
- **SAML21 E15 family (fix).** BOSSA 1.9.1 tags the SAML21 E15A/E15B rows as the SAMD21
  family; corrected to SAML21 (matches BOSSA PR #152).
- **Device-table transcription (fixes).** Checking every row against its datasheet turned up seven
  more defects carried over from 1.9.1. Three make a part unreachable: the AT91SAM7SE32 CHIPID
  (`0x272A0340`, where the datasheet says `0x27280340` in three separate places) and all three
  ATSAM3S1 CHIPIDs (their SRAMSIZ nibble holds the SAM3S2's value, so no real SAM3S1 ever matched).
  Three are wrong lock-region counts, which is not cosmetic — the count sets the region-to-page
  arithmetic every lock and unlock uses: ATSAM4SD16 and ATSAM4SA16 both carried 256, which is only
  the ATSAM4SD32's (both are 128), and ATSAM4S4 carried 16, which is only the ATSAM4S2's (it is 32).
  The seventh is four part names that lost a digit or a prefix: `AT91SAMX512` → `AT91SAM7X512`,
  `AT91SAMXC512` → `AT91SAM7XC512`, `ATSAM7L128` → `AT91SAM7L128`, `ATSAM9XE512` → `AT91SAM9XE512`.
  (The disabled ATSAM3N0 row's count was wrong the same way — 1, the ATSAM3N00's — and is fixed too.)
- **Device-table additions.** Eight rows for parts the vendor documents list and BOSSA's table does
  not: ATSAM3S8A and ATSAM3SD8A (SAM3S CHIPID table), ATSAME51G18A and ATSAME51G19A (SAM D5x/E5x
  errata — absent from the 2019 revision, added in the 2023 one), ATSAMC21J17AU and ATSAMC21J18AU
  (SAM C20/C21 errata), and the disabled ATSAM3N00 pair (SAM3N errata). Each row's comment carries
  its source.
- **Post-1.9.1 backports.** Beyond a pure 1.9.1 port, this library also folds in SAMC21
  support and its slow-erase status-poll retry (#123/#152), extra SAMD21 D/L package
  variants (#124), the EEFC unique-id read exposed as
  `GetUniqueId()` (#132), and readable SAMD51/E5x security via the DSU (#127). See the
  changelog for the full list.

## Notes and limitations

- **USB CDC only** — no UART/DBGU (XMODEM) support. The standard SAM-BA USB port is
  VID `0x03EB` / PID `0x6124`.
- **EEFC reads are word-by-word**: the ROM monitors on EEFC parts (SAM3/SAM4/SAM9XE/SAMx7x)
  answer `R#` block reads of flash — and of the boot memory at address 0, which remaps the
  same flash — with all zeros. Confirmed on hardware, and not limited to SAM3 despite
  BOSSA's comment saying so (its applet-based SRAM staging quietly covers the whole family).
  This library reads those parts one 32-bit word at a time instead, pipelined in batches of
  128 `w#` commands per USB transfer, so reads and verify work — just slower (~4x the wire
  bytes of a block read). `SafeMode` drops the pipelining too, one word per round trip.
- **Boot source.** Every supported part boots flash unless a boot-mode GPNVM bit says otherwise, so
  `SamBaChipBootSource.Flash` is the fixed answer wherever there is no bit and `Rom` is the only request
  that can ever be refused. Parts that can move: bootable legacy EFC (SAM7X/SE/XC — GPNVM2) and all
  EEFC (SAM3/4/9XE/SAMx7x — GPNVM1, or GPNVM3 on the SAM9XE, whose lower GPNVM bits configure the
  brown-out detector). Parts that cannot: the small SAM7S variants, which always boot flash because an
  erase copies SAM-BA into flash and it relocates itself to RAM to run, and every NVMCTRL part
  (SAMC21/D21/R21/L21, SAMD51/E5x), which has no boot ROM at all. `ChipInfo.CanSelectBootSource` says
  which you have. `SetBootSource` accepts the source already in force on any part and throws only for
  one it cannot reach.
- **The bit is sticky, so a firmware update has to set it.** On a part that can select, the boot source
  survives an erase — so an update that leaves it pointing at the ROM comes back up in the SAM-BA
  monitor rather than running what was just written. That is what `SamBaUpdateOptions.SetBootToFlash`
  (default true) is for, and why the options are applied after the erase and the write. It is a step to
  perform or skip, not a selection: `false` leaves the boot configuration alone rather than pointing the
  part at the ROM, and it is silently skipped where there is no bit to set.
- **`WriteMemory` to flash vs `UpdateFirmware`.** `WriteMemory(address, data)` always auto-erases
  page by page as it writes, so on SAM4 / SAMx7x it can only reach the first 16 KB (the EEFC
  auto-erase limit). `UpdateFirmware` has the same per-page limit unless
  `SamBaUpdateOptions.BulkErase` is set (default false) to erase the whole image up front instead.
- **A connect can fail on a port left dirty by a previous session**, with
  `SamBaTransportException` reporting that the `V#` reply is not a version string. Killing a process
  part-way through a flash read or verify leaves the device with the rest of that transfer still to
  send, and it resumes as soon as something opens the port again. `Open()` already retries once
  (closing and reopening the port, which is what stops the leftovers arriving); if it still fails,
  just call it again, and power-cycle the board if a second attempt does not take. The alternative —
  accepting the reply — is worse, because it silently mis-reads the monitor's capabilities and then
  the chip id.
- File format is raw binary; parse hex/ELF yourself before calling `UpdateFirmware`.
- `SamBaDevice` is single-consumer; don't overlap operations from multiple threads.

## License

MIT. Contains logic derived from BOSSA 1.9.1 (BSD-3-Clause, © 2011-2018 ShumaTech) —
see THIRD-PARTY-NOTICES.md in the repository.

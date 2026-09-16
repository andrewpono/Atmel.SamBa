# Design notes

Conventions and constraints for working *on* this library. The [root README](../README.md) is the
project tour and [src/Atmel.SamBa/README.md](../src/Atmel.SamBa/README.md) is the consumer-facing
package documentation; neither covers the rules below.

## Layering

```
SerialPortTransport /                    Windows TFMs: overlapped Win32 serial I/O
PortableSerialTransport (ISambaTransport)  portable TFMs: System.IO.Ports
        v
SambaMonitor                             SAM-BA wire protocol: N V w W o O R S G X
        v
FlashController                          Efc / Eefc / D2xNvm / D5xNvm register programming
        v
FlashProgrammer                          write-block loop, progress, verify
        v
SamBaDevice                              the public API
```

Each layer talks only to the one below it. The flash controllers reach into `SambaMonitor` for word
and block access, but never into the transport; `SamBaDevice` owns the monitor and hands it to the
controller that `FlashController.Create` builds. `FlashProgrammer` is handed only its controller —
deliberately no monitor, so that **every** flash read and write goes through the controller. All of
the reads funnel into `FlashController.ReadRange` (`ReadBlock` included), which is what let the EEFC
zero-read fix (see *Known slow paths*) fork one route instead of hunting every reader; a `Verify`
that read through the monitor itself would quietly have missed it.

**The public surface is deliberately small:** `SamBaDevice`, `SupportedChips`, `SamBaChipInfo`,
`SamBaChipFamily`, `SamBaChipBootSource`, `SamBaUpdateOptions`, the event args, the exception
hierarchy, and `ISambaTransport` — plus, on the Windows TFMs only, `SamBaDeviceDiscovery`,
`SamBaDeviceWatcher`, `SamBaDiscoveryOptions` and the arrival/removal event args. Everything
else — the monitor, both concrete transports, the chip table, all four flash controllers — is
`internal`. Adding a type to the public surface is an API decision, not a convenience.

`ISambaTransport` is public by decision, not convenience: it is the seam that lets a consumer run
the library over a link the library does not ship (a different serial stack, a TCP bridge), handed
in through the public `SamBaDevice(ISambaTransport)` constructor. The concrete transports stay
internal — the interface is the contract, the implementations are plumbing.

## Platform split

The package ships four TFMs in two pairs:

| | `net48` | `netstandard2.0` | `net8.0` | `net8.0-windows7.0` |
|---|---|---|---|---|
| Transport behind `SamBaDevice(string)` | Anp.Serial.Win32 | System.IO.Ports | System.IO.Ports | Anp.Serial.Win32 |
| Discovery, watcher, `SamBaDiscoveryOptions`, arrival/removal event args | yes | no | no | yes |
| PnP port metadata (`PortName`, `FriendlyName`, VID/PID) | yes | no | no | yes |
| Public `ISambaTransport` + `SamBaDevice(ISambaTransport)` | yes | yes | yes | yes |

Mechanics: whole files are switched per TFM with `Compile Remove` in the csproj (the two concrete
transports, discovery, the watcher, and the three types only they touch), so no file carries `#if`
around an entire type; the `SAMBA_WIN32` symbol exists only for the small forks inside
`SamBaDevice.cs` — which default transport the string constructor builds, and whether the PnP
metadata probe runs.

Asset selection is by the consumer's TFM, not the machine it runs on: a `net8.0-windows` app gets
the full surface, a plain `net8.0` app gets the portable one **even when it runs on Windows** (to
keep discovery, target a `-windows` TFM), and a `net471`/`net472` app resolves `netstandard2.0` —
also no discovery. The `netstandard2.0` asset has one more wrinkle: System.IO.Ports is a
compile-time facade over per-RID implementations, so it works in RID-resolving applications
(any SDK-style app) and throws `PlatformNotSupportedException` where nothing performed RID
resolution.

One behavioral delta between the transports, documented rather than papered over: the Win32 layer
turns "port closed while a read is pending" into an immediate abort, while System.IO.Ports does
not support cross-thread close under a blocked read — there the close waits out the read's own
timeout. Every read the monitor issues is bounded (5 s at most), so the difference is a short
stall, never a hang.

## Accessibility within internal types

Most internal types declare `public` members. This is intentional, and it means something different
from what it looks like:

- **Nothing leaks.** A member's accessibility is capped by its containing type, so `public` on a
  member of an `internal` class *is* internal to the assembly (plus `Atmel.SamBa.Tests`, via
  `InternalsVisibleTo`). The compiled metadata is identical either way.
- **Sometimes it is mandatory.** An implicit interface implementation must be public whatever the
  interface's own accessibility — that covers every member of `SerialPortTransport` and
  `PortableSerialTransport` (`ISambaTransport`) and `SambaMonitor.Dispose` (`IDisposable`).
- **`public` vs `protected` marks the audience.** In `FlashController`, `public` is what the rest of
  the assembly consumes (`EraseAll`, `WriteBlock`, `ReadBlock`, `GetLockRegions`, `SetLockRegions`)
  and `protected` is the extension seam for the four controllers (`Monitor`, `Chip`, `LoadLatch`,
  `ValidateBlock`, `OnInitialize`, `OnSetAutoErase`). Writing the first group as `internal` would flatten the
  distinction, because `protected internal` means protected **or** internal — wider than both. No
  modifier expresses "in-assembly consumers but not subclasses", so `public`/`protected` is the
  closest encoding available.
- **`internal` marks a deliberate hole.** Where a member exists for one specific collaborator or for
  the tests rather than as part of the type's own API, it is `internal` even on a type whose other
  members are `public`: `SambaMonitor.WriteWordCommandLength` and `ReadWordCommandLength` (the tests
  size expected batches from them), `ParseArduinoExtensions`, `ProgressReporter`, and the
  constructor. `WriteWord` beside them is `public` because it is protocol surface; the command
  encoders behind it are `private`, since both word-batched transfer directions (`WriteViaWords`,
  `ReadViaWords`) live inside the monitor rather than being assembled by the flash layer.

One consequence: `CS1591` (missing XML comment) only fires for members visible outside the assembly,
so none of these are compiler-checked. Documenting them is a house rule, not an enforced one.

## The write block, not the page

The flash layer programs in **write blocks**: the smallest span that can be written without
disturbing what surrounds it. `FlashController.WriteBlock` / `ReadBlock` and `FlashProgrammer`'s loop
all work in these, and a write offset must be aligned to one.

| Family | Hardware page | Write block |
|--------|---------------|-------------|
| EFC, EEFC | 256 B (typical) | the page — erase-and-write-page erases exactly what it writes |
| NVMCTRL D2x (SAMD21/C21/L21/R21) | 64 B | a 256 B row — 4 pages |
| NVMCTRL D5x (SAMD51/E5x) | 512 B | an 8 KB block — 16 pages |

The hardware page still exists and still matters: the NVM page buffer holds exactly one, so
`NvmFamilyController.WriteBlock` erases once and then commits its pages one at a time. What the block
gives is a unit whose replacement is safe, which is what makes `FlashProgrammer`'s read-modify-write
correct on all four families with no family special-casing.

Getting this wrong is not a style matter. Merging at page granularity on NVMCTRL writes one page while
erasing — and therefore blanking — the other three or fifteen in its block. `ChipRecord` keeps the
datasheet-true page geometry and derives the block from the controller kind, so
`SamBaChipInfo` can report both and the chip table still matches the numbers it was transcribed from.

## Device-table provenance

`ChipTable`'s 185 rows are **transcribed from vendor documents, never derived**. Every key and every
geometry field has been checked row by row against the part's own datasheet; the 101 DSU rows' DEVSEL
bytes against the family silicon errata, which is where Microchip moved that table. No row is currently
unverified against a vendor document.

Rules the table follows, all of them earned by something that went wrong:

- **Transcribe, don't infer.** DEVSEL numbering is assigned per series, not family-wide: SAME51 runs
  `0x00`–`0x04` in release order where SAMD51, SAME53 and SAME54 use `0x02`–`0x06` for the same
  packages, and SAME51's late G variants sit at `0x06`/`0x05` — G18 *below* G19, the reverse of
  SAMD51's. Any pattern you think you see across series is a coincidence.
- **Prefer the self-consistent source when a datasheet disagrees with itself**, and record the
  reasoning in the row's comment. It happens more than you would expect, and it is not always fixed in
  later revisions: SAM3S8/SD8
  §18.5 puts the EEFC at `0x400E0800` where all four of its own register addresses say `0x400E0A00`.
  SAM7XC §8.1.2 gives the XC512 "1254 pages of 256 bytes" beside the byte count that makes it 2048.
  Where the vendor does resolve it, cite the resolution: the SAM9XE's first revision printed two
  mutually exclusive CIDRs for its 512 KB part and revision 6254B corrected them in its change log.
- **A CIDR field decode is a check, not a source.** Page size, lock count and plane count are not
  encoded at all, one CIDR covers both the ATSAM4E16 and the ATSAM4E8, and NVPSIZ describes the ROM
  when NVPTYP says 3. `Rows_FlashSizeAgreesWithItsChipId` uses the decode as an independent second
  opinion on 82 of the 84 CHIPID rows.
- **`PlaneCount` counts controllers, not banks** — see `ChipRecord.PlaneCount`. The ATSAM3SD8 is
  dual-bank behind a single EEFC and is therefore 1, while its GETD descriptor answers `FL_NB_PLANE`
  2. Reading "dual plane" off a datasheet cover and writing 2 would address a register block that is
  not an EEFC.
- **Keys for parts that never shipped stay in.** A key no part reports can never match, `SupportedChips`
  groups by name so nothing phantom is listed, and the ids are documented — so the 48-pin SAM4S and
  SAM3S8/SD8 rows are kept with a comment rather than deleted.
- **Rows for parts this library cannot reach are commented out, not deleted** (SAM3N, SAM7L: DBGU/UART
  only), so the geometry survives for a possible future transport and monitor. Same treatment, different reason, for the one
  row that described a bootloader emulating hardware rather than a part: it claimed more lock regions
  per plane than `MC_FSR` can report, so `Efc`'s constructor refused it and the row was never openable.
  A guess at the right figure would have been geometry invented for a device nobody here can test —
  and on the only row whose id and sizes no vendor document covers.

What the table cannot settle on its own, the part does at open: `FlashController.ResolveGeometry`
reads the EEFC descriptor or the NVMCTRL `PARAM` register, adopts it when no row matched, and raises
`SamBaDevice.GeometryMismatchDetected` when it disagrees with a row that did. A transcription slip in a
lock count or page count is therefore loud on real hardware rather than silent — which is the safety
net behind every "unverified" note in the table. Which side wins a disagreement on a matched row is
`SamBaGeometryPrecedence`, taken by `Open()`/`FlashController.Create`: `Table` (the default) trusts this
table over the unverified read; `Device` is the escape hatch for a caller stuck on a table row already
known to be wrong, with no corrected release to move to yet — the mismatch is still reported either way,
naming whichever side actually took effect. `Device` has no effect at all — same as `Table` — on a part
whose controller has nothing to report, such as a legacy EFC predating `GETD`; that is reported too
(`FlashController.DevicePrecedenceHadNoEffect`), rather than left looking like a silently honored choice.

Documents used for the last full pass, worth re-fetching before the next one: SAM7S 6175M, SAM7SE 6222H,
SAM7X 6120K, SAM7XC 6290I, SAM7L 6257B, SAM9XE 6254E, SAM3S 6500F, SAM3S8/SD8 11090B, SAM3N 11011C,
SAM3U 6430G, SAM3X/A 11057C, SAM4S 11100K, SAM4E 11157H, SAMx7x DS60001527J, SAMD21 DS40001882L,
SAMC21 DS60001479M, SAML21 DS60001477C, SAMR21 42223G, SAMD5x/E5x DS60001507N. Silicon errata, which
carry the DEVSEL tables and are worth re-reading for new entries as much as new anomalies: SAM D21/DA1
DS80000760G, SAM C20/C21 DS80000740K, SAM D5x/E5x DS80000748T. Each of the three turned up rows the
datasheets alone could not supply — SAME51G18A/G19A from the D5x/E5x errata, ATSAMC21J17AU/J18AU from
the C20/C21 one — and the SAM3N and SAM3S errata chapters supplied ids their CHIPID tables omitted.

One thing the table still cannot settle from paper: the SAM3U4 flash base rests on read-side aliasing
that only hardware can confirm — see the note on that row.

## No device I/O in constructors

Controller constructors do address arithmetic and validation only. Register initialization belongs in
`OnInitialize()`, which `FlashController.Create` invokes after construction — so a half-built
controller never issues a command, and no virtual call reaches hardware during construction.

`Create` is therefore a three-step sequence — construct, `ResolveGeometry`, `OnInitialize` — and the
middle step is why `FlashController.Chip` is settable rather than `readonly`: the geometry probe is
itself register access, so only a built controller can perform it, and a provisional record is
completed after construction rather than before it. The alternative that would keep the field readonly
is to probe through a throwaway controller and build a second from the finished record; the trade is
recorded on `Chip` itself, along with what would make it worth taking.

## Naming register constants

Named after the numeric role they play, so a use site never has to guess it:

| Suffix | Meaning |
|--------|---------|
| `*Mask` | a value ANDed or ORed with a register (one bit or many) |
| `*Shift` | a shift count positioning a multi-bit field |
| `*BitIndex`, `First*Bit` | a bit position |
| no suffix | a field's contents rather than its placement — `FlashWaitStates`, `DefaultFlashCycles` |

Command opcodes and register offsets live in nested `private static class Cmd` / `Reg` groups per
controller; the monitor's wire alphabet lives in `SambaMonitor.Syntax`. A command letter or opcode
should appear as a literal in exactly one place.

## One command, one bracket

Every keyed flash command goes through its family's `Execute` — wait for idle, write the command,
wait it out, check the error. That makes each command **self-sufficient**: it does not trust
earlier paths to have finished theirs (the leading wait re-establishes idle locally), and its
failure is thrown where it was issued, attributed to the operation that caused it. The trailing
wait matters doubly on the EFC family, where reading the status register clears its error bits —
an unwaited command's error can be read away by the next status query and never reported.

The redundant poll this costs when nothing is in flight exits on its first read. Consumers of the
bracket — `ApplyOptions`, the write loops — may then run steps back to back without waits of
their own.

Deliberate exceptions, each commented at the site:

- the EEFC unique-id sequence: STUI drops FRDY and must not be waited on until SPUI, so its
  bracket spans the whole pair;
- the EFC getters: MC_FSR exposes lock/security/GPNVM state as live status bits — no command is
  issued at all;
- mode-register (FMR/CTRLA/CTRLB) writes: configuration, not commands — nothing to wait out.

## Connecting to a pipe that may not be empty

The handshake assumes nothing about what the port already holds, and reads exactly once: the version
reply. Everything else it might receive is thrown away without being read, by
`SambaMonitor.ClearStalePipe` — purge, wait ~10 ms for bytes that were on the wire when the purge ran,
purge again. Two purges because `Purge` only discards what has already reached the driver, so a byte
one purge earlier than it needs to be would otherwise sit where the next reply is looked for.

It runs twice. Before the handshake, for whatever a half-finished previous session left behind; and
after `N#`, for the mode-switch echo — which also gives the wire the separation `V#` needs, since the
monitor misparses two commands sharing one USB packet. Calling it straight after a command write is
safe even though it purges the transmit direction too: the transport's write is awaited to completion,
and on USB CDC that means the bulk transfer was acknowledged, so nothing of ours is still queued.

**Neither clear reads.** Reading until a read came back empty is what the first version used to do, and it
cost every connect a 100 ms timeout plus the cancellation and timeout exceptions the serial layer
raises to implement one — on every port, to no effect on the quiet ones, which is nearly all of them.
The `N#` echo was worse: a monitor already in binary mode sends none, and binary mode survives closing
the port, so a read for that reply timed out on every connect after the first. Nor did reading settle
the case the first clear was written for: a session killed mid-`R#` leaves the device with as much as a
whole 256 KB chunk still to send, the transfer resumes as soon as the next session posts reads, and it
outlasts any budget short enough to sit in front of a connect.

So a dirty pipe is caught where it shows instead. `ReadVersion` requires the `V#` reply to contain
something that looks like a version — a printable run of at least four characters with a digit among
them — and throws `SamBaTransportException` otherwise. Without that check leftover data **passes**: the
accumulate loop stops at whatever non-printable byte closes its first run, the length is non-zero so the
existing "no reply to V#" throw does not fire, and the connect succeeds carrying that run, or nothing at
all, as the version, with baseline `Capabilities` behind it. That is the worst of the three outcomes —
it drops the read cap an Arduino bootloader needs, so every later block read is corrupt, and the
remaining leftover bytes misalign each reply after them, so the chip probe reads a garbage id and may
land on a plausible one.

A **run**, not a prefix, and that is what makes the unread `N#` echo safe. Firmware latency is not
something this library can bound, so an echo slower than the settle window lands in front of the version
reply; scored as a prefix it would be a zero-length version, and the connect would fail permanently,
because the retry meets the same latency. Stepping over leading non-printables costs nothing and removes
the assumption. Only non-printables, though — a monitor that echoed something printable would still put
it at the head of the version string, and the purge is what covers that. The two mechanisms are
complementary: the purge handles an echo of unknown content, the run handles one of unknown timing.

Failing beats draining because failing is recoverable: `HandshakeWithRetry` answers it with close →
reopen → retry, and the close is the part that matters, being what stops the driver pulling the
remainder. Past that it reaches the caller, for whom repeating a connect is cheap.

The check is a heuristic and knowingly not airtight — leftover data carrying a long printable run with a
digit in it passes. The airtight test, that the pipe is quiet after the reply, costs exactly the
timed-out read this stopped paying, so it is not taken.

## Known slow paths

Sequences where a faster one is known and deliberately not taken. Recorded here rather than as
commented-out code in the source, where no compiler checks it and a rename leaves it quietly wrong.

**`Eefc.GetLockRegions` issues one GLB per lock region**, waiting for FRDY each time — roughly 500 USB
round trips on a 256-region ATSAM4SD32, before `ApplyLockRegions` has changed anything. The cheaper
form is one GLB per plane, then a walk over that plane's successive `FRR` words:

```
for each plane:
    Execute(registers, Cmd.GetLockBit, 0, ...)          // once, not once per region
    for bit in 0 .. LockRegionsPerPlane - 1:
        if bit % 32 == 0: frr = Monitor.ReadWord(Frr(registers))
        regions[plane * LockRegionsPerPlane + bit] = (frr & (1u << (bit % 32))) != 0
```

About a dozen round trips instead of 500. It rests on one GLB latching every lock word — which the
per-region loop's own `FRR` paging already implies, but which no datasheet read for this library
states and no part on hand has confirmed. The per-region sequence is the one known to work, so verify
on a two-plane part before switching.

The per-region subset path (`SetLockRegions(IReadOnlyList<int>, bool)`) reuses that same one-GLB
primitive (`Eefc.ReadLockRegion`) for only the regions it touches, so a two-region request costs two
GLBs regardless of the part's region count — the batch above stays worthwhile only for the read-all
and lock-all forms.

A firmware update asked to lock with `FlashLockScope.Written` takes that subset path too:
`FlashController.LockRegionsCovering` turns the image's offset and length into the regions it
occupies, and `ApplyOptions` routes them through `SetLockRegions` rather than straight to the family
hook, so the one validation of region indexes covers computed subsets as well as user-supplied ones.
`FlashLockScope.All` skips straight to the family hook instead, the same as
`SamBaDevice.SetLockRegions(bool)`. Locking every region on a `Written` request would write-protect
flash the update never wrote — somebody else's bootloader, on an offset image. `UnlockBeforeWrite` is
deliberately the broad, all-regions form regardless: a full erase blanks the whole flash and cannot
leave a locked region standing anywhere.

**`SafeMode` costs a system timer tick per word**, not a modest multiple of the batched path — see the
remarks on `SambaMonitor.FlushDelay` for what that comes to on a large part.

**Every read on an EEFC part travels as `w#` word reads** (`SambaMonitor.ReadViaWords`), because those
ROM monitors answer an `R#` block read of flash — and of the boot memory at address 0, which remaps
the same flash — with all zeros (SamBa monitor bug). Confirmed on hardware, and family-wide rather than SAM3-only as
BOSSA's source comments claim; BOSSA never notices because its applet stages every EEFC read through
SRAM, an option an applet-free library does not have. The words are pipelined 128 commands per USB
transfer to recover most of the round-trip cost, but the wire still carries 16 bytes per 4 bytes read
(a 12-byte command plus a 4-byte reply) — about four times an `R#` stream, and there is no cheaper
form short of executing code on the target. The fork is `FlashController.ReadRequiresWords`, false
everywhere but `Eefc`; `SamBaDevice.ReadMemory` forks every address on it, not just the flash window,
because of the address-0 remap. Under `SafeMode` each word additionally waits out its own reply.

## Enum options over bools

`SamBaUpdateOptions.SetSecurity` is a `SecurityAction` (`Leave`/`SetPermanently`) rather than a
`bool` for the same reason `Lock` is a `FlashLockScope` rather than a `bool` — asymmetric options read
badly as bools (`false` is not "unset"; here it is not even expressible, since the bit cannot be
cleared in software) — but security carries the sharper cost: a `bool SetSecurity` defaults to
`false` on every path a value can arrive by (a fresh options object, `Clone()`, a settings object
deserialized with a missing field), and the one path that matters, a caller who means to set it, looks
identical at the call site to every path that does not. `SecurityAction.SetPermanently` has to be
spelled out; nothing produces it by omission.

## Timeout ownership

- `SambaMonitor.NormalTimeout` — every command write, and most replies. Read by
  `EfcFamilyController.WaitFsr` for its own register polling.
- `SambaMonitor.LongTimeout` — read by `NvmFamilyController.WaitReady`.
- `SambaMonitor.ChipEraseTimeout` — per instance, settable, read at each wait; seeded from
  `DefaultChipEraseTimeout`. Both the `X#` route and the register erase-all route use it.
- `QuickTimeout` — private to the monitor, and down to one read: the one that closes off a version reply
  from a monitor that sends no terminator, which is the only read in the library allowed to come back
  empty. Nothing clears the pipe by reading, and no reply that may not arrive is read for.
- `OpenPortBudget` — not a wait, a decision made in `Connect` before the handshake is attempted at
  all: how long its own `Open()` call may take before the port is deemed too slow to bother
  handshaking, let alone paying for `HandshakeWithRetry`'s one retry — itself another `Open()` just
  as likely to stall. `HandshakeWithRetry` carries no budget of its own and always runs its one retry
  once called; this is what decides whether it is called at all. A multiple of `NormalTimeout` rather
  than its own literal, so the margin stays proportional if that budget changes.

Controller wait loops are wall-clock deadlines (`Stopwatch`), never iteration counts — each iteration
costs USB round-trips, so a count would misreport the elapsed time in
`SamBaFlashTimeoutException.Timeout`.

**None of the above reaches the transport's own control-plane calls** — opening the handle, asserting
DTR, purging, closing. Every one of those is a single blocking Win32 call with no overlapped
structure and no `TimeSpan` of its own; on the Windows transport, `SerialDevice.DtrEnable`
(`EscapeCommFunction`) and the handle teardown inside `Close()`/`Dispose()` (`CloseHandle`) cannot be
given a deadline or interrupted by `CancelIoEx` the way a pending read can. If `usbser.sys` will not
service one — observed after `ChipIdentifier` reads an address a part does not support and hangs its
CPU (see the probe-order remark on `ChipIdentifier.Identify`) — the call blocks for however long the
driver takes to notice, independent of `NormalTimeout` or any other figure this library owns; tens of
seconds has been observed on real hardware, on the *next* connect's `DtrEnable`, well past every
timeout above. `ISambaTransport.StatusReporter` (see *Progress reporting*) exists because of this gap:
there is no bounded call to time out, so the only mitigation available is naming which call is stuck.

A second, unrelated way to reach the same symptom, also observed on real hardware: an AT91SAM7SE512
Rev A running SAM-BA monitor v1.4 (or Rev B running SAM-BA monitor v2.0), — flash firmware, 
set boot to flash (see *Boot source*), close the session, then try to reach the monitor again.

A third way is deliberate rather than observed: `ChipIdentifier.Identify`'s `mode` parameter
(`SamBaDevice.Open`'s `identificationMode`) exists because address 0 is not guaranteed to hold a
branch opcode on a genuine AT91SAM7/9 part — blank flash, or firmware that left something else there,
reads back looking like a Cortex-M part despite the core being legacy, sending `Auto` down the wrong
branch to read a CPUID register that does not exist. Forcing `ChipId` or `CpuId` on a part of the
*other* core generation reproduces the same hang on purpose, by reading a register the connected part
does not have. `Auto` (the default) keeps the probe order load-bearing as described above; the forced
values are for a caller that already knows the
part's core generation, never for guessing at one that hangs under `Auto`.

## Progress reporting

- Stage names live in `Events/ProgressStage.cs`, not as literals at the raise sites: a stage spans
  layers — "Identifying" is reported from `ChipIdentifier`, from `SamBaDevice.Open` and from the
  geometry check — and a consumer grouping by stage has to see one stage rather than three spellings.
  The strings are effectively public, so changing a value is a changelog entry.
- `SambaMonitor.Report` is the single sink below the device layer; `ChipIdentifier` reports through
  the monitor it was handed rather than building args of its own.
- Raises are synchronous, inline on the calling thread, and a handler that throws fails the operation
  it was reporting on. That is intended — it is the caller's own bug surfacing — with exactly one
  exception: `SamBaDevice.ReportGeometryMismatch` contains handler failures, because it accompanies no
  work (the device is already open and `ResolveGeometry` has already settled which side's figures are
  in force, per `SamBaGeometryPrecedence`) and it sits inside `Open`'s try, whose catch closes the port.
- `ISambaTransport.StatusReporter` is the same idea one layer down, for calls that cannot be given
  progress *during* because they cannot be given a timeout at all (see the closing note under
  *Timeout ownership*). A transport reports immediately before each blocking control-plane call —
  `SerialPortTransport`/`PortableSerialTransport` emit "Opening port", "Setting DTR" (Win32 only),
  "Purging buffered data" and "Closing port" — so a stall surfaces as a named step instead of a
  silent gap before the eventual failure. `SambaMonitor` is the only wirer: `Connect` points it at
  `Report(_, ProgressStage.Connecting)` for the whole handshake, including its one retry and both
  `ClearStalePipe` purges; `Disconnect` repoints it at the new `ProgressStage.Disconnecting` first.
  Nothing resets it in between — the property is only ever read from inside a call one of those two
  methods made, so a stale delegate from a previous session is not observable.
- `HandshakeWithRetry` reports "Retrying handshake on a freshly reopened port"
  (`ProgressStage.Connecting`) right before its one retry — unconditionally, since the method carries
  no budget of its own; `OpenPortBudget` (see *Timeout ownership*) is what decides in `Connect`
  whether the method is called at all. Placed there rather than at the top of the method so a
  consumer never sees it for the ordinary case — the first attempt succeeding — only for the two
  documented reasons the retry exists.

## Boot source

Two facts about the hardware, and everything here follows from them:

- **Every part in the chip table boots flash unless a GPNVM bit says otherwise.** No part's *fixed* source
  is the ROM. On a SAM7SE/X/XC or any EEFC part the bit selects ROM or flash; a small SAM7S has no bit
  because it always boots flash — it carries SAM-BA in ROM like every CHIPID part but cannot run it from
  there, so a `Boot` button copies the monitor into flash and it relocates itself to RAM to run. An NVMCTRL part
  has no bit because it has no SAM-BA ROM at all; its bootloader is flash-resident. Holding the monitor
  in ROM and being able to boot it are separate properties, and `SamBaChipBootSource` is about the second.
- **The bit is sticky and survives an erase.** On a part that has one, pointing it back at flash is
  what lets newly written firmware run instead of the monitor coming up again. That is why
  `UpdateFirmware` applies options *after* the erase and write, not before — the ordering is
  load-bearing, and commented as such at the call site.

`SamBaChipBootSource { Rom, Flash }` rather than a bool, and `CanSelectBootSource` rather than
`CanBootFlash`. Both names are the result of getting this wrong: the Bossa flag reads as "this part can
boot flash", which is false for exactly the parts it returns false for, and `Efc.GetBootSource`
inherited that reading and answered `Rom` for the SAM7S — inverting all three boot members on those
parts, with a test and a `ChipTable` comment that both looked deliberate. The current name says what is
tested (is there a bit?) rather than what a reader guesses it tests.

One rule across all four controllers: **a request for the source the part already uses is a no-op,
whether or not that family could have moved; only an unreachable source throws.** So
`CanSelectBootSource` alone never decides the answer — `GetBootSource` reports the source in force, and
on a fixed-source part the impossible request is therefore always `Rom`. `SamBaDevice.SetBootSource`
keeps no capability check of its own (it had one, and it refused requests NVMCTRL parts had already
satisfied by construction). `UpdateFirmware` still filters on `CanSelectBootSource` before asking, so a
default update never raises the option on a part with nothing to set.

`SamBaUpdateOptions.SetBootToFlash` stays a `bool` while the device method takes the enum, and that
asymmetry is deliberate: the option is a *step to perform or skip*, like `BulkErase`, not a selection.
Its `false` leaves the boot configuration alone. Making it a `SamBaChipBootSource?` would
offer "point this part at the ROM as part of a firmware update", which is not a thing anyone wants.

## Exceptions

A closed hierarchy under `SamBaException`: the base is public so one catch covers every *device*
failure, but every constructor is internal, so nothing outside the assembly can raise or extend one.
All leaves are sealed.

Two kinds of failure stay outside that hierarchy on purpose. Caller mistakes are BCL exceptions —
`ArgumentOutOfRangeException` for a bad offset, `ObjectDisposedException` for a dead device. So are
the library's own internal disagreements, and those split in two, which is worth keeping straight
because both look like "we can't do that":

- **`InvalidOperationException` — the tables and the code disagree.** The chip table names a flash
  controller `FlashController.Create` does not build, or a controller kind
  `ChipRecord.PagesPerWriteBlock` has not been taught, or an EFC/EEFC family `ChipTable.TraitsOf` has
  no traits for. Unreachable while the two agree, no device answer can cause one, and every case means
  a code change.
- **`NotSupportedException` — a geometry this controller cannot drive.** `Efc`'s constructor, whose
  MC_FSR reports at most 16 lock regions per plane; `NvmFamilyController.OnInitialize`, whose lock
  bits have to fit inside the user-area buffer it reads and rewrites. Different claim, and reachable
  from data rather than only from a code gap — since geometry adoption, the second one can be reached
  by a part's own PARAM answer (a D5x reporting an 8-byte page leaves 8 bytes of user area where 12
  are needed), and the first is reachable by any table row that carries more lock regions per plane
  than the register can report.

Neither is a `SamBaException`, so neither is something a consumer catches by device-failure type. That
is right for the first group and a rough edge on the second, where a part can be refused during
`Open()` for what it reported about itself. No live row reaches either check — the row that used to
reach the first is disabled, see the note on it in `ChipTable` — so the edge is latent, and
`SamBaUnsupportedDeviceException` is where these belong if it ever stops being.

The context property is called `Operation` on every type that has one, and it carries either the
library method that issued the command (`nameof(WriteBlock)`) or a description of the specific
command where that is more precise (`"row erase (ER)"`, `"boot-source change"`) — never a bare
opcode, and never a register or peripheral name.

`SamBaFlashCommandException` covers three causes and each throw site sets the one flag that applies:
`IsCommandError`, `IsLockError`, or `IsUnsupported` — the last meaning no command reached the
controller at all because the family cannot do what was asked, so a retry is pointless. Every site is
a flash-controller operation; nothing else borrows the type for want of a better one.

`SamBaUnsupportedOperationException` is the same claim about work that never reaches a flash
controller, which is why it is a type rather than a fourth flag. Today one site raises it:
`SamBaDevice.Reset` on a family `DeviceResetter` has no route for. That used to be an
`InvalidOperationException`, which was defensible while a missing route could only mean a gap between
the chip table and the reset code — but geometry fallback made `SamBaChipFamily.Unknown` a family real
devices are placed in, so a reachable device-capability failure was sitting outside the hierarchy that
one catch is supposed to cover. Reusing `SamBaFlashCommandException` with `IsUnsupported` would have
kept the surface smaller at the cost of raising a flash-command error for something no flash
controller was asked to do.

`Unknown` is narrower than "any unlisted part" now that `ChipTable.TryFindChipIdFamilyFallback` and
`ChipIdentifier.NvmProvisional` try `Families.TryResolveChipIdFamily`/`TryResolveDeviceIdFamily`
first: an unlisted variant whose CIDR (ARCH+EPROC) or DSU DID (upper word) still names a known family
gets that family — and its real EEFC base, boot GPNVM bit, unique-id word count, and reset route —
instead of `Unknown`. Only a part matching no family's pattern at all falls through to `Unknown`, and
even then the no-family-specific-route case above is narrower still: `DeviceResetter.TryReset` also
takes the initial probe's CPUID, and a part that read one there is confirmed Cortex-M regardless of
family, so it still resets through the architectural AIRCR. `SamBaUnsupportedOperationException` is
reachable only by the remaining case — identified on the legacy CHIPID-only branch, which never reads
a CPUID, so nothing confirms even that much about the core.

Every public member's XML names what it throws, the two that reach nearly all of them included:
`SamBaDeviceNotOpenException` before `Open()` and `ObjectDisposedException` after `Dispose()`. Terse
and repetitive on purpose — a per-member tag is the only form a call site's tooltip shows, so a
sentence in the class remarks would be invisible where it is needed. The compiler does not help here:
`GenerateDocumentationFile` checks that a member *has* a doc comment (CS1591), never that it has the
right tags, and CS1573 only complains about a missing `<param>` when the member documents some of its
parameters and not others — an all-or-nothing omission passes silently.

No binary serialization: no `[Serializable]`, no `ISerializable` pair. It would only ever be exercised
by `BinaryFormatter` (obsolete since .NET 5, throws on .NET 8, gone in .NET 9) or by an AppDomain
boundary on `net48`, and neither is in this library's path. Carrying it meant well over a hundred
lines across seven files that no test can reach, plus an invariant nothing enforces — add a property,
forget `GetObjectData`, and the round-trip silently drops it. JSON and XML serialization never
consulted those members anyway.

## Thread safety

None, by design. One monitor per device, driven by its owning `SamBaDevice`. Commands are a
request/reply exchange over a single serial port, so concurrent callers would interleave on the wire
regardless of the shared scratch buffers (`_replyBuffer`, `_wordCommand`, and the word-batch pair
`_readBatchCommands`/`_readBatchReplies` plus `_writeBatchCommands`). Callers
that need concurrency serialize at the device level.

## Language and build constraints

- **`LangVersion` 7.3** for all four TFMs (`net48;netstandard2.0;net8.0;net8.0-windows7.0`). No
  nullable reference types, switch expressions, `using` declarations, target-typed `new`, or range
  syntax in the library. The test project targets `net8.0` and `net8.0-windows7.0` with nullable
  enabled and is not bound by this.
- **Zero warnings** across every TFM, library and WPF app, with `EnableNETAnalyzers` and
  `EnforceCodeStyleInBuild` on. `GenerateDocumentationFile` is on, so a broken `cref` is a warning —
  avoid crefs that cross into another namespace's internal types and use `<c>Type.Member</c>` instead.
- **LF line endings** on every source file.
- **Comments cite the hardware.** A register write, a wait loop, or a wire quirk is explained by the
  datasheet behaviour that forces it — the erratum, the bus width, the USB packet rule — so the next
  reader can check the claim against the part.

## Tests

xUnit, `net8.0` and `net8.0-windows7.0`, with `InternalsVisibleTo` reaching the internals above.
The `net8.0` build resolves the library's portable target, so the portable surface (including
`PortableSerialTransport`) is compiled and exercised for real; the discovery and watcher tests
compile only on the windows target, the portable-transport tests only on `net8.0`.

- `ScriptedTransport` answers registered command→reply pairs and records every write verbatim;
  `WrittenText` joins the writes with `|` so a test can pin an exact command sequence. It also counts
  purges and reads, which is how the handshake's "no speculative read" property is pinned — a read that
  costs a full timeout on hardware is invisible in a fake unless something counts it.
- `FakeSamDevice` models flash and controller registers well enough to run whole write loops. It does
  not model erase, so a test cannot yet observe flash being blanked. `EnqueueWordRead` scripts
  successive reads of one address to return successive values — the only way to express a result
  register (EEFC `FRR`), whose every read yields the next word of a descriptor or lock-bit run.
- Many assertions pin **exact register write values and polling order** — which plane's status is read
  first, that error bits are checked before the ready bit, that a page write commits after the latch
  load. Those orders come from the datasheets and matter on hardware; refactors must preserve them
  rather than update the assertions to match new behaviour.

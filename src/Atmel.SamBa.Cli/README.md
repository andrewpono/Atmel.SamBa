# SAM-BA CLI (`sambac`)

Command-line tool for flashing Atmel/Microchip SAM devices over the SAM-BA USB CDC monitor,
built on the [Anp.Atmel.SamBa](../Atmel.SamBa/README.md) library. The option surface follows
`bossac` (BOSSA's CLI), so existing invocations — Arduino's among them — carry over with at
most a change of executable name.

## Usage

```
sambac [OPTION...] [FILE]
```

| Option | Description |
|--------|-------------|
| `-e`, `--erase` | Erase the whole flash. With `--write`: full erase before programming. |
| `-w`, `--write` | Write FILE to flash. Without `--erase`, each write block is auto-erased as it is written (on SAM3/SAM4/SAMx7x this only works within the first 16 KB — combine with `--erase`). |
| `-r`, `--read[=SIZE]` | Read SIZE bytes of flash into FILE (default: the entire flash). |
| `-v`, `--verify` | Verify FILE against flash. With `--write`: read-back verification after programming. |
| `-o`, `--offset=OFFSET` | Flash byte offset for write/read/verify (default: 0). Decimal or `0x` hex. |
| `-p`, `--port=PORT` | Port to use: `COM7`, `/dev/ttyACM0`, `/dev/cu.usbmodem…`. A bare name like `ttyACM0` gets `/dev/` prefixed on Linux/macOS. Default: auto-scan (see *Platforms*). |
| `-b`, `--boot[=BOOL]` | Boot from flash if BOOL is 1 (the default), from ROM if 0. |
| `--identify=MODE` | Chip identification probe: `auto` (default), `chipid` or `cpuid`. Not a `bossac` option — see below before using `chipid`/`cpuid`. |
| `--geometry-precedence=WHICH` | Which account wins on a table/device flash-geometry disagreement: `table` (default) or `device`. Not a `bossac` option — see below before using `device`. |
| `-l`, `--lock[=LIST]` | Lock the comma-separated region LIST (`-l 0,1,2`), or **all** regions when bare. Decimal region numbers only; no range syntax, as in `bossac`. |
| `-u`, `--unlock[=LIST]` | Unlock the LIST or **all** regions (before erase/write when combined with `--write`). Same syntax. |
| `-s`, `--security` | Set the security bit. **Irreversible** — blocks further SAM-BA access; only the ERASE pin recovers the part. |
| `-i`, `--info` | Display device information (chip, monitor version, geometry, locks, security, boot source, unique id). |
| `-R`, `--reset` | Reset the CPU after all other operations. |
| `-h`, `--help` | Help text. |
| `--version` | Version. |

Multiple operations combine in one invocation and run in the conventional order:
unlock → erase → write (+verify) → read → boot → lock → security → info → reset.
Lock always precedes security — the security bit cuts off further flash-controller
commands, so nothing can be sequenced after it.

```bash
sambac -p COM7 -i
sambac -e -w -v -b firmware.bin
sambac -p /dev/ttyACM0 -r 8192 dump.bin
sambac -p COM7 -u -e -w -v -R firmware.bin
sambac -p COM7 -u 0,1 -e -w -v firmware.bin
```

Note that, as in `bossac`, locked regions are **not** unlocked unless `-u` is given, and the
boot source is **not** touched unless `-b` is given.

`--identify` overrides how the library decides between the legacy AT91SAM7/9 CHIPID probe and
the Cortex-M CPUID probe. `auto` (the default) reads the ARM reset vector at address 0 and lets
its content decide, which is usually right but is only ever a guess — a genuine AT91SAM7/9 part
is not guaranteed to have a branch opcode there (blank flash, for instance), and a wrong guess
hangs the SAM-BA monitor on the next read. `chipid`/`cpuid` skip that guess and force the named
probe directly; only use one of them on a part whose core generation you already know, since
forcing the wrong one causes the same hang on purpose.

`--geometry-precedence` overrides which account wins when the part's own reported flash geometry
disagrees with its device-table row. `table` (the default) keeps the datasheet-sourced row — safe
for every part this library already lists, since the device's account is read but never verified
against real silicon. `device` trusts the part's own reading instead; only use it when the table
row itself is known to be wrong for the connected part and no corrected release of this library is
available yet. Either way a disagreement is always printed as a progress message at connect time —
this option only changes which geometry the rest of the run uses.

On SAM3/SAM4/SAM9XE/SAMx7x, `--read` and `--verify` run word-by-word (noticeably slower): the
ROM's block read answers with zeros for flash, including its boot alias at address 0.

### Options accepted but not supported

These parse (so `bossac` invocations do not trip over them) but always fail with an error and
exit code 3:

- `-c`, `--bod[=BOOL]` and `-t`, `--bor[=BOOL]` — brown-out configuration is deliberately
  outside the library's scope.
- `-d`, `--debug` — no protocol trace output.
- `-U`, `--usb-port=0` — RS-232/UART cannot be driven; USB CDC is the only transport
  (`-U`/`-U 1` is accepted as a no-op).
- `-a`, `--arduino-erase` — the 1200 baud erase touch is not implemented.

Option bundling (`-ewv`) is not supported; write the flags separately.

## Platforms

Two builds ship, mirroring the library's platform split:

| Build | Runs on | Auto-scan without `--port` |
|-------|---------|----------------------------|
| `net48` | Windows | Yes — enumerates SAM-BA CDC ports (VID 0x03EB, PID 0x6124) and takes the first. |
| `net8.0` | Windows, Linux, macOS | Yes, but unfiltered — takes the first serial port the OS reports, whether or not it's a SAM-BA device. Use `--port` when more than one is present. |

Linux: opening the port needs read/write permission on the tty (the `dialout` group on most
distributions). macOS: use the `/dev/cu.*` path, not `/dev/tty.*`.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success (also help/version). |
| 1 | No arguments / no operation specified. |
| 2 | Invalid argument or combination. |
| 3 | Unsupported option or capability. |
| 4 | Device or verification failure. |
| 99 | Unexpected internal error. |

## Publishing

Publish profiles for framework-dependent single-file builds are under
`Properties/PublishProfiles` (linux-x64, linux-arm64, osx-x64, osx-arm64):

```bash
dotnet publish -c Release -f net8.0 -r linux-x64 --self-contained false -p:PublishSingleFile=true
```

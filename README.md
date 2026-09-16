# Atmel.SamBa

.NET library, CLI tool, and WPF apps for Atmel/Microchip **SAM-BA** device programming over USB CDC — device discovery, chip identification, memory read/write, flash erase/write/verify, GPNVM boot configuration, lock regions, and security bit.

Inspired by [BOSSA](https://github.com/shumatech/BOSSA) 1.9.1, but applet-free — unlike BOSSA, flash pages are programmed directly through the flash controller registers over the SAM-BA monitor protocol, with no code uploaded to the target.

## Projects

| Project | Description | Platforms |
|---------|-------------|-----------|
| [Anp.Atmel.SamBa](src/Atmel.SamBa/README.md) | Core library — discovery, hot-plug watching, chip identification, erase, read, write, verify, boot/lock/security options. | Windows, Linux, macOS (discovery and hot-plug watching: Windows only) |
| [Atmel.SamBa.Cli](src/Atmel.SamBa.Cli/README.md) | Command-line tool (`sambac`) with a `bossac`-compatible option surface. | Windows (`net48`, filtered auto-scan), Windows/Linux/macOS (`net8.0`, unfiltered auto-scan or `--port`) |
| [Atmel.SamBa.FirmwareUpdater](src/Atmel.SamBa.FirmwareUpdater/README.md) | WPF firmware updater (`SamBaUpdater`) — erase, update, verify, lock, reset. | Windows (`net48`) |
| [Atmel.SamBa.Lite](src/Atmel.SamBa.Lite/README.md) | WPF engineering tool (`SamBaLite`) — hex memory view, word/file writes, lock/security/boot control, FW updater. | Windows (`net48`) |

## Quick Start

Install from [NuGet](https://www.nuget.org/packages/Anp.Atmel.SamBa):

```
dotnet add package Anp.Atmel.SamBa
```

```csharp
var devices = SamBaDeviceDiscovery.Enumerate();

using (var device = devices[0])
{
    device.ProgressChanged += (s, e) => Console.WriteLine(e);
    device.Open();
    Console.WriteLine(device.ChipInfo);   // e.g. ATSAM3X8 (Sam3X): 512 KB flash ...

    byte[] firmware = File.ReadAllBytes("firmware.bin");
    device.UpdateFirmware(firmware);      // write + verify + boot-to-flash + reset
}
```

See the [library README](src/Atmel.SamBa/README.md) for full API documentation, and
[docs/DESIGN.md](docs/DESIGN.md) for the internal layering and coding conventions.

## Supported chips

The full BOSSA 1.9.1 device table plus later additions — 185 rows, every field checked against the
vendor datasheets and, for the parts identified by DSU device id, the family silicon errata:

| Family | Flash controller | Examples |
|--------|------------------|----------|
| SAM7S* / SAM7SE* / SAM7X / SAM7XC | Legacy EFC | AT91SAM7S256, AT91SAM7SE512 |
| SAM3S / SAM3U / SAM3X* / SAM3A | EEFC | ATSAM3X8E (Arduino Due) |
| SAM4S / SAM4E / SAM9XE | EEFC | ATSAM4S16, AT91SAM9XE256 |
| SAME70 / SAMS70 / SAMV70 / SAMV71 | EEFC | ATSAME70Q21 |
| SAMC21 / SAMD21* / SAMR21 / SAML21 | NVMCTRL (row erase) | ATSAMD21G18 (Arduino Zero) |
| SAMD51 / SAME51 / SAME53 / SAME54 | NVMCTRL (block erase) | ATSAMD51J20, ATSAME54P20 |

\* Hardware-tested.

**Not supported:** SAM3N and SAM7L — no USB device port on any variant (only UART0/DBGU, which
this library's USB CDC transport can't reach) and both end-of-life; and the qNimble Quarto
bootloader — its device-table row claims lock-region counts the legacy `MC_FSR` register can't
represent. All three rows are commented out so identification rejects them up front rather than
failing mid-sequence.

`AT91SAM7S32`/`AT91SAM7S16` have no USB port either but stay enabled — after the CHIPID
revision-bit mask they alias to the USB-equipped `AT91SAM7S321`/`AT91SAM7S161`, whose flash
geometry they share exactly.

## Target Frameworks

| Project | net48 | netstandard2.0 | net8.0 | net8.0-windows |
|---------|:-----:|:--------------:|:------:|:--------------:|
| Library (`Anp.Atmel.SamBa`) | x | x | x | x |
| CLI (`sambac`) | x | | x | |
| FirmwareUpdater (`SamBaUpdater`) | x | | | |
| Lite (`SamBaLite`) | x | | | |

On the Windows targets (`net48`, `net8.0-windows`) the transport is
[Anp.Serial.Win32](https://www.nuget.org/packages/Anp.Serial.Win32) (overlapped Win32 serial I/O,
CfgMgr32 discovery and hot-plug notifications). On the portable targets (`netstandard2.0`,
`net8.0`) the transport is System.IO.Ports: construct devices by port path —
`new SamBaDevice("/dev/ttyACM0")` — with no `SamBaDeviceDiscovery` or `SamBaDeviceWatcher`.
Asset selection follows the consuming project's TFM, not the machine: a plain `net8.0` app gets
the portable surface even on Windows — target a `-windows` TFM to keep discovery.

## References

- [BOSSA](https://github.com/shumatech/BOSSA) — the reference implementation this library is inspired by

## License

MIT — see [LICENSE](LICENSE). Contains logic derived from BOSSA (BSD-3-Clause) — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

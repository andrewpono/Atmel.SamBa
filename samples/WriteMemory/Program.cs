// WriteMemory — Write a binary file to an arbitrary memory address.
//
// device.WriteMemory always auto-erases page by page as it writes to a flash address (RAM and
// peripheral addresses are a plain raw write) — no separate erase step is needed for a small
// write. On SAM4 / SAMx7x that auto-erase only reaches the first 16 KB of flash (the EEFC
// erase-and-write-page command's own limit); pass --bulk-erase-first to erase the whole chip
// with EraseAllFlash() before writing, which lifts that limit the same way
// SamBaUpdateOptions.BulkErase does for UpdateFirmware.
//
// Usage: WriteMemory <address> <input.bin> [--bulk-erase-first]
//
// Examples:
//   WriteMemory 0x00080000 payload.bin                   -- write, per-page auto-erase
//   WriteMemory 0x00080000 payload.bin --bulk-erase-first -- erase the chip, then write
//   WriteMemory 0x20000000 scratch.bin                   -- write to RAM (no erase involved)

using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using System;
using System.Globalization;
using System.IO;

namespace Samples.WriteMemory
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2 || args[0] == "--help")
            {
                Console.WriteLine("Usage: WriteMemory <address> <input.bin> [--bulk-erase-first]");
                Console.WriteLine();
                Console.WriteLine("  address            Target start address (hex, e.g. 0x00080000)");
                Console.WriteLine("  input.bin          Binary file to write");
                Console.WriteLine("  --bulk-erase-first Erase the whole chip before writing");
                return 1;
            }

            if (!TryParseUint(args[0], out uint address))
            {
                Console.Error.WriteLine($"Invalid address: {args[0]}");
                return 1;
            }

            string inputPath = args[1];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"File not found: {inputPath}");
                return 1;
            }

            bool bulkEraseFirst = Array.Exists(args, a => a == "--bulk-erase-first");

            byte[] data = File.ReadAllBytes(inputPath);
            if (data.Length == 0)
            {
                Console.Error.WriteLine("Input file is empty.");
                return 1;
            }

            Console.WriteLine($"Input: {inputPath} ({data.Length} bytes)");
            Console.WriteLine($"Target: 0x{address:X8} .. 0x{address + (uint)data.Length - 1:X8}");
            Console.WriteLine();

            var devices = SamBaDeviceDiscovery.Enumerate();

            if (devices.Count == 0)
            {
                Console.Error.WriteLine("No SAM-BA device found.");
                return 1;
            }

            using (var device = devices[0])
            {
                device.ProgressChanged += OnProgress;
                device.Open();

                Console.WriteLine($"Device: {device.DisplayName}");
                Console.WriteLine();

                try
                {
                    if (bulkEraseFirst)
                    {
                        Console.WriteLine("Erasing entire chip...");
                        device.EraseAllFlash();
                        Console.WriteLine();
                    }

                    Console.WriteLine("Writing...");
                    device.WriteMemory(address, data);
                    Console.WriteLine();

                    Console.WriteLine("Write complete.");
                }
                catch (SamBaFlashCommandException ex)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Flash command failed: {ex.Message}");
                    if (ex.IsLockError)
                        Console.Error.WriteLine("  The target region is locked; unlock it first with " +
                            "device.SetLockRegions(false).");
                    if (ex.IsUnsupported)
                        Console.Error.WriteLine("  Pass --bulk-erase-first to erase the whole chip " +
                            "up front instead of relying on per-page auto-erase.");
                    return 2;
                }
            }

            return 0;
        }

        private static void OnProgress(object sender, SamBaProgressEventArgs e)
        {
            if (e.IsIndeterminate)
            {
                Console.WriteLine($"  [{e.Stage}] {e.Message}");
            }
            else
            {
                Console.Write($"\r  [{e.Stage}] {e.Percentage,3}% — {e.Message}");
                if (e.Value == e.Maximum)
                    Console.WriteLine();
            }
        }

        private static bool TryParseUint(string text, out uint result)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out result);

            return uint.TryParse(text, out result);
        }
    }
}

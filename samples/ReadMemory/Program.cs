// ReadMemory — Read a range of device memory and save to file or display as hex.
//
// Usage: ReadMemory <address> <length> [output.bin] [--safe-mode]
//
// Examples:
//   ReadMemory 0x00080000 1024              — hex dump 1 KB from flash start (SAM3X)
//   ReadMemory 0x00080000 65536 dump.bin    — save 64 KB to file
//
// EEFC parts (SAM3/SAM4/SAM9XE/SAMx7x) answer block reads of flash with all zeros, so this
// library reads those one 32-bit word at a time instead — ReadMemory hides that automatically.
// Pass --safe-mode if a device drops data mid-read (drops the pipelining, one word per exchange).

using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Events;
using System;
using System.Globalization;
using System.IO;

namespace Samples.ReadMemory
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: ReadMemory <address> <length> [output.bin] [--safe-mode]");
                Console.WriteLine();
                Console.WriteLine("  address     Start address (hex, e.g. 0x00080000)");
                Console.WriteLine("  length      Number of bytes to read (decimal or hex with 0x prefix)");
                Console.WriteLine("  output      Optional output file (omit for hex dump to console)");
                Console.WriteLine("  --safe-mode Read one word per exchange instead of batching");
                return 1;
            }

            if (!TryParseUint(args[0], out uint address))
            {
                Console.Error.WriteLine($"Invalid address: {args[0]}");
                return 1;
            }

            if (!TryParseInt(args[1], out int length) || length <= 0)
            {
                Console.Error.WriteLine($"Invalid length: {args[1]}");
                return 1;
            }

            bool safeMode = Array.Exists(args, a => a == "--safe-mode");
            string outputPath = args.Length > 2 && args[2] != "--safe-mode" ? args[2] : null;

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
                device.SafeMode = safeMode;

                Console.WriteLine($"Device: {device.DisplayName}");
                Console.WriteLine($"Reading {length} bytes from 0x{address:X8}...");
                Console.WriteLine();

                byte[] data = device.ReadMemory(address, length);

                if (outputPath != null)
                {
                    File.WriteAllBytes(outputPath, data);
                    Console.WriteLine($"Saved {data.Length} bytes to {outputPath}");
                }
                else
                {
                    HexDump(data, address);
                }
            }

            return 0;
        }

        private static void OnProgress(object sender, SamBaProgressEventArgs e)
        {
            if (!e.IsIndeterminate)
                Console.Write($"\r  {e.Percentage,3}% — {e.Message}");

            if (e.Value == e.Maximum)
                Console.WriteLine();
        }

        private static void HexDump(byte[] data, uint baseAddress)
        {
            const int bytesPerLine = 16;

            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                int count = Math.Min(bytesPerLine, data.Length - i);

                // Address
                Console.Write($"  {(baseAddress + (uint)i):X8}  ");

                // Hex bytes
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (j == 8) Console.Write(" ");

                    if (j < count)
                        Console.Write($"{data[i + j]:X2} ");
                    else
                        Console.Write("   ");
                }

                // ASCII
                Console.Write(" |");
                for (int j = 0; j < count; j++)
                {
                    byte b = data[i + j];
                    Console.Write(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                Console.WriteLine("|");
            }
        }

        private static bool TryParseUint(string text, out uint result)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out result);

            return uint.TryParse(text, out result);
        }

        private static bool TryParseInt(string text, out int result)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(text.Substring(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out result);

            return int.TryParse(text, out result);
        }
    }
}

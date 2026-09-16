// BasicUpdate — Discover a SAM-BA device and update its firmware.
//
// Usage: BasicUpdate <firmware.bin> [--bulk-erase] [--no-verify] [--unlock] [--offset 0x2000]
//
// Windows targets only (net48, net8.0-windows): device discovery needs Anp.Serial.Win32. On the
// portable targets (netstandard2.0, net8.0) construct the device directly from the OS port name
// instead — see the ExternalDiscovery sample.

using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using System;
using System.IO;

namespace Samples.BasicUpdate
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help")
            {
                Console.WriteLine(
                    "Usage: BasicUpdate <firmware.bin> [--bulk-erase] [--no-verify] [--unlock] " +
                    "[--offset 0x2000]");
                return 1;
            }

            string filePath = args[0];
            bool bulkErase = Array.Exists(args, a => a == "--bulk-erase");
            bool verify = !Array.Exists(args, a => a == "--no-verify");
            bool unlock = Array.Exists(args, a => a == "--unlock");
            uint offset = ParseOffsetArg(args);

            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"File not found: {filePath}");
                return 1;
            }

            byte[] firmware = File.ReadAllBytes(filePath);
            Console.WriteLine($"Firmware: {filePath} ({firmware.Length} bytes)");

            // Enumerate SAM-BA USB CDC ports using the default VID/PID (0x03EB / 0x6124).
            var devices = SamBaDeviceDiscovery.Enumerate();

            if (devices.Count == 0)
            {
                Console.Error.WriteLine("No SAM-BA device found.");
                return 1;
            }

            if (devices.Count > 1)
                Console.WriteLine($"Found {devices.Count} devices, using the first one.");

            using (var device = devices[0])
            {
                device.ProgressChanged += OnProgress;

                Console.WriteLine($"Device: {device.DisplayName}");

                device.Open();

                Console.WriteLine(device.ChipInfo);
                Console.WriteLine();

                try
                {
                    // Defaults (options: null) are write (auto-erasing page by page), verify,
                    // boot-to-flash, reset — without bulk-erasing, unlocking, locking or touching
                    // the security bit. Only override what this run actually needs.
                    device.UpdateFirmware(firmware, new SamBaUpdateOptions
                    {
                        BulkErase = bulkErase,
                        Verify = verify,
                        UnlockBeforeWrite = unlock,
                        Offset = offset,
                    });
                    Console.WriteLine();
                    Console.WriteLine("Firmware update successful.");
                }
                catch (SamBaVerificationException ex)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Verification failed: {ex.Message}");
                    Console.Error.WriteLine($"  Address: 0x{ex.MismatchAddress:X8}");
                    return 2;
                }
                catch (SamBaFlashCommandException ex)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Flash command failed: {ex.Message}");
                    if (ex.IsLockError)
                        Console.Error.WriteLine("  Pass --unlock to clear locked regions first.");
                    if (ex.IsUnsupported)
                        Console.Error.WriteLine("  Pass --bulk-erase to erase the whole chip up " +
                            "front instead of relying on per-page auto-erase.");
                    return 2;
                }

                // UpdateFirmware resets and closes the device itself when options.Reset is true
                // (the default) — nothing further to do here.
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

        private static uint ParseOffsetArg(string[] args)
        {
            int idx = Array.IndexOf(args, "--offset");
            if (idx < 0 || idx + 1 >= args.Length)
                return 0;

            string val = args[idx + 1];
            if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                val = val.Substring(2);

            return uint.TryParse(val, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint result)
                ? result
                : 0;
        }
    }
}

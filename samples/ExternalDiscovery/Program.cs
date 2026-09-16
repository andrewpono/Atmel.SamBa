// ExternalDiscovery — Create a SamBaDevice from a known port path.
//
// Demonstrates constructing SamBaDevice directly when the port comes from an external source
// such as PnpDeviceToolkit, RegisterDeviceNotification, WMI, or the OS port name on the
// portable targets (netstandard2.0, net8.0) — those have no discovery of their own.
//
// Usage: ExternalDiscovery <device-path-or-port> [firmware.bin]
//
// Examples:
//   ExternalDiscovery "\\?\usb#vid_03eb&pid_6124#..."   (Windows device-interface path)
//   ExternalDiscovery COM7 firmware.bin                 (Windows COM port name)
//   ExternalDiscovery /dev/ttyACM0                      (Linux)
//   ExternalDiscovery /dev/cu.usbmodem14101             (macOS)

using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Exceptions;
using System;
using System.IO;

namespace Samples.ExternalDiscovery
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: ExternalDiscovery <device-path-or-port> [firmware.bin]");
                Console.WriteLine();
                Console.WriteLine("Creates a SamBaDevice from a path or port name obtained externally.");
                Console.WriteLine("If a firmware file is provided, updates it; otherwise reads and");
                Console.WriteLine("displays the first 256 bytes of flash.");
                Console.WriteLine();
                Console.WriteLine("To find device paths on Windows, use SamBaDeviceDiscovery.Enumerate()");
                Console.WriteLine("or an external tool such as PnpDeviceToolkit:");
                Console.WriteLine("  https://github.com/andrewpono/PnpDeviceToolkit");
                return 1;
            }

            string devicePath = args[0];
            string firmwarePath = args.Length > 1 ? args[1] : null;

            // Create the device directly from a known path or port name. The device is not opened.
            using (var device = new SamBaDevice(devicePath))
            {
                device.ProgressChanged += OnProgress;
                device.Open();

                Console.WriteLine($"Device: {device.DisplayName}");
                Console.WriteLine(device.ChipInfo);
                Console.WriteLine();

                if (firmwarePath != null)
                {
                    if (!File.Exists(firmwarePath))
                    {
                        Console.Error.WriteLine($"File not found: {firmwarePath}");
                        return 1;
                    }

                    byte[] firmware = File.ReadAllBytes(firmwarePath);
                    Console.WriteLine($"Updating firmware: {firmwarePath} ({firmware.Length} bytes)");

                    try
                    {
                        // Defaults: auto-erasing write, verify, boot-to-flash, reset.
                        device.UpdateFirmware(firmware);
                        Console.WriteLine();
                        Console.WriteLine("Update successful.");
                    }
                    catch (SamBaVerificationException ex)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"Verification failed: {ex.Message}");
                        return 2;
                    }
                }
                else
                {
                    // No firmware file — just read and display the first 256 bytes of flash.
                    int readLen = Math.Min(256, (int)device.ChipInfo.FlashSize);

                    Console.WriteLine($"Reading {readLen} bytes from 0x{device.ChipInfo.FlashAddress:X8}...");
                    byte[] data = device.ReadMemory(device.ChipInfo.FlashAddress, readLen);

                    string preview = BitConverter.ToString(data, 0, Math.Min(16, data.Length));
                    Console.WriteLine($"First 16 bytes: {preview}");
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
    }
}

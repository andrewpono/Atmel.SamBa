// DeviceWatcher — Monitor for SAM-BA devices connecting and disconnecting.
//
// Runs until Enter is pressed. Demonstrates SamBaDeviceWatcher with shared discovery defaults.
//
// Windows targets only (net48, net8.0-windows): the watcher is built on
// Anp.Serial.Win32's CM_Register_Notification and requires Windows 8 or later.

using Anp.Atmel.SamBa;
using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using System;

namespace Samples.DeviceWatcher
{
    class Program
    {
        static void Main(string[] args)
        {
            // Configure shared discovery defaults.
            // These are used by both SamBaDeviceDiscovery.Enumerate() and SamBaDeviceWatcher.
            SamBaDeviceDiscovery.ConfigureDefaults(options =>
            {
                options.VendorId = SamBaDevice.DefaultVendorId;
                options.ProductId = SamBaDevice.DefaultProductId;
            });

            Console.WriteLine("Watching for SAM-BA devices. Press Enter to stop.");
            Console.WriteLine();

            // List devices already connected
            var existing = SamBaDeviceDiscovery.Enumerate();
            if (existing.Count > 0)
            {
                Console.WriteLine($"Already connected ({existing.Count}):");
                foreach (var d in existing)
                {
                    PrintDevice(d);
                    d.Dispose();
                }
                Console.WriteLine();
            }

            // Start watching. The filter above is read once, at Start() — restart the watcher
            // if ConfigureDefaults is called again while it is running.
            using (var watcher = new SamBaDeviceWatcher())
            {
                watcher.DeviceArrived += OnDeviceArrived;
                watcher.DeviceRemoved += OnDeviceRemoved;
                watcher.Start();

                Console.ReadLine();

                watcher.Stop();
            }

            Console.WriteLine("Stopped.");
        }

        private static void OnDeviceArrived(object sender, SamBaDeviceArrivedEventArgs e)
        {
            Console.WriteLine($"+ ARRIVED:");
            PrintDevice(e.Device);

            // The device is unopened and this is the only subscriber here, so it owns disposal.
            // With several subscribers, settle on one owner instead — see the event's own doc.
            e.Device.Dispose();
        }

        private static void OnDeviceRemoved(object sender, SamBaDeviceRemovedEventArgs e)
        {
            Console.WriteLine($"- REMOVED: {e.DevicePath}");
            Console.WriteLine();
        }

        private static void PrintDevice(SamBaDevice device)
        {
            Console.WriteLine($"    Display: {device.DisplayName}");

            if (device.VendorId.HasValue)
                Console.WriteLine($"    VID:     0x{device.VendorId:X4}");
            if (device.ProductId.HasValue)
                Console.WriteLine($"    PID:     0x{device.ProductId:X4}");

            if (!string.IsNullOrEmpty(device.FriendlyName))
                Console.WriteLine($"    Name:    {device.FriendlyName}");

            Console.WriteLine($"    Port:    {device.PortName}");
            Console.WriteLine($"    Path:    {device.DevicePath}");
            Console.WriteLine();
        }
    }
}

// ChipInfoInspector — Open a device and display its chip identification, flash geometry, and
// current configuration (boot source, lock regions, security bit, unique id).
//
// Read-only: no erase, write, lock, or security calls are made.

using Anp.Atmel.SamBa;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Samples.ChipInfoInspector
{
    class Program
    {
        static int Main(string[] args)
        {
            var devices = SamBaDeviceDiscovery.Enumerate();

            if (devices.Count == 0)
            {
                Console.Error.WriteLine("No SAM-BA device found.");
                return 1;
            }

            using (var device = devices[0])
            {
                device.Open();

                Console.WriteLine($"Device: {device.DisplayName}");
                Console.WriteLine($"Monitor version: {device.MonitorVersion}");
                Console.WriteLine();

                SamBaChipInfo chip = device.ChipInfo;
                Console.WriteLine($"Name:              {chip.Name}");
                Console.WriteLine($"Family:            {chip.Family}");
                Console.WriteLine($"Chip id:           {FormatHex(chip.ChipId)}");
                Console.WriteLine($"Extended chip id:  {FormatHex(chip.ExtendedChipId)}");
                Console.WriteLine($"Device id:         {FormatHex(chip.DeviceId)}");
                Console.WriteLine($"CPU id:            {FormatHex(chip.CpuId)}");
                Console.WriteLine();

                Console.WriteLine($"Flash address:     0x{chip.FlashAddress:X8}");
                Console.WriteLine($"Flash size:        {FormatSize((uint)chip.FlashSize)}");
                Console.WriteLine($"Pages:             {chip.PageCount} x {chip.PageSize} B");
                Console.WriteLine($"Planes:            {chip.PlaneCount}");
                Console.WriteLine($"Lock regions:      {chip.LockRegionCount}");
                Console.WriteLine($"Write block size:  {chip.WriteBlockSize} B " +
                    $"({chip.WriteBlockCount} blocks)");
                Console.WriteLine();

                Console.WriteLine($"Boot source:       {device.GetBootSource()} " +
                    $"(selectable: {chip.CanSelectBootSource})");

                IReadOnlyList<bool> lockRegions = device.GetLockRegions();
                int lockedCount = lockRegions.Count(locked => locked);
                Console.WriteLine($"Lock state:        {lockedCount}/{lockRegions.Count} regions locked");

                Console.WriteLine($"Security bit:      {(device.GetSecurity() ? "set" : "clear")}");

                if (chip.HasUniqueId)
                {
                    IReadOnlyList<uint> uniqueId = device.GetUniqueId();
                    string words = string.Join(" ", uniqueId.Select(w => $"{w:X8}"));
                    Console.WriteLine($"Unique id:         {words}");
                }
                else
                {
                    Console.WriteLine("Unique id:         (not available on this family)");
                }
            }

            return 0;
        }

        private static string FormatHex(uint? value) => value.HasValue ? $"0x{value:X8}" : "(none)";

        private static string FormatSize(uint bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024 * 1024)} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024} KB";
            return $"{bytes} B";
        }
    }
}

using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Exceptions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;


namespace Anp.Atmel.SamBa.Cli
{
    internal static class Program
    {
        #region Constants

        private const string appName = "sambac";

        private static class ExitCodes
        {
            public const int Success = 0;
            public const int ShowedHelp = 0;
            public const int NoArgs = 1;
            public const int InvalidArgument = 2;
            public const int Unsupported = 3;
            public const int DeviceError = 4;
            public const int UnhandledException = 99;
        }

        #endregion

        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    PrintHelp();
                    return ExitCodes.NoArgs;
                }

                CommandLineOptions options = CommandLineOptions.Parse(args);

                if (options.Help)
                {
                    PrintHelp();
                    return ExitCodes.ShowedHelp;
                }

                if (options.Version)
                {
                    PrintVersion();
                    return ExitCodes.ShowedHelp;
                }

                int rejected = RejectUnsupportedOptions(options);
                if (rejected != ExitCodes.Success)
                    return rejected;

                int invalid = ValidateCombinations(options);
                if (invalid != ExitCodes.Success)
                    return invalid;

                return Run(options);
            }
            catch (SamBaException ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return ExitCodes.DeviceError;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is FormatException ||
                ex is OverflowException ||
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is InvalidOperationException)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return ExitCodes.InvalidArgument;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.ToString());
                return ExitCodes.UnhandledException;
            }
        }

        #region Validation

        /// <summary>
        /// Fails every option that is accepted for command-line compatibility but has no
        /// implementation here, naming each one, so a caller learns about all of them at once.
        /// </summary>
        private static int RejectUnsupportedOptions(CommandLineOptions options)
        {
            var failures = new List<string>();

            if (options.Bod)
                failures.Add("--" + OptionNames.Bod +
                    " is not supported: brown-out detector configuration is outside this tool's scope.");

            if (options.Bor)
                failures.Add("--" + OptionNames.Bor +
                    " is not supported: brown-out reset configuration is outside this tool's scope.");

            if (options.Debug)
                failures.Add("--" + OptionNames.Debug +
                    " is not supported: this tool has no protocol trace output.");

            if (options.ArduinoErase)
                failures.Add("--" + OptionNames.ArduinoErase +
                    " is not supported: the 1200 baud erase touch is not implemented.");

            if (options.UsbPortGiven && !options.UsbPortIsUsb)
                failures.Add("--" + OptionNames.UsbPort +
                    "=0 is not supported: USB CDC is the only transport (RS-232/UART cannot be driven).");

            if (failures.Count == 0)
                return ExitCodes.Success;

            foreach (string failure in failures)
                Console.Error.WriteLine("ERROR: " + failure);

            return ExitCodes.Unsupported;
        }

        private static int ValidateCombinations(CommandLineOptions options)
        {
            if (options.Read && (options.Write || options.Verify))
            {
                Console.Error.WriteLine("ERROR: --" + OptionNames.Read + " is exclusive of --" +
                    OptionNames.Write + " and --" + OptionNames.Verify + ".");
                return ExitCodes.InvalidArgument;
            }

            bool needsFile = options.Write || options.Read || options.Verify;

            if (needsFile && string.IsNullOrEmpty(options.FilePath))
            {
                Console.Error.WriteLine("ERROR: a FILE argument is required for --" + OptionNames.Read +
                    ", --" + OptionNames.Write + " and --" + OptionNames.Verify + ".");
                return ExitCodes.InvalidArgument;
            }

            if (!needsFile && !string.IsNullOrEmpty(options.FilePath))
            {
                Console.Error.WriteLine("ERROR: FILE was given without --" + OptionNames.Read + ", --" +
                    OptionNames.Write + " or --" + OptionNames.Verify + ".");
                return ExitCodes.InvalidArgument;
            }

            if ((options.Write || options.Verify) && !File.Exists(options.FilePath))
            {
                Console.Error.WriteLine("ERROR: input file not found: " + options.FilePath);
                return ExitCodes.InvalidArgument;
            }

            if (options.Erase && !options.Write && options.Offset != 0)
            {
                Console.Error.WriteLine("ERROR: a stand-alone erase always erases the whole flash; --" +
                    OptionNames.Offset + " is honored only together with --" + OptionNames.Write + ".");
                return ExitCodes.Unsupported;
            }

            bool anyOperation = options.Erase || options.Write || options.Read || options.Verify ||
                options.Info || options.Security || options.Reset || options.BootGiven ||
                options.Lock || options.Unlock;

            if (!anyOperation)
            {
                Console.Error.WriteLine("ERROR: no operation specified. Try '" + appName + " --" +
                    OptionNames.Help + "'.");
                return ExitCodes.NoArgs;
            }

            return ExitCodes.Success;
        }

        #endregion

        #region Operations

        private static int Run(CommandLineOptions options)
        {
            // Region lists are parsed up front so a syntax error costs no port open.
            int[] unlockList = ParseRegionListOrNull(options.UnlockRegions, OptionNames.Unlock);
            int[] lockList = ParseRegionListOrNull(options.LockRegions, OptionNames.Lock);

            byte[] image = null;
            if (options.Write || options.Verify)
                image = File.ReadAllBytes(options.FilePath);

            using (SamBaDevice device = CreateDevice(options))
            {
                var reporter = new ConsoleProgressReporter();
                device.ProgressChanged += reporter.OnProgress;

                device.Open(options.IdentificationMode, options.GeometryPrecedence);
                Console.WriteLine("Connected: " + device.DisplayName);
                Console.WriteLine();

                if (options.Unlock && !options.Write)
                {
                    if (unlockList == null)
                    {
                        device.SetLockRegions(false);
                        Console.WriteLine("All regions unlocked.");
                    }
                    else
                    {
                        device.SetLockRegions(unlockList, false);
                        Console.WriteLine("Regions " + FormatRegionList(unlockList) + " unlocked.");
                    }
                }

                if (options.Erase && !options.Write)
                    EraseAll(device);

                if (options.Write)
                    WriteImage(device, image, options, unlockList, lockList);

                if (options.Verify && !options.Write)
                {
                    int verifyResult = VerifyImage(device, image, options.Offset);
                    if (verifyResult != ExitCodes.Success)
                        return verifyResult;
                }

                if (options.Read)
                    ReadFlash(device, options);

                if (options.BootGiven)
                    ApplyBootSource(device, options);

                // Lock before security: the security bit denies the flash controller any further
                // command, so a lock sequenced after it would be dropped.
                if (options.Lock && !options.Write)
                {
                    if (lockList == null)
                    {
                        device.SetLockRegions(true);
                        Console.WriteLine("All regions locked.");
                    }
                    else
                    {
                        device.SetLockRegions(lockList, true);
                        Console.WriteLine("Regions " + FormatRegionList(lockList) + " locked.");
                    }
                }

                if (options.Security && !options.Write)
                {
                    device.SetSecurity();
                    Console.WriteLine("Security bit set.");
                }

                if (options.Info)
                    PrintDeviceInfo(device);

                if (options.Reset)
                {
                    device.Reset();
                    Console.WriteLine("CPU reset.");
                }
            }

            return ExitCodes.Success;
        }

        /// <summary>
        /// Opens nothing yet: returns the device for the requested port, or the first device
        /// auto-scan finds. Auto-scan is VID/PID-filtered in the Windows build and unfiltered
        /// (every serial port on the machine) in the portable build.
        /// </summary>
        private static SamBaDevice CreateDevice(CommandLineOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.Port))
                return new SamBaDevice(NormalizePortPath(options.Port));

#if SAMBA_WIN32
            IReadOnlyList<SamBaDevice> found = SamBaDeviceDiscovery.Enumerate();
            if (found.Count == 0)
            {
                throw new ArgumentException("Auto scan found no SAM-BA device. Specify a port with --" +
                    OptionNames.Port + ".");
            }

            SamBaDevice selected = found[0];
            for (int i = 1; i < found.Count; i++)
                found[i].Dispose();

            string port = selected.PortName.Length != 0 ? selected.PortName : selected.DevicePath;
            Console.WriteLine("Device found on " + port);
            return selected;
#else
            string[] names = SerialPort.GetPortNames();
            if (names.Length == 0)
            {
                throw new ArgumentException("Auto scan found no SAM-BA device. Specify a port with --" +
                    OptionNames.Port + ".");
            }

            string port = names[0];
            if (names.Length > 1)
            {
                Console.WriteLine("Multiple serial ports found (unfiltered on this platform): " +
                    string.Join(", ", names) + ".");
                Console.WriteLine("Trying " + port + " first. Specify --" + OptionNames.Port +
                    " to pick another.");
            }
            else
            {
                Console.WriteLine("Trying serial port " + port + " (unfiltered on this platform). " +
                    "Specify --" + OptionNames.Port + " to pick another.");
            }
            return new SamBaDevice(port);
#endif
        }

        /// <summary>
        /// A bare port name like "ttyACM0" gets its device directory prefixed, as serial tools
        /// conventionally allow; Windows COM names and full paths pass through untouched.
        /// </summary>
        private static string NormalizePortPath(string port)
        {
            port = port.Trim();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !port.StartsWith("/", StringComparison.Ordinal))
            {
                return "/dev/" + port;
            }

            return port;
        }

        private static void EraseAll(SamBaDevice device)
        {
            var stopwatch = Stopwatch.StartNew();
            device.EraseAllFlash();
            stopwatch.Stop();

            Console.WriteLine("Erase complete in " + FormatSeconds(stopwatch) + ".");
        }

        private static void WriteImage(SamBaDevice device, byte[] image, CommandLineOptions options,
            int[] unlockList, int[] lockList)
        {
            if (unlockList != null)
            {
                device.SetLockRegions(unlockList, false);
                Console.WriteLine("Regions " + FormatRegionList(unlockList) + " unlocked.");
            }

            bool lockAfterWrite = options.Lock || lockList != null;

            var updateOptions = new SamBaUpdateOptions
            {
                BulkErase = options.Erase,
                Verify = options.Verify,
                UnlockBeforeWrite = options.Unlock && unlockList == null,
                Offset = options.Offset,
                SetBootToFlash = options.BootGiven && options.BootToFlash,
                // Every lock runs below instead of inside the update, because this tool's lock option
                // means the regions the user named and the whole flash when bare, while the update's
                // own Written scope means only the regions its image covers. The security bit has to
                // follow the lock (it cuts off further controller commands), so it leaves the options too.
                Lock = FlashLockScope.None,
                SetSecurity = options.Security && !lockAfterWrite
                    ? SecurityAction.SetPermanently
                    : SecurityAction.Leave,
                // Reset stays a separate final step in Run so it also runs without a write.
                Reset = false,
            };

            var stopwatch = Stopwatch.StartNew();
            device.UpdateFirmware(image, updateOptions);
            stopwatch.Stop();

            Console.WriteLine("Write complete: " + image.Length + " bytes in " +
                FormatSeconds(stopwatch) + ".");

            if (lockList != null)
            {
                device.SetLockRegions(lockList, true);
                Console.WriteLine("Regions " + FormatRegionList(lockList) + " locked.");
            }
            else if (options.Lock)
            {
                device.SetLockRegions(true);
                Console.WriteLine("All regions locked.");
            }

            if (lockAfterWrite && options.Security)
            {
                device.SetSecurity();
                Console.WriteLine("Security bit set.");
            }
        }

        private static int VerifyImage(SamBaDevice device, byte[] image, uint offset)
        {
            SamBaChipInfo chip = device.ChipInfo;

            if (offset + (long)image.Length > chip.FlashSize)
            {
                Console.Error.WriteLine("ERROR: the image does not fit: offset 0x" +
                    offset.ToString("X") + " + " + image.Length + " bytes exceeds the " +
                    chip.FlashSize + " byte flash.");
                return ExitCodes.InvalidArgument;
            }

            byte[] actual = device.ReadMemory(chip.FlashAddress + offset, image.Length);

            for (int i = 0; i < image.Length; i++)
            {
                if (actual[i] == image[i])
                    continue;

                Console.Error.WriteLine("ERROR: verify failed at flash offset 0x" +
                    (offset + (uint)i).ToString("X") + ": expected 0x" + image[i].ToString("X2") +
                    ", read 0x" + actual[i].ToString("X2") + ".");
                return ExitCodes.DeviceError;
            }

            Console.WriteLine("Verify successful: " + image.Length + " bytes match.");
            return ExitCodes.Success;
        }

        private static void ReadFlash(SamBaDevice device, CommandLineOptions options)
        {
            SamBaChipInfo chip = device.ChipInfo;

            if (options.Offset >= chip.FlashSize)
            {
                throw new ArgumentException("--" + OptionNames.Offset + " 0x" +
                    options.Offset.ToString("X") + " is beyond the " + chip.FlashSize + " byte flash.");
            }

            long size = options.ReadSize ?? chip.FlashSize - options.Offset;

            // Like reading a file, a read past the end stops at the end rather than failing.
            if (options.Offset + size > chip.FlashSize)
                size = chip.FlashSize - options.Offset;

            byte[] data = device.ReadMemory(chip.FlashAddress + options.Offset, (int)size);
            File.WriteAllBytes(options.FilePath, data);

            Console.WriteLine("Read " + data.Length + " bytes from flash offset 0x" +
                options.Offset.ToString("X") + " into " + options.FilePath + ".");
        }

        private static void ApplyBootSource(SamBaDevice device, CommandLineOptions options)
        {
            // A write already pointed the part at flash through the update options.
            if (options.Write && options.BootToFlash)
                return;

            device.SetBootSource(options.BootToFlash ? SamBaChipBootSource.Flash : SamBaChipBootSource.Rom);
            Console.WriteLine("Boot source set to " + (options.BootToFlash ? "flash." : "ROM."));
        }

        private static void PrintDeviceInfo(SamBaDevice device)
        {
            SamBaChipInfo chip = device.ChipInfo;

            Console.WriteLine();
            Console.WriteLine("Device       : " + chip.Name);
            Console.WriteLine("Version      : " + device.MonitorVersion);
            Console.WriteLine("Address      : 0x" + chip.FlashAddress.ToString("X8"));
            Console.WriteLine("Pages        : " + chip.PageCount);
            Console.WriteLine("Page Size    : " + chip.PageSize + " bytes");
            Console.WriteLine("Total Size   : " + (chip.FlashSize / 1024) + " KB");
            Console.WriteLine("Planes       : " + chip.PlaneCount);
            Console.WriteLine("Lock Regions : " + chip.LockRegionCount);
            Console.WriteLine("Locked       : " +
                DescribeOrFallback(() => FormatLockedRegions(device.GetLockRegions())));
            Console.WriteLine("Security     : " +
                DescribeOrFallback(() => device.GetSecurity() ? "true" : "false"));
            Console.WriteLine("Boot Flash   : " + DescribeOrFallback(() => DescribeBootSource(device)));

            if (chip.HasUniqueId)
            {
                Console.WriteLine("Unique Id    : " +
                    DescribeOrFallback(() => FormatUniqueId(device.GetUniqueId())));
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// One info field failing to read should not take the rest of the block with it — the
        /// failure becomes the field's value instead.
        /// </summary>
        private static string DescribeOrFallback(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (SamBaException ex)
            {
                return "(unavailable: " + ex.Message + ")";
            }
        }

        private static string DescribeBootSource(SamBaDevice device)
        {
            if (!device.ChipInfo.CanSelectBootSource)
                return "true (fixed)";

            return device.GetBootSource() == SamBaChipBootSource.Flash ? "true" : "false";
        }

        /// <summary>
        /// Parses a comma-separated decimal region list; null or empty (the bare option form)
        /// yields null, meaning all regions. Anything other than plain digits between the commas
        /// is refused with the offending piece named, rather than read as far as it parses.
        /// </summary>
        private static int[] ParseRegionListOrNull(string list, string optionName)
        {
            if (string.IsNullOrEmpty(list))
                return null;

            string[] pieces = list.Split(',');
            var regions = new int[pieces.Length];

            for (int i = 0; i < pieces.Length; i++)
            {
                if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture,
                    out regions[i]))
                {
                    throw new FormatException("Invalid region list for --" + optionName + ": '" +
                        list + "' - '" + pieces[i] + "' is not a region number.");
                }
            }

            return regions;
        }

        private static string FormatRegionList(int[] regions) => string.Join(", ", regions);

        /// <summary>Locked region indexes compressed into ranges: "none", "0-3, 8, 12-15".</summary>
        private static string FormatLockedRegions(IReadOnlyList<bool> regions)
        {
            var builder = new StringBuilder();
            int i = 0;

            while (i < regions.Count)
            {
                if (!regions[i])
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < regions.Count && regions[i])
                    i++;

                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(start);
                if (i - 1 > start)
                    builder.Append('-').Append(i - 1);
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private static string FormatUniqueId(IReadOnlyList<uint> words)
        {
            if (words.Count == 0)
                return "(none)";

            var builder = new StringBuilder(words.Count * 9);
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');
                builder.Append(words[i].ToString("X8"));
            }

            return builder.ToString();
        }

        private static string FormatSeconds(Stopwatch stopwatch)
        {
            double seconds = stopwatch.ElapsedMilliseconds / 1000.0;
            return seconds.ToString("0.000", CultureInfo.InvariantCulture) + " seconds";
        }

        #endregion

        #region Help

        private static void PrintVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version.ToString(3);
            Console.WriteLine($"{appName} v{ver}");
        }

        private static string Tag(char shortName, string longName, string valueSuffix = "")
        {
            return "-" + shortName + ", --" + longName + valueSuffix;
        }

        private static void PrintOption(string tag, string description)
        {
            Console.WriteLine("  " + tag.PadRight(24) + description);
        }

        private static void PrintHelp()
        {
            Console.WriteLine(appName + " - Atmel/Microchip SAM-BA flashing utility (USB CDC)\n");
            Console.WriteLine("Usage: " + appName + " [OPTION...] [FILE]\n");

            Console.WriteLine("Options:");
            PrintOption(Tag(ShortNames.Erase, OptionNames.Erase),
                "erase the whole flash (with --" + OptionNames.Write + ": erase before programming)");
            PrintOption(Tag(ShortNames.Write, OptionNames.Write),
                "write FILE to flash");
            PrintOption(Tag(ShortNames.Read, OptionNames.Read, "[=SIZE]"),
                "read SIZE bytes of flash into FILE (default: the entire flash)");
            PrintOption(Tag(ShortNames.Verify, OptionNames.Verify),
                "verify FILE against flash (with --" + OptionNames.Write + ": verify after programming)");
            PrintOption(Tag(ShortNames.Offset, OptionNames.Offset, "=OFFSET"),
                "flash byte offset for write/read/verify (default: 0)");
            PrintOption(Tag(ShortNames.Port, OptionNames.Port, "=PORT"),
                "port to use, e.g. COM7 or /dev/ttyACM0 (default: auto-scan; see below)");
            PrintOption(Tag(ShortNames.Boot, OptionNames.Boot, "[=BOOL]"),
                "boot from flash if BOOL is 1 (the default), from ROM if 0");
            PrintOption("    --" + OptionNames.Identify + "=MODE",
                "chip identification probe: auto (default), chipid or cpuid - see README");
            PrintOption("    --" + OptionNames.GeometryPrecedence + "=WHICH",
                "which geometry wins on a table/device disagreement: table (default) or device");
            PrintOption(Tag(ShortNames.Lock, OptionNames.Lock, "[=LIST]"),
                "lock the comma-separated region LIST, e.g. 0,1,2 (all regions when bare)");
            PrintOption(Tag(ShortNames.Unlock, OptionNames.Unlock, "[=LIST]"),
                "unlock the comma-separated region LIST (all regions when bare)");
            PrintOption(Tag(ShortNames.Security, OptionNames.Security),
                "set the security bit (IRREVERSIBLE: blocks further SAM-BA access)");
            PrintOption(Tag(ShortNames.Info, OptionNames.Info),
                "display device information");
            PrintOption(Tag(ShortNames.Reset, OptionNames.Reset),
                "reset the CPU after the other operations");
            PrintOption(Tag(ShortNames.Help, OptionNames.Help),
                "display this help text");
            PrintOption("    --" + OptionNames.Version,
                "display version information");
            Console.WriteLine();

            Console.WriteLine("Accepted for compatibility but not supported (each fails with an error):");
            PrintOption(Tag(ShortNames.Bod, OptionNames.Bod, "[=BOOL]"),
                "brown-out detect configuration");
            PrintOption(Tag(ShortNames.Bor, OptionNames.Bor, "[=BOOL]"),
                "brown-out reset configuration");
            PrintOption(Tag(ShortNames.Debug, OptionNames.Debug),
                "protocol trace output");
            PrintOption(Tag(ShortNames.UsbPort, OptionNames.UsbPort, "[=BOOL]"),
                "0 selects RS-232, which this tool cannot drive (1, USB, is the default)");
            PrintOption(Tag(ShortNames.ArduinoErase, OptionNames.ArduinoErase),
                "erase and reset via the 1200 baud touch");
            Console.WriteLine();

            Console.WriteLine("Without --" + OptionNames.Port + ", the Windows .NET Framework build " +
                "auto-scans for a SAM-BA USB CDC port;");
            Console.WriteLine("the cross-platform .NET build has no discovery and requires --" +
                OptionNames.Port + ".");
            Console.WriteLine();

            Console.WriteLine("Examples:");
            Console.WriteLine("  " + appName + " -p COM7 -i");
            Console.WriteLine("  " + appName + " -e -w -v -b firmware.bin");
            Console.WriteLine("  " + appName + " -p /dev/ttyACM0 -r 8192 dump.bin");
            Console.WriteLine("  " + appName + " -p COM7 -u -e -w -v -R firmware.bin");
        }

        #endregion
    }
}

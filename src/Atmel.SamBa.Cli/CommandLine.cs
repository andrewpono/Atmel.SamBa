using System;
using System.Globalization;


namespace Anp.Atmel.SamBa.Cli
{
    /// <summary>
    /// Long option names, shared by the parser, the validation messages and the help text so a
    /// rename cannot leave the three disagreeing.
    /// </summary>
    internal static class OptionNames
    {
        public const string Erase = "erase";
        public const string Write = "write";
        public const string Read = "read";
        public const string Verify = "verify";
        public const string Offset = "offset";
        public const string Port = "port";
        public const string Boot = "boot";
        public const string Identify = "identify";
        public const string Bod = "bod";
        public const string Bor = "bor";
        public const string Lock = "lock";
        public const string Unlock = "unlock";
        public const string Security = "security";
        public const string Info = "info";
        public const string Debug = "debug";
        public const string UsbPort = "usb-port";
        public const string Reset = "reset";
        public const string ArduinoErase = "arduino-erase";
        public const string Help = "help";
        public const string Version = "version";
        public const string GeometryPrecedence = "geometry-precedence";
    }

    /// <summary>Single-letter option names. Case matters: -r reads, -R resets.</summary>
    internal static class ShortNames
    {
        public const char Erase = 'e';
        public const char Write = 'w';
        public const char Read = 'r';
        public const char Verify = 'v';
        public const char Offset = 'o';
        public const char Port = 'p';
        public const char Boot = 'b';
        public const char Bod = 'c';
        public const char Bor = 't';
        public const char Lock = 'l';
        public const char Unlock = 'u';
        public const char Security = 's';
        public const char Info = 'i';
        public const char Debug = 'd';
        public const char UsbPort = 'U';
        public const char Reset = 'R';
        public const char ArduinoErase = 'a';
        public const char Help = 'h';
    }

    /// <summary>
    /// Everything the command line asked for, already typed; <see cref="Parse"/> is the only
    /// producer. The grammar is the conventional single-dash/double-dash one: '--name',
    /// '--name=value', '-x', '-xVALUE', a detached value token after an option that requires one,
    /// and a bare token as the FILE argument. An option with an optional value takes a detached
    /// token only when it reads as a value of the right shape, so a following FILE argument is
    /// never swallowed. Option bundling ('-ew') is not supported. Numbers accept decimal or
    /// 0x-prefixed hexadecimal; booleans accept true/false/1/0.
    /// </summary>
    internal sealed class CommandLineOptions
    {
        private CommandLineOptions()
        {
        }

        public bool Erase { get; private set; }

        public bool Write { get; private set; }

        public bool Read { get; private set; }

        /// <summary>Byte count for the read operation; null reads to the end of flash.</summary>
        public uint? ReadSize { get; private set; }

        public bool Verify { get; private set; }

        /// <summary>Flash byte offset for write/read/verify. Zero unless given.</summary>
        public uint Offset { get; private set; }

        public string Port { get; private set; }

        /// <summary>True when the boot option appeared at all; the value is in
        /// <see cref="BootToFlash"/>.</summary>
        public bool BootGiven { get; private set; }

        /// <summary>True selects flash (the default when the option carries no value), false the ROM.</summary>
        public bool BootToFlash { get; private set; } = true;

        /// <summary>Which probe <see cref="SamBaDevice.Open"/> takes at the legacy-CHIPID-versus-CPUID
        /// branch. Auto (the default) reads the reset vector, as if the option had not been given.</summary>
        public SamBaChipIdentificationMode IdentificationMode { get; private set; } =
            SamBaChipIdentificationMode.Auto;

        /// <summary>Which geometry wins when the table and the device disagree. Table (the default)
        /// keeps the datasheet-sourced row, as if the option had not been given.</summary>
        public SamBaGeometryPrecedence GeometryPrecedence { get; private set; } =
            SamBaGeometryPrecedence.Table;

        public bool Bod { get; private set; }

        public bool Bor { get; private set; }

        public bool Lock { get; private set; }

        /// <summary>Raw region list given to the lock option; null when the bare form was used.</summary>
        public string LockRegions { get; private set; }

        public bool Unlock { get; private set; }

        /// <summary>Raw region list given to the unlock option; null when the bare form was used.</summary>
        public string UnlockRegions { get; private set; }

        public bool Security { get; private set; }

        public bool Info { get; private set; }

        public bool Debug { get; private set; }

        /// <summary>True when the usb-port option appeared; the value is in <see cref="UsbPortIsUsb"/>.</summary>
        public bool UsbPortGiven { get; private set; }

        /// <summary>True selects USB (the default when the option carries no value), false RS-232.</summary>
        public bool UsbPortIsUsb { get; private set; } = true;

        public bool Reset { get; private set; }

        public bool ArduinoErase { get; private set; }

        public bool Help { get; private set; }

        public bool Version { get; private set; }

        /// <summary>The one positional argument, or null. Read/write/verify name their file here.</summary>
        public string FilePath { get; private set; }

        private enum OptionArity
        {
            None = 0,
            Required = 1,
            Optional = 2,
        }

        private sealed class OptionSpec
        {
            /// <summary>'\0' when the option has no short form.</summary>
            public readonly char ShortName;

            public readonly string LongName;

            public readonly OptionArity Arity;

            /// <summary>Optional arity only: whether a detached token reads as this option's value.</summary>
            public readonly Func<string, bool> AcceptsDetached;

            /// <summary>Applies the option; the value is null when none was given.</summary>
            public readonly Action<CommandLineOptions, string> Apply;

            public OptionSpec(char shortName, string longName, OptionArity arity,
                Func<string, bool> acceptsDetached, Action<CommandLineOptions, string> apply)
            {
                ShortName = shortName;
                LongName = longName;
                Arity = arity;
                AcceptsDetached = acceptsDetached;
                Apply = apply;
            }
        }

        private static readonly OptionSpec[] specs =
        {
            new OptionSpec(ShortNames.Erase, OptionNames.Erase, OptionArity.None, null,
                (o, v) => o.Erase = true),
            new OptionSpec(ShortNames.Write, OptionNames.Write, OptionArity.None, null,
                (o, v) => o.Write = true),
            new OptionSpec(ShortNames.Read, OptionNames.Read, OptionArity.Optional, IsUInt32Token,
                (o, v) =>
                {
                    o.Read = true;
                    o.ReadSize = v == null ? (uint?)null : ParseUInt32(v, OptionNames.Read);
                }),
            new OptionSpec(ShortNames.Verify, OptionNames.Verify, OptionArity.None, null,
                (o, v) => o.Verify = true),
            new OptionSpec(ShortNames.Offset, OptionNames.Offset, OptionArity.Required, null,
                (o, v) => o.Offset = ParseUInt32(v, OptionNames.Offset)),
            new OptionSpec(ShortNames.Port, OptionNames.Port, OptionArity.Required, null,
                (o, v) => o.Port = v),
            new OptionSpec(ShortNames.Boot, OptionNames.Boot, OptionArity.Optional, IsBoolToken,
                (o, v) =>
                {
                    o.BootGiven = true;
                    o.BootToFlash = v == null || ParseBool(v, OptionNames.Boot);
                }),
            new OptionSpec('\0', OptionNames.Identify, OptionArity.Required, null,
                (o, v) => o.IdentificationMode = ParseIdentificationMode(v, OptionNames.Identify)),
            new OptionSpec('\0', OptionNames.GeometryPrecedence, OptionArity.Required, null,
                (o, v) => o.GeometryPrecedence =
                    ParseGeometryPrecedence(v, OptionNames.GeometryPrecedence)),
            new OptionSpec(ShortNames.Bod, OptionNames.Bod, OptionArity.Optional, IsBoolToken,
                (o, v) => o.Bod = true),
            new OptionSpec(ShortNames.Bor, OptionNames.Bor, OptionArity.Optional, IsBoolToken,
                (o, v) => o.Bor = true),
            new OptionSpec(ShortNames.Lock, OptionNames.Lock, OptionArity.Optional, IsRegionListToken,
                (o, v) =>
                {
                    o.Lock = true;
                    o.LockRegions = v;
                }),
            new OptionSpec(ShortNames.Unlock, OptionNames.Unlock, OptionArity.Optional, IsRegionListToken,
                (o, v) =>
                {
                    o.Unlock = true;
                    o.UnlockRegions = v;
                }),
            new OptionSpec(ShortNames.Security, OptionNames.Security, OptionArity.None, null,
                (o, v) => o.Security = true),
            new OptionSpec(ShortNames.Info, OptionNames.Info, OptionArity.None, null,
                (o, v) => o.Info = true),
            new OptionSpec(ShortNames.Debug, OptionNames.Debug, OptionArity.None, null,
                (o, v) => o.Debug = true),
            new OptionSpec(ShortNames.UsbPort, OptionNames.UsbPort, OptionArity.Optional, IsBoolToken,
                (o, v) =>
                {
                    o.UsbPortGiven = true;
                    o.UsbPortIsUsb = v == null || ParseBool(v, OptionNames.UsbPort);
                }),
            new OptionSpec(ShortNames.Reset, OptionNames.Reset, OptionArity.None, null,
                (o, v) => o.Reset = true),
            new OptionSpec(ShortNames.ArduinoErase, OptionNames.ArduinoErase, OptionArity.None, null,
                (o, v) => o.ArduinoErase = true),
            new OptionSpec(ShortNames.Help, OptionNames.Help, OptionArity.None, null,
                (o, v) => o.Help = true),
            new OptionSpec('\0', OptionNames.Version, OptionArity.None, null,
                (o, v) => o.Version = true),
        };

        /// <summary>
        /// Parses the raw arguments. Throws <see cref="ArgumentException"/> for an unknown option,
        /// a missing or superfluous value, or a second FILE argument, and
        /// <see cref="FormatException"/> for a value that does not parse — both are reported as a
        /// usage error by the caller.
        /// </summary>
        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();
            if (args == null)
                return options;

            bool literalOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i] ?? string.Empty;
                if (token.Length == 0)
                    continue;

                if (!literalOnly && token == "--")
                {
                    literalOnly = true;
                    continue;
                }

                if (!literalOnly && token.Length > 1 && token[0] == '-')
                {
                    i = ApplyOption(options, args, i);
                    continue;
                }

                if (options.FilePath != null)
                    throw new ArgumentException("Only one FILE argument is allowed: " + token);
                options.FilePath = token;
            }

            return options;
        }

        /// <summary>
        /// Applies the option token at <paramref name="index"/> and returns the index of the last
        /// token it consumed (the same index, or one further for a detached value).
        /// </summary>
        private static int ApplyOption(CommandLineOptions options, string[] args, int index)
        {
            string token = args[index];
            OptionSpec spec;
            string inlineValue;
            string display;

            if (token[1] == '-')
            {
                int eq = token.IndexOf('=');
                string name = eq >= 0 ? token.Substring(2, eq - 2) : token.Substring(2);
                inlineValue = eq >= 0 ? token.Substring(eq + 1) : null;
                display = "--" + name;

                spec = FindLong(name);
                if (spec == null)
                    throw new ArgumentException("Unknown option: " + display);
            }
            else
            {
                char name = token[1];
                display = "-" + name;

                spec = FindShort(name);
                if (spec == null)
                    throw new ArgumentException("Unknown option: " + display);

                string rest = token.Substring(2);
                if (rest.Length == 0)
                    inlineValue = null;
                else if (spec.Arity == OptionArity.None)
                    throw new ArgumentException(
                        "Unexpected text after " + display + " (option bundling is not supported): " + token);
                else
                    inlineValue = rest;
            }

            switch (spec.Arity)
            {
                case OptionArity.None:
                    if (inlineValue != null)
                        throw new ArgumentException("Option " + display + " does not take a value.");
                    spec.Apply(options, null);
                    return index;

                case OptionArity.Required:
                    if (inlineValue != null)
                    {
                        spec.Apply(options, inlineValue);
                        return index;
                    }
                    if (index + 1 >= args.Length)
                        throw new ArgumentException("Missing value for option " + display + ".");
                    spec.Apply(options, args[index + 1]);
                    return index + 1;

                default:
                    if (inlineValue != null)
                    {
                        spec.Apply(options, inlineValue);
                        return index;
                    }
                    if (index + 1 < args.Length && spec.AcceptsDetached(args[index + 1]))
                    {
                        spec.Apply(options, args[index + 1]);
                        return index + 1;
                    }
                    spec.Apply(options, null);
                    return index;
            }
        }

        private static OptionSpec FindLong(string name)
        {
            foreach (OptionSpec spec in specs)
            {
                if (string.Equals(spec.LongName, name, StringComparison.Ordinal))
                    return spec;
            }
            return null;
        }

        private static OptionSpec FindShort(char name)
        {
            foreach (OptionSpec spec in specs)
            {
                if (spec.ShortName == name)
                    return spec;
            }
            return null;
        }

        private static bool IsBoolToken(string token)
        {
            return token != null && ParseBoolOrNull(token).HasValue;
        }

        private static bool IsUInt32Token(string token)
        {
            if (token == null)
                return false;

            try
            {
                ParseUInt32(token, string.Empty);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>A comma-separated list of region numbers — the shape a region value takes.</summary>
        private static bool IsRegionListToken(string token)
        {
            if (string.IsNullOrEmpty(token) || !char.IsDigit(token[0]))
                return false;

            for (int i = 0; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i]) && token[i] != ',')
                    return false;
            }
            return true;
        }

        private static bool ParseBool(string s, string keyName)
        {
            bool? value = ParseBoolOrNull(s);
            if (value.HasValue)
                return value.Value;

            throw new FormatException("Invalid boolean value for --" + keyName + ": " + s +
                " (use true/false or 1/0)");
        }

        private static SamBaChipIdentificationMode ParseIdentificationMode(string s, string keyName)
        {
            s = s.Trim();

            if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase))
                return SamBaChipIdentificationMode.Auto;
            if (string.Equals(s, "chipid", StringComparison.OrdinalIgnoreCase))
                return SamBaChipIdentificationMode.ChipId;
            if (string.Equals(s, "cpuid", StringComparison.OrdinalIgnoreCase))
                return SamBaChipIdentificationMode.CpuId;

            throw new FormatException("Invalid value for --" + keyName + ": " + s +
                " (use auto, chipid or cpuid)");
        }

        private static SamBaGeometryPrecedence ParseGeometryPrecedence(string s, string keyName)
        {
            s = s.Trim();

            if (string.Equals(s, "table", StringComparison.OrdinalIgnoreCase))
                return SamBaGeometryPrecedence.Table;
            if (string.Equals(s, "device", StringComparison.OrdinalIgnoreCase))
                return SamBaGeometryPrecedence.Device;

            throw new FormatException("Invalid value for --" + keyName + ": " + s +
                " (use table or device)");
        }

        private static bool? ParseBoolOrNull(string s)
        {
            s = s.Trim();

            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "1", StringComparison.Ordinal))
                return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "0", StringComparison.Ordinal))
                return false;

            return null;
        }

        private static uint ParseUInt32(string s, string keyName)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));

            s = s.Trim();

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hexDigits = s.Substring(2);
                if (uint.TryParse(hexDigits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                    return v;
                throw new FormatException("Invalid hex value for --" + keyName + ": " + s);
            }

            if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint result))
                return result;

            throw new FormatException("Invalid numeric value for --" + keyName + ": " + s +
                " (use 0x prefix for hexadecimal)");
        }
    }
}

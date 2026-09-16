using Anp.Atmel.SamBa.Configuration;
using Anp.Serial.Win32;
using System;
using System.Collections.Generic;


namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Finds the serial ports a SAM-BA device could be behind, filtered by USB VID/PID —
    /// <see cref="SamBaDevice.DefaultVendorId"/> / <see cref="SamBaDevice.DefaultProductId"/> by
    /// default, the Atmel SAM-BA USB CDC function.
    /// <para>
    /// Ports are matched, never probed: whether one answers as a SAM-BA monitor is known only after
    /// <see cref="SamBaDevice.Open"/>. With both filters left null every COM port on the machine
    /// matches, most of them nothing to do with SAM-BA — see <see cref="SamBaDevice.DisplayName"/>,
    /// which names such a port after its PnP identity rather than calling it a SAM-BA device.
    /// </para>
    /// </summary>
    public static class SamBaDeviceDiscovery
    {
        // Guards the shared defaults, and nothing else. Enumeration itself is deliberately not
        // serialized and does not need to be: the path query underneath keeps nothing between calls
        // (a per-call buffer and list, one read-only separator) and CfgMgr32's device-interface list
        // is a stateless size-then-fetch that retries itself when the list changes mid-query.
        private static readonly object _gate = new object();
        private static SamBaDiscoveryOptions _defaults = new SamBaDiscoveryOptions();

        /// <summary>
        /// Adjusts the shared default discovery options used by <see cref="Enumerate"/> (when
        /// called without options) and by <see cref="SamBaDeviceWatcher"/>. Thread-safe: the update
        /// is applied to a copy and swapped in atomically, so a concurrent reader sees either all of
        /// it or none of it, and if <paramref name="configure"/> throws the defaults are left as
        /// they were.
        /// <para>
        /// <paramref name="configure"/> runs while this class's lock is held — held on purpose, so
        /// that two threads configuring at once cannot lose one another's edits — so it must do
        /// nothing but set properties on the options it is handed. Anything slow in it, and anything
        /// that blocks, stalls every other caller of this class and of <see cref="Enumerate"/> until
        /// it returns. It cannot deadlock against <see cref="SamBaDeviceWatcher"/>, though:
        /// <see cref="SamBaDeviceWatcher.Start"/> reads these defaults before taking its own lock,
        /// deliberately, so no thread ever holds that lock while waiting for this one and the two can
        /// never be taken in opposite orders.
        /// </para>
        /// </summary>
        /// <param name="configure">Receives a copy of the current defaults to modify.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
        public static void ConfigureDefaults(Action<SamBaDiscoveryOptions> configure)
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            lock (_gate)
            {
                SamBaDiscoveryOptions candidate = _defaults.Clone();
                configure(candidate);
                _defaults = candidate;
            }
        }

        /// <summary>
        /// Enumerates matching serial ports and returns one unopened <see cref="SamBaDevice"/>
        /// per port. The caller owns and should dispose the returned devices. Note that a
        /// matching port is not probed — whether it answers as a SAM-BA monitor is only
        /// known after <see cref="SamBaDevice.Open"/>.
        /// <para>
        /// Not free per port, though: constructing each device reads that port's PnP metadata — port
        /// name, friendly name, VID/PID — from the registry, so an unfiltered call on a machine with a
        /// dozen COM ports does a dozen registry lookups before it returns.
        /// </para>
        /// </summary>
        /// <param name="options">
        /// Discovery options; null uses the shared defaults. Copied on entry, so a caller may keep
        /// one instance and edit it between calls without an edit from another thread tearing the
        /// filter this call applies.
        /// </param>
        /// <returns>
        /// One device per matching port, in enumeration order. Empty when nothing matches — and also
        /// when the enumeration itself failed, which cannot be told apart here: the serial layer
        /// underneath answers a failed device-interface query with no paths and reports the reason
        /// through <see cref="Anp.Serial.Win32.Diagnostics.SerialDiag.Error"/>, which is where a
        /// caller that needs to distinguish the two has to listen.
        /// </returns>
        public static IReadOnlyList<SamBaDevice> Enumerate(SamBaDiscoveryOptions options = null)
        {
            SamBaDiscoveryOptions opts = options != null ? options.Clone() : SnapshotDefaults();

            IReadOnlyList<string> paths = SerialDiscovery.GetDevicePaths(opts.VendorId, opts.ProductId);
            var devices = new List<SamBaDevice>(paths.Count);
            foreach (string path in paths)
                devices.Add(new SamBaDevice(path));

            return devices.AsReadOnly();
        }

        /// <summary>
        /// The shared defaults as a detached copy, for <see cref="Enumerate"/> when it is given no
        /// options and for <see cref="SamBaDeviceWatcher.Start"/>. Detached so that a caller holding
        /// one can read or edit it without touching what the next caller sees.
        /// </summary>
        internal static SamBaDiscoveryOptions SnapshotDefaults()
        {
            lock (_gate)
            {
                return _defaults.Clone();
            }
        }
    }
}

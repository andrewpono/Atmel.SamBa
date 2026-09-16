using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using Anp.Serial.Win32;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;


namespace Anp.Atmel.SamBa
{
    /// <summary>
    /// Watches for SAM-BA device arrival and removal using OS PnP notifications
    /// (CM_Register_Notification via <c>Anp.Serial.Win32.SerialWatcher</c> — event-driven,
    /// no polling). Requires Windows 8 or later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The VID/PID filter comes from <see cref="SamBaDeviceDiscovery"/>'s shared defaults, read once
    /// when <see cref="Start"/> is called and held for as long as this watcher runs. A later
    /// <see cref="SamBaDeviceDiscovery.ConfigureDefaults"/> call therefore does not reach a watcher
    /// that is already running, and until it is stopped and started again the two disagree:
    /// <see cref="SamBaDeviceDiscovery.Enumerate"/> matches the new filter while this watcher goes on
    /// matching the one it started with. Expect that to show up as a port enumeration lists and no
    /// arrival event ever mentions, or as arrivals for ports enumeration has stopped returning — so
    /// restart the watcher whenever the defaults change.
    /// </para>
    /// <para>
    /// Events are raised on thread-pool threads — marshal to your UI thread as needed.
    /// </para>
    /// <para>
    /// A notification already under way can reach a handler after <see cref="Stop"/> has returned:
    /// stopping detaches this watcher from the port notifications, but a raise that had already begun
    /// runs to completion on its own thread. A subscriber that tears its state down in
    /// <see cref="Stop"/> has to tolerate one late event — including between the two halves of the
    /// stop/start pair a filter change needs.
    /// </para>
    /// <para>
    /// Every subscriber receives an event even if an earlier one throws; the failure surfaces through
    /// <see cref="Anp.Serial.Win32.Diagnostics.SerialDiag.Error"/> rather than reaching the caller,
    /// which is on a thread-pool thread with nobody to catch it.
    /// </para>
    /// <para>
    /// A handler may stop or dispose this watcher from inside itself — a UI that stops at the first
    /// device found, say. Events are raised on a thread-pool thread rather than on the OS callback
    /// thread, so unregistering from a handler does not re-enter the notification that produced it.
    /// </para>
    /// </remarks>
    public sealed class SamBaDeviceWatcher : IDisposable
    {
        private readonly object _sync = new object();
        private SerialWatcher _watcher;

        // Read and written under _sync only, which is what makes it safe without being volatile —
        // and why ThrowIfDisposed must only ever be called with the lock already held.
        private bool _disposed;

        /// <summary>
        /// Raised when a matching device arrives. Supplies an unopened <see cref="SamBaDevice"/>; see
        /// <see cref="SamBaDeviceArrivedEventArgs.Device"/> for who is expected to dispose it.
        /// </summary>
        public event EventHandler<SamBaDeviceArrivedEventArgs> DeviceArrived;

        /// <summary>Raised when a matching device is removed. Supplies the device path.</summary>
        public event EventHandler<SamBaDeviceRemovedEventArgs> DeviceRemoved;

        /// <summary>
        /// True while watching; false before <see cref="Start"/>, and after <see cref="Stop"/> or
        /// <see cref="Dispose"/>.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _watcher != null;
                }
            }
        }

        /// <summary>
        /// Starts watching, on the VID/PID filter <see cref="SamBaDeviceDiscovery"/>'s defaults hold
        /// at this moment and no later reading of them — see the class remarks for what a filter
        /// change during a run does and does not affect. No-op when already running.
        /// </summary>
        /// <exception cref="System.PlatformNotSupportedException">Windows 8 or later is required.</exception>
        /// <exception cref="System.ComponentModel.Win32Exception">
        /// Registering for PnP notifications failed. This watcher is left not running, with the failed
        /// registration torn down.
        /// </exception>
        /// <exception cref="ObjectDisposedException">This watcher has been disposed.</exception>
        public void Start()
        {
            // Read before the lock is taken, not inside it. Holding _sync while reaching for
            // discovery's own lock would be one half of a lock-order inversion — the other half being
            // a ConfigureDefaults callback, which runs under that lock, stopping a watcher — and
            // reading first makes the cycle impossible instead of merely unlikely. The cost is one
            // copy wasted when the call turns out to be a no-op.
            SamBaDiscoveryOptions options = SamBaDeviceDiscovery.SnapshotDefaults();

            lock (_sync)
            {
                ThrowIfDisposed();
                if (_watcher != null)
                    return;

                var watcher = new SerialWatcher(options.VendorId, options.ProductId);
                watcher.DeviceArrived += OnSerialDeviceArrived;
                watcher.DeviceRemoved += OnSerialDeviceRemoved;
                try
                {
                    watcher.StartWatching();
                }
                catch
                {
                    watcher.DeviceArrived -= OnSerialDeviceArrived;
                    watcher.DeviceRemoved -= OnSerialDeviceRemoved;
                    watcher.Dispose();
                    throw;
                }

                _watcher = watcher;
            }
        }

        /// <summary>
        /// Stops watching. No-op when not running, and safe after <see cref="Dispose"/> — unlike the
        /// underlying serial watcher, which throws once disposed.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                if (_watcher == null)
                    return;

                _watcher.DeviceArrived -= OnSerialDeviceArrived;
                _watcher.DeviceRemoved -= OnSerialDeviceRemoved;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        /// <summary>Stops watching and releases resources. Safe to call more than once.</summary>
        public void Dispose()
        {
            // The flag is set under the lock, and both halves of that matter. A Start racing this
            // call then reads the new value under the same lock, so it cannot slip past
            // ThrowIfDisposed and register a watcher that this Dispose has already gone past — one
            // that nothing would ever unregister, leaving notifications arriving at a disposed
            // object for the life of the process. Checking and setting together also makes a
            // concurrent second Dispose a no-op rather than a second teardown.
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            Stop();
        }

        private void OnSerialDeviceArrived(object sender, SerialDeviceEventArgs e)
        {
            EventHandler<SamBaDeviceArrivedEventArgs> handler = DeviceArrived;
            if (handler == null)
                return;

            RaiseToEverySubscriber(handler, this, new SamBaDeviceArrivedEventArgs(new SamBaDevice(e.DevicePath)));
        }

        private void OnSerialDeviceRemoved(object sender, SerialDeviceEventArgs e)
        {
            EventHandler<SamBaDeviceRemovedEventArgs> handler = DeviceRemoved;
            if (handler == null)
                return;

            RaiseToEverySubscriber(handler, this, new SamBaDeviceRemovedEventArgs(e.DevicePath));
        }

        /// <summary>
        /// Invokes every subscriber even if one throws, then lets a failure out. Both halves are
        /// deliberate. The serial watcher underneath raises through a helper that isolates subscribers
        /// from one another, so invoking this event's delegate directly would give that up — the first
        /// handler to throw would hide the device from every handler after it. Swallowing instead
        /// would be worse: this library has no diagnostics channel of its own, so the exception would
        /// disappear, whereas letting one out returns it to that helper, which reports it through
        /// <see cref="Anp.Serial.Win32.Diagnostics.SerialDiag.Error"/> and carries on.
        /// <para>
        /// Which is why this belongs to the watcher despite its shape, and is not the general-purpose
        /// event raiser it looks like. The rethrow means something only because of who catches it: a
        /// raise from <see cref="SamBaDevice"/> runs on the caller's own thread with nobody behind it,
        /// so the same helper there would either abort the caller's operation over a logging bug or
        /// lose the exception entirely. That is the choice <c>SamBaDevice.ReportGeometryMismatch</c>
        /// faces and settles differently, and the reason it contains failures instead of forwarding
        /// them here.
        /// </para>
        /// </summary>
        internal static void RaiseToEverySubscriber<T>(EventHandler<T> handler, object sender, T args)
        {
            List<Exception> failures = null;
            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<T>)subscriber)(sender, args);
                }
                catch (Exception ex)
                {
                    if (failures == null)
                        failures = new List<Exception>();
                    failures.Add(ex);
                }
            }

            if (failures == null)
                return;

            // One failure is rethrown as itself, stack trace intact, because that is the report
            // someone has to debug; several can only travel together.
            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();

            throw new AggregateException($"{failures.Count} event subscribers threw", failures);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SamBaDeviceWatcher));
        }
    }
}

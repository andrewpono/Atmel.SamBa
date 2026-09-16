using System;
using System.Windows;
using System.Windows.Threading;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Behaviors
{
    /// <summary>
    /// Adjusts a Window's Height by one or more deltas whenever the bound IsAdvancedMode value toggles.
    ///
    /// This exists because XAML triggers can't do arithmetic (Height +/- Delta). The behavior is configured
    /// purely from XAML using attached properties.
    ///
    /// Convention used by this app (configurable):
    /// - The Window's XAML Height is treated as the baseline size.
    /// - BaselineIsAdvanced (default true) indicates whether that baseline corresponds to Advanced mode.
    /// - On startup (Loaded), if the initial IsAdvancedMode differs from the baseline mode, we apply the
    ///   appropriate delta once so the window opens at the correct size.
    ///
    /// Notes:
    /// - You can specify direction-specific deltas via ExpandDelta (Basic->Advanced) and CollapseDelta (Advanced->Basic).
    ///   If either is not set (NaN), Delta is used as a fallback.
    /// - If ExpandDelta != CollapseDelta, toggling Advanced on/off will not be "round-trip" neutral and the
    ///   height will drift by (ExpandDelta - CollapseDelta) per full cycle. This is intentional/allowed.
    /// </summary>
    public static class WindowHeightDeltaBehavior
    {
        static WindowHeightDeltaBehavior()
        {
            // IMPORTANT: OnIsAdvancedModeChanged only fires when the effective DP value changes.
            // If the bound VM property starts out equal to the DP default (e.g., false), the change
            // callback may never run at startup and we'd miss the initial size correction.
            //
            // This class handler ensures we always get a chance to apply the initial size once the
            // Window is loaded (but only for Windows that actually use this behavior).
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyWindowLoaded),
                handledEventsToo: true);
        }

        public static readonly DependencyProperty IsAdvancedModeProperty = DependencyProperty.RegisterAttached(
            "IsAdvancedMode",
            typeof(bool),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(false, OnIsAdvancedModeChanged));

        /// <summary>
        /// Declares whether the Window's XAML Height should be treated as the Advanced baseline (true) or
        /// the Basic baseline (false).
        ///
        /// This is necessary so the behavior can correctly size the window at startup, even when the initial
        /// IsAdvancedMode value equals the DP default and no change callback fires.
        /// </summary>
        public static readonly DependencyProperty BaselineIsAdvancedProperty = DependencyProperty.RegisterAttached(
            "BaselineIsAdvanced",
            typeof(bool),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(true));

        /// <summary>
        /// If true, clamps the resulting window height (and adjusts Top as needed) so the window stays
        /// within the current monitor's work area (screen area excluding taskbar/docked bars).
        ///
        /// This is useful when expanding to Advanced mode on small screens.
        /// </summary>
        public static readonly DependencyProperty ClampToWorkAreaProperty = DependencyProperty.RegisterAttached(
            "ClampToWorkArea",
            typeof(bool),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(false));

        public static bool GetClampToWorkArea(DependencyObject obj)
        {
            return (bool)obj.GetValue(ClampToWorkAreaProperty);
        }

        public static void SetClampToWorkArea(DependencyObject obj, bool value)
        {
            obj.SetValue(ClampToWorkAreaProperty, value);
        }

        public static bool GetBaselineIsAdvanced(DependencyObject obj)
        {
            return (bool)obj.GetValue(BaselineIsAdvancedProperty);
        }

        public static void SetBaselineIsAdvanced(DependencyObject obj, bool value)
        {
            obj.SetValue(BaselineIsAdvancedProperty, value);
        }

        public static bool GetIsAdvancedMode(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsAdvancedModeProperty);
        }

        public static void SetIsAdvancedMode(DependencyObject obj, bool value)
        {
            obj.SetValue(IsAdvancedModeProperty, value);
        }

        public static readonly DependencyProperty DeltaProperty = DependencyProperty.RegisterAttached(
            "Delta",
            typeof(double),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(240d));

        /// <summary>
        /// Optional delta used when switching from Basic -> Advanced.
        /// If NaN, falls back to Delta.
        /// </summary>
        public static readonly DependencyProperty ExpandDeltaProperty = DependencyProperty.RegisterAttached(
            "ExpandDelta",
            typeof(double),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(double.NaN));

        public static double GetExpandDelta(DependencyObject obj)
        {
            return (double)obj.GetValue(ExpandDeltaProperty);
        }

        public static void SetExpandDelta(DependencyObject obj, double value)
        {
            obj.SetValue(ExpandDeltaProperty, value);
        }

        /// <summary>
        /// Optional delta used when switching from Advanced -> Basic.
        /// If NaN, falls back to Delta.
        /// </summary>
        public static readonly DependencyProperty CollapseDeltaProperty = DependencyProperty.RegisterAttached(
            "CollapseDelta",
            typeof(double),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(double.NaN));

        public static double GetCollapseDelta(DependencyObject obj)
        {
            return (double)obj.GetValue(CollapseDeltaProperty);
        }

        public static void SetCollapseDelta(DependencyObject obj, double value)
        {
            obj.SetValue(CollapseDeltaProperty, value);
        }

        public static double GetDelta(DependencyObject obj)
        {
            return (double)obj.GetValue(DeltaProperty);
        }

        public static void SetDelta(DependencyObject obj, double value)
        {
            obj.SetValue(DeltaProperty, value);
        }

        private static readonly DependencyProperty HasAppliedStartupProperty = DependencyProperty.RegisterAttached(
            "HasAppliedStartup",
            typeof(bool),
            typeof(WindowHeightDeltaBehavior),
            new PropertyMetadata(false));

        private static bool GetHasAppliedStartup(DependencyObject obj)
        {
            return (bool)obj.GetValue(HasAppliedStartupProperty);
        }

        private static void SetHasAppliedStartup(DependencyObject obj, bool value)
        {
            obj.SetValue(HasAppliedStartupProperty, value);
        }

        private static void OnIsAdvancedModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var window = d as Window;
            if (window == null)
                return;

            // If already loaded, respond immediately to toggles.
            if (window.IsLoaded)
            {
                // Important: if callers drive Delta/ExpandDelta/CollapseDelta via style triggers,
                // the attached delta property may update *after* IsAdvancedMode. Defer to let the
                // dependency property system / triggers settle.
                var oldValue = (bool)e.OldValue;
                var newValue = (bool)e.NewValue;
                window.Dispatcher.BeginInvoke(
                    new Action(() => ApplyDelta(window, oldValue, newValue)),
                    DispatcherPriority.Background);
            }
        }

        private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            var window = sender as Window;
            if (window == null)
                return;

            // Only act for windows that actually use this behavior.
            // We consider the behavior active when IsAdvancedMode is provided via local value, style, or binding.
            var source = DependencyPropertyHelper.GetValueSource(window, IsAdvancedModeProperty);
            if (source.BaseValueSource == BaseValueSource.Default)
                return;

            if (GetHasAppliedStartup(window))
                return;

            SetHasAppliedStartup(window, true);

            // Defer so ActualHeight is valid and any style triggers have applied.
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                // If maximized, changing Height is ineffective; ignore.
                if (window.WindowState == WindowState.Maximized)
                    return;

                var desiredAdvanced = GetIsAdvancedMode(window);
                var baselineIsAdvanced = GetBaselineIsAdvanced(window);

                var currentHeight = GetCurrentHeight(window);
                if (currentHeight <= 0)
                    return;

                // Start from current size; apply delta only if baseline differs from initial state.
                var targetHeight = currentHeight;

                if (desiredAdvanced != baselineIsAdvanced)
                {
                    if (desiredAdvanced)
                    {
                        var delta = GetExpandDeltaEffective(window);
                        if (delta > 0)
                            targetHeight = currentHeight + delta;
                    }
                    else
                    {
                        var delta = GetCollapseDeltaEffective(window);
                        if (delta > 0)
                            targetHeight = currentHeight - delta;
                    }
                }

                // Clamp to MinHeight / MaxHeight if set.
                targetHeight = ClampToWindowMinMax(window, targetHeight);

                var currentTop = window.Top;
                var targetTop = currentTop;

                // Always clamp to work area if enabled, even if no delta was needed.
                if (GetClampToWorkArea(window))
                    ClampToWorkArea(window, ref targetHeight, ref targetTop);

                if (!DoubleUtil.AreClose(currentHeight, targetHeight))
                    window.SetCurrentValue(FrameworkElement.HeightProperty, targetHeight);

                if (!DoubleUtil.AreClose(currentTop, targetTop))
                    window.SetCurrentValue(Window.TopProperty, targetTop);
            }), DispatcherPriority.Loaded);
        }

        private static void ApplyDelta(Window window, bool oldValue, bool newValue)
        {
            if (oldValue == newValue)
                return;

            var expandDelta = GetExpandDeltaEffective(window);
            var collapseDelta = GetCollapseDeltaEffective(window);

            // If maximized, changing Height is ineffective; ignore.
            if (window.WindowState == WindowState.Maximized)
                return;

            var currentHeight = GetCurrentHeight(window);
            double targetHeight;

            if (newValue)
            {
                if (expandDelta <= 0)
                    return;
                targetHeight = currentHeight + expandDelta;
            }
            else
            {
                if (collapseDelta <= 0)
                    return;
                targetHeight = currentHeight - collapseDelta;
            }

            // Clamp to MinHeight / MaxHeight if set.
            targetHeight = ClampToWindowMinMax(window, targetHeight);

            var currentTop = window.Top;
            var targetTop = currentTop;

            if (GetClampToWorkArea(window))
                ClampToWorkArea(window, ref targetHeight, ref targetTop);

            if (!DoubleUtil.AreClose(currentHeight, targetHeight))
                window.SetCurrentValue(FrameworkElement.HeightProperty, targetHeight);

            if (!DoubleUtil.AreClose(currentTop, targetTop))
                window.SetCurrentValue(Window.TopProperty, targetTop);
        }

        private static double ClampToWindowMinMax(Window window, double height)
        {
            // MinHeight
            if (!double.IsNaN(window.MinHeight) && window.MinHeight > 0)
                height = Math.Max(window.MinHeight, height);

            // MaxHeight (WPF default is +Infinity)
            if (!double.IsNaN(window.MaxHeight) && !double.IsInfinity(window.MaxHeight) && window.MaxHeight > 0)
                height = Math.Min(window.MaxHeight, height);

            return height;
        }

        private static void ClampToWorkArea(Window window, ref double targetHeight, ref double targetTop)
        {
            var workArea = GetWorkArea(window);

            // Clamp height to work area.
            if (targetHeight > workArea.Height)
                targetHeight = workArea.Height;

            // Keep the window fully inside the work area by adjusting Top.
            var minTop = workArea.Top;
            var maxTop = workArea.Bottom - targetHeight;
            if (maxTop < minTop)
                maxTop = minTop;

            if (double.IsNaN(targetTop) || double.IsInfinity(targetTop))
                targetTop = minTop;

            if (targetTop < minTop)
                targetTop = minTop;
            else if (targetTop > maxTop)
                targetTop = maxTop;
        }

        private static Rect GetWorkArea(Window window)
        {
            //// Best effort: use the monitor the window is on.
            //// If we can't resolve the screen or transforms, fall back to SystemParameters.WorkArea.
            //try
            //{
            //    var hwnd = new WindowInteropHelper(window).Handle;
            //    if (hwnd == IntPtr.Zero)
            //        return SystemParameters.WorkArea;

            //    var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            //    var waPx = screen.WorkingArea; // pixels

            //    var source = PresentationSource.FromVisual(window);
            //    var ct = source?.CompositionTarget;
            //    if (ct != null)
            //    {
            //        // Convert device pixels -> DIPs
            //        Matrix fromDevice = ct.TransformFromDevice;
            //        var tl = fromDevice.Transform(new Point(waPx.Left, waPx.Top));
            //        var br = fromDevice.Transform(new Point(waPx.Right, waPx.Bottom));
            //        return new Rect(tl, br);
            //    }

            //    // Fallback: assume 1:1 scaling
            //    return new Rect(waPx.Left, waPx.Top, waPx.Width, waPx.Height);
            //}
            //catch
            //{
            //    return SystemParameters.WorkArea;
            //}

            // DIPs, primary monitor only.
            // For actual monitor need to use Forms or Win32.
            // Good enough for now.
            return SystemParameters.WorkArea;
        }

        private static double GetExpandDeltaEffective(Window window)
        {
            var v = GetExpandDelta(window);
            if (!double.IsNaN(v))
                return v;
            return GetDelta(window);
        }

        private static double GetCollapseDeltaEffective(Window window)
        {
            var v = GetCollapseDelta(window);
            if (!double.IsNaN(v))
                return v;
            return GetDelta(window);
        }

        private static double GetCurrentHeight(Window window)
        {
            // ActualHeight is most accurate once the window is rendered.
            var h = window.ActualHeight;
            if (!double.IsNaN(h) && h > 0)
                return h;

            h = window.Height;
            if (!double.IsNaN(h) && h > 0)
                return h;

            return 0;
        }

        /// <summary>
        /// Utility to compare doubles with a small tolerance.
        /// We keep this local to avoid pulling in PresentationFramework internals.
        /// </summary>
        private static class DoubleUtil
        {
            private const double Epsilon = 0.5; // half DIP is good enough for window sizing

            public static bool AreClose(double a, double b)
            {
                if (double.IsNaN(a) && double.IsNaN(b))
                    return true;
                return Math.Abs(a - b) < Epsilon;
            }
        }
    }
}

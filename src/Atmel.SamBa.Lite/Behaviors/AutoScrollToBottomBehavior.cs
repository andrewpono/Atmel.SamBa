using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;


namespace Anp.Atmel.SamBa.Lite.Behaviors
{
    /// <summary>
    /// Attached behavior that automatically scrolls a ListBox to its last item whenever
    /// the items collection changes.
    ///
    /// Behavior:
    /// - On initial load / when becoming visible, always scroll to the end.
    /// - On subsequent collection changes, only scroll if the user is already at (or near) the bottom.
    ///
    /// Usage:
    ///   beh:AutoScrollToBottomBehavior.IsEnabled="True"
    /// </summary>
    public static class AutoScrollToBottomBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(AutoScrollToBottomBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject element)
            => (bool)element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject element, bool value)
            => element.SetValue(IsEnabledProperty, value);

        private static readonly DependencyProperty CollectionChangedHandlerProperty =
            DependencyProperty.RegisterAttached(
                "CollectionChangedHandler",
                typeof(NotifyCollectionChangedEventHandler),
                typeof(AutoScrollToBottomBehavior),
                new PropertyMetadata(null));

        private static NotifyCollectionChangedEventHandler GetCollectionChangedHandler(DependencyObject element)
            => (NotifyCollectionChangedEventHandler)element.GetValue(CollectionChangedHandlerProperty);

        private static void SetCollectionChangedHandler(DependencyObject element, NotifyCollectionChangedEventHandler value)
            => element.SetValue(CollectionChangedHandlerProperty, value);

        private static readonly DependencyProperty CachedScrollViewerProperty =
            DependencyProperty.RegisterAttached(
                "CachedScrollViewer",
                typeof(ScrollViewer),
                typeof(AutoScrollToBottomBehavior),
                new PropertyMetadata(null));

        private static ScrollViewer GetCachedScrollViewer(DependencyObject element)
            => (ScrollViewer)element.GetValue(CachedScrollViewerProperty);

        private static void SetCachedScrollViewer(DependencyObject element, ScrollViewer value)
            => element.SetValue(CachedScrollViewerProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ListBox listBox))
                return;

            if (e.OldValue is bool oldEnabled && oldEnabled)
                Detach(listBox);

            if (e.NewValue is bool newEnabled && newEnabled)
                Attach(listBox);
        }

        private static void Attach(ListBox listBox)
        {
            listBox.Loaded += OnLoaded;
            listBox.IsVisibleChanged += OnIsVisibleChanged;

            if (listBox.Items is INotifyCollectionChanged notify)
            {
                NotifyCollectionChangedEventHandler handler = (s, e) => ScrollToEnd(listBox, force: false);
                SetCollectionChangedHandler(listBox, handler);
                notify.CollectionChanged += handler;
            }

            // In case there are already items at attach time.
            ScrollToEnd(listBox, force: true);
        }

        private static void Detach(ListBox listBox)
        {
            listBox.Loaded -= OnLoaded;
            listBox.IsVisibleChanged -= OnIsVisibleChanged;

            if (listBox.Items is INotifyCollectionChanged notify)
            {
                var handler = GetCollectionChangedHandler(listBox);
                if (handler != null)
                    notify.CollectionChanged -= handler;
            }

            SetCollectionChangedHandler(listBox, null);
            SetCachedScrollViewer(listBox, null);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            ScrollToEnd(sender as ListBox, force: true);
        }

        private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible)
                ScrollToEnd(sender as ListBox, force: true);
        }

        private static void ScrollToEnd(ListBox listBox, bool force)
        {
            if (listBox == null)
                return;

            if (!listBox.IsLoaded || !listBox.IsVisible)
                return;

            // Respect user scrolling: if not forced, only scroll when already at (or near) bottom.
            if (!force && !IsAtBottom(listBox))
                return;

            var count = listBox.Items.Count;
            if (count <= 0)
                return;

            var last = listBox.Items[count - 1];

            // Defer until WPF has updated layout / generated item containers.
            listBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    listBox.ScrollIntoView(last);
                }
                catch
                {
                    // Ignore occasional layout race issues; the next collection change will retry.
                }
            }), DispatcherPriority.Background);
        }

        private static bool IsAtBottom(ListBox listBox)
        {
            // We treat "unknown" as at-bottom to avoid disabling auto-scroll due to template timing.
            var sv = GetCachedScrollViewer(listBox) ?? FindAndCacheScrollViewer(listBox);
            if (sv == null)
                return true;

            if (sv.ScrollableHeight <= 0)
                return true;

            const double tolerance = 1.0; // DIPs
            return sv.VerticalOffset >= (sv.ScrollableHeight - tolerance);
        }

        private static ScrollViewer FindAndCacheScrollViewer(ListBox listBox)
        {
            if (listBox == null)
                return null;

            var sv = FindVisualChild<ScrollViewer>(listBox);
            if (sv != null)
                SetCachedScrollViewer(listBox, sv);

            return sv;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild)
                    return tChild;

                var nested = FindVisualChild<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }
    }
}

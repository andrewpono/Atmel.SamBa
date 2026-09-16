using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


namespace Anp.Atmel.SamBa.Lite.Behaviors
{
    public static class DeselectOnEmptyClickBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DeselectOnEmptyClickBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj)
            => (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value)
            => obj.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ListBox listBox))
                return;

            if ((bool)e.OldValue)
                listBox.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;

            if ((bool)e.NewValue)
                listBox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListBox listBox))
                return;

            var hit = listBox.InputHitTest(e.GetPosition(listBox)) as DependencyObject;
            while (hit != null && hit != listBox)
            {
                if (hit is ListBoxItem)
                    return;
                hit = VisualTreeHelper.GetParent(hit);
            }

            listBox.UnselectAll();
        }
    }
}

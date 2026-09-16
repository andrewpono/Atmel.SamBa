using Anp.Atmel.SamBa.FirmwareUpdater.Utilities;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Behaviors
{
    public static class HexInputBehavior
    {
        private static readonly Regex HexRegex = new Regex("^[0-9a-fA-F]+$", RegexOptions.Compiled);

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(HexInputBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject element, bool value)
        {
            element.SetValue(IsEnabledProperty, value);
        }

        public static bool GetIsEnabled(DependencyObject element)
        {
            return (bool)element.GetValue(IsEnabledProperty);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBox tb))
                return;

            if ((bool)e.OldValue)
            {
                tb.PreviewTextInput -= OnPreviewTextInput;
                DataObject.RemovePastingHandler(tb, OnPaste);
                tb.TextChanged -= OnTextChanged;
            }

            if ((bool)e.NewValue)
            {
                tb.PreviewTextInput += OnPreviewTextInput;
                DataObject.AddPastingHandler(tb, OnPaste);
                tb.TextChanged += OnTextChanged;
            }
        }

        private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !HexRegex.IsMatch(e.Text);
        }

        private static void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
            {
                e.CancelCommand();
                return;
            }

            var text = (e.SourceDataObject.GetData(DataFormats.UnicodeText) as string) ?? string.Empty;
            text = HexUtility.Normalize(text);
            if (!string.IsNullOrEmpty(text) && !HexRegex.IsMatch(text))
                e.CancelCommand();
        }

        private static void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!(sender is TextBox tb))
                return;

            var normalized = HexUtility.Normalize(tb.Text);
            if (normalized == tb.Text)
                return;

            var sel = tb.SelectionStart;
            tb.Text = normalized;
            tb.SelectionStart = Math.Min(sel, tb.Text.Length);
        }
    }
}

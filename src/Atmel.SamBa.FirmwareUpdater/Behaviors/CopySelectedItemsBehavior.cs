using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Behaviors
{
    public static class CopySelectedItemsBehavior
    {
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.RegisterAttached(
                "Command",
                typeof(ICommand),
                typeof(CopySelectedItemsBehavior),
                new PropertyMetadata(null, OnCommandChanged));

        public static ICommand GetCommand(DependencyObject obj)
            => (ICommand)obj.GetValue(CommandProperty);

        public static void SetCommand(DependencyObject obj, ICommand value)
            => obj.SetValue(CommandProperty, value);

        private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ListBox listBox))
                return;

            if (e.OldValue != null)
                listBox.PreviewKeyDown -= OnPreviewKeyDown;

            if (e.NewValue != null)
                listBox.PreviewKeyDown += OnPreviewKeyDown;
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control)
                return;

            if (!(sender is ListBox listBox))
                return;

            var cmd = GetCommand(listBox);
            if (cmd != null && cmd.CanExecute(listBox.SelectedItems))
            {
                cmd.Execute(listBox.SelectedItems);
                e.Handled = true;
            }
        }
    }
}

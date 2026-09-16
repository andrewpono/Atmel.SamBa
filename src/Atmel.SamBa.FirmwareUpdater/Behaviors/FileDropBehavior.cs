using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Behaviors
{
    public static class FileDropBehavior
    {
        public static readonly DependencyProperty FileDropCommandProperty =
            DependencyProperty.RegisterAttached(
                "FileDropCommand",
                typeof(ICommand),
                typeof(FileDropBehavior),
                new PropertyMetadata(null, OnFileDropCommandChanged));

        public static void SetFileDropCommand(DependencyObject element, ICommand value)
        {
            element.SetValue(FileDropCommandProperty, value);
        }

        public static ICommand GetFileDropCommand(DependencyObject element)
        {
            return (ICommand)element.GetValue(FileDropCommandProperty);
        }

        private static void OnFileDropCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
                return;

            if (e.OldValue != null)
            {
                element.PreviewDragOver -= OnPreviewDragOver;
                element.Drop -= OnDrop;
            }

            if (e.NewValue != null)
            {
                element.PreviewDragOver += OnPreviewDragOver;
                element.Drop += OnDrop;
            }
        }

        private static void OnPreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private static void OnDrop(object sender, DragEventArgs e)
        {
            if (!(sender is DependencyObject d))
                return;

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var first = files?.FirstOrDefault(f => File.Exists(f));
            if (string.IsNullOrWhiteSpace(first))
                return;

            var cmd = GetFileDropCommand(d);
            if (cmd == null)
                return;

            if (cmd.CanExecute(first))
                cmd.Execute(first);
        }
    }
}

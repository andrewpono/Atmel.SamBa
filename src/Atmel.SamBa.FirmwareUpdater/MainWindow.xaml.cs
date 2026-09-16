using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace Anp.Atmel.SamBa.FirmwareUpdater
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // The VID/PID boxes live inside a checkable menu item (selecting the Custom filter).
        // On the first click, focus the box for editing without letting the click bubble up and
        // toggle the parent item; once focused, clicks pass through for caret placement.
        private void HexBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            {
                tb.Focus();
                e.Handled = true;
            }
        }
    }
}

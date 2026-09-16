using Microsoft.Win32;
using System.Windows;


namespace Anp.Atmel.SamBa.Lite.Services
{
    public sealed class UserDialogService : IUserDialogService
    {
        public string BrowseFirmwareFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select firmware file",
                Filter = "Firmware files|*.bin|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public string BrowseOpenBinaryFile(string title)
        {
            var dlg = new OpenFileDialog
            {
                Title = title ?? "Select binary file",
                Filter = "Binary files|*.bin|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public string BrowseSaveLogFile(string suggestedFileName)
        {
            return BrowseSaveFile("Save log", "Text files|*.txt|All files|*.*", suggestedFileName);
        }

        public string BrowseSaveTextFile(string suggestedFileName)
        {
            return BrowseSaveFile(
                "Save data", "Text files|*.txt|CSV files|*.csv|All files|*.*", suggestedFileName);
        }

        public string BrowseSaveBinaryFile(string suggestedFileName)
        {
            return BrowseSaveFile("Save binary", "Binary files|*.bin|All files|*.*", suggestedFileName);
        }

        public bool Confirm(string title, string message)
        {
            return MessageBox.Show(
                message ?? string.Empty,
                title ?? "Confirm",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        public void ShowInfo(string title, string message)
        {
            MessageBox.Show(
                message ?? string.Empty,
                title ?? "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ShowError(string title, string message)
        {
            MessageBox.Show(
                message ?? string.Empty,
                title ?? "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private string BrowseSaveFile(string title, string filter, string suggestedFileName)
        {
            var dlg = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = suggestedFileName ?? ""
            };

            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
    }
}

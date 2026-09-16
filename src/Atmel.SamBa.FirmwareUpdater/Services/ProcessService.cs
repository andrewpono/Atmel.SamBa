using System.Diagnostics;
using System.IO;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Services
{
    public sealed class ProcessService : IProcessService
    {
        public void OpenFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            var info = new ProcessStartInfo("explorer.exe", "\"" + folderPath + "\"");
            try { Process.Start(info); } catch { /* ignore */ }
        }

        public void OpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); } catch { /* ignore */ }
        }
    }
}

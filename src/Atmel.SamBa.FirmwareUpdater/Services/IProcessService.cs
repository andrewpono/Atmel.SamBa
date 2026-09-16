

namespace Anp.Atmel.SamBa.FirmwareUpdater.Services
{
    public interface IProcessService
    {
        void OpenFolder(string folderPath);

        void OpenFile(string filePath);
    }
}

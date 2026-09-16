using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.FirmwareUpdater.Models;
using Anp.Atmel.SamBa.FirmwareUpdater.Services;
using Anp.Atmel.SamBa.FirmwareUpdater.Utilities;
using MvvmHelpers;
using MvvmHelpers.Commands;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;


namespace Anp.Atmel.SamBa.FirmwareUpdater.ViewModels
{
    public sealed class MainViewModel : BaseViewModel, IDisposable, IDataErrorInfo
    {
        private readonly string _appVersion;
        private readonly string _libraryVersion;
        private readonly bool _isDesignMode;
        private readonly ISettingsService _settingsService;
        private readonly IUserDialogService _dialogService;
        private readonly IToolWindowService _toolWindowService;
        private readonly IDispatcherService _dispatcher;
        private readonly IProcessService _processService;
        private readonly IClipboardService _clipboardService;

        private SamBaDeviceWatcher _watcher;
        private readonly SemaphoreSlim _deviceLock = new SemaphoreSlim(1, 1);

        // Log entries are enqueued from any thread and drained to the UI
        // in batches by a DispatcherTimer, avoiding per-entry dispatches.
        private const int LogFlushIntervalMs = 100;
        private readonly ConcurrentQueue<string> _pendingLogEntries = new ConcurrentQueue<string>();
        private DispatcherTimer _logFlushTimer;
        private volatile int _logTimerRunningOrRequested;

        // Parameterless constructor required for design-time support
        public MainViewModel()
            : this("",
                  "",
                  true,
                  new SettingsService(),
                  new UserDialogService(),
                  new ToolWindowService(new DispatcherService()),
                  new DispatcherService(),
                  new ProcessService(),
                  new ClipboardService())
        {
        }

        public MainViewModel(
            string appVersion,
            string libraryVersion,
            bool isDesignMode,
            ISettingsService settingsService,
            IUserDialogService dialogService,
            IToolWindowService toolWindowService,
            IDispatcherService dispatcher,
            IProcessService processService,
            IClipboardService clipboardService)
        {
            _appVersion = appVersion ?? "0.0.0";
            _libraryVersion = libraryVersion ?? "0.0.0";
            _isDesignMode = isDesignMode;
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _toolWindowService = toolWindowService ?? throw new ArgumentNullException(nameof(toolWindowService));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _processService = processService ?? throw new ArgumentNullException(nameof(processService));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));

            // ObservableRangeCollection is problematic for range operations when bound to UI elements.
            DeviceList = new ObservableRangeCollection<SamBaDevice>();
            LogEntries = new BatchObservableCollection<string>();

            OpenFirmwareCommand = new Command(ExecuteOpenFirmware, CanInteract);
            SaveLogCommand = new Command(ExecuteSaveLog, CanInteract);
            OpenSettingsLocationCommand = new Command(ExecuteOpenSettingsLocation);

            RefreshDevicesCommand = new AsyncCommand(RefreshDevicesAsync, (_) => CanInteract());
            ShowDevicePropertiesCommand = new Command(ExecuteShowDeviceProperties, () => SelectedDevice != null);
            ShowSupportedChipsCommand = new Command(ExecuteShowSupportedChips);
            ShowAboutCommand = new Command(ExecuteShowAbout);

            EraseCommand = new AsyncCommand(ExecuteEraseAsync, (_) => CanOperateOnDevice());
            ResetCommand = new AsyncCommand(ExecuteResetAsync, (_) => CanOperateOnDevice());
            ShowChipInfoCommand = new AsyncCommand(ExecuteShowChipInfoAsync, (_) => CanOperateOnDevice());
            UpdateCommand = new AsyncCommand(ExecuteUpdateAsync, (_) => CanOperateOnDevice());
            UnlockCommand = new AsyncCommand(ExecuteUnlockAsync, (_) => CanOperateOnDevice());

            FirmwareFileDroppedCommand = new Command<string>(ExecuteFirmwareFileDropped);

            ClearLogCommand = new Command(ExecuteClearLog);
            CopySelectedLogCommand = new Command<IList>(ExecuteCopyLog, CanCopySelectedLog);

            if (!_isDesignMode)
                LoadSettings();
        }

        #region Observable collections
        public ObservableRangeCollection<SamBaDevice> DeviceList { get; }

        // MvvmHelpers' ObservableRangeCollection AddRange doesn't work when bound to a UI.
        // Use a custom BatchObservableCollection that raises a single Reset notification for batches.
        public BatchObservableCollection<string> LogEntries { get; }
        #endregion Observable collections

        #region Bindable properties
        public string WindowTitle => $"SAM-BA Firmware Updater v{_appVersion}";

        private SamBaDevice _selectedDevice;
        public SamBaDevice SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    ShowDevicePropertiesCommand.RaiseCanExecuteChanged();
                    RaiseDeviceCanExecuteChanged();
                }
            }
        }

        private string _firmwareFilePath;
        public string FirmwareFilePath
        {
            get => _firmwareFilePath;
            set => SetProperty(ref _firmwareFilePath, value);
        }

        private bool _isAdvancedMode;
        public bool IsAdvancedMode
        {
            get => _isAdvancedMode;
            set => SetProperty(ref _isAdvancedMode, value);
        }

        private bool _isWatcherEnabled;
        public bool IsWatcherEnabled
        {
            get => _isWatcherEnabled;
            set => SetProperty(ref _isWatcherEnabled, value, onChanged: () => ToggleWatcher(value));
        }

        // Device-discovery filter. AtmelSamBa (default) lists only Atmel SAM-BA (VID 0x03EB /
        // PID 0x6124); AnyDevice clears the filter so every serial port is listed (how Arduino /
        // custom SAM boards under their own VID/PID become reachable); Custom filters by the
        // user-supplied VID/PID below. Applied to the shared discovery defaults via
        // ApplyDeviceFilter(); the watcher is restarted so it re-reads the filter (captured
        // only at Start()).
        private DeviceFilterMode _deviceFilterMode = DeviceFilterMode.AtmelSamBa;
        public DeviceFilterMode DeviceFilterMode
        {
            get => _deviceFilterMode;
            set => SetProperty(ref _deviceFilterMode, value, onChanged: OnDeviceFilterChanged);
        }

        // The three menu items act as a radio group. Each is a settable bool over the enum:
        // setting true selects that mode; setting false (clicking the active item) is ignored
        // but re-raises so the checkmark snaps back.
        public bool UseAtmelFilter
        {
            get => DeviceFilterMode == DeviceFilterMode.AtmelSamBa;
            set { if (value) DeviceFilterMode = DeviceFilterMode.AtmelSamBa; else OnPropertyChanged(); }
        }

        public bool MatchAnyDevice
        {
            get => DeviceFilterMode == DeviceFilterMode.AnyDevice;
            set { if (value) DeviceFilterMode = DeviceFilterMode.AnyDevice; else OnPropertyChanged(); }
        }

        public bool UseCustomFilter
        {
            get => DeviceFilterMode == DeviceFilterMode.Custom;
            set { if (value) DeviceFilterMode = DeviceFilterMode.Custom; else OnPropertyChanged(); }
        }

        // Custom USB VID/PID filter (hex, up to 4 digits). A blank/invalid field matches any,
        // so the user can filter by VID only, PID only, or both. Only used when the filter mode
        // is Custom. Edits commit on focus-loss and re-apply the filter when Custom is active.
        private string _customVendorIdHex = string.Empty;
        public string CustomVendorIdHex
        {
            get => _customVendorIdHex;
            set => SetProperty(ref _customVendorIdHex, value, onChanged: OnCustomFilterValueChanged);
        }

        private string _customProductIdHex = string.Empty;
        public string CustomProductIdHex
        {
            get => _customProductIdHex;
            set => SetProperty(ref _customProductIdHex, value, onChanged: OnCustomFilterValueChanged);
        }

        // Safe mode loads the flash latch one word at a time instead of one batched
        // USB transfer per page — slower, but robust with bootloaders that drop data
        // mid-stream. Applied to SamBaDevice.SafeMode before each device operation.
        private bool _isSafeModeEnabled;
        public bool IsSafeModeEnabled
        {
            get => _isSafeModeEnabled;
            set => SetProperty(ref _isSafeModeEnabled, value);
        }

        // Unlock any locked flash regions before erase/write, so programming a locked part
        // doesn't fail with a lock error. Passed to SamBaUpdateOptions.UnlockBeforeWrite on each update.
        private bool _autoUnlock = false;
        public bool AutoUnlock
        {
            get => _autoUnlock;
            set => SetProperty(ref _autoUnlock, value);
        }

        // Which probe SamBaDevice.Open takes at the legacy-CHIPID-versus-CPUID branch. Auto (the
        // default) reads the reset vector; forcing ChipId/CpuId is only safe on a part whose core
        // generation is already known - see SamBaChipIdentificationMode's remarks.
        private SamBaChipIdentificationMode _chipIdentificationMode = SamBaChipIdentificationMode.Auto;
        public SamBaChipIdentificationMode ChipIdentificationMode
        {
            get => _chipIdentificationMode;
            set => SetProperty(ref _chipIdentificationMode, value, onChanged: OnChipIdentificationModeChanged);
        }

        private void OnChipIdentificationModeChanged()
        {
            OnPropertyChanged(nameof(IdentifyAuto));
            OnPropertyChanged(nameof(IdentifyForceChipId));
            OnPropertyChanged(nameof(IdentifyForceCpuId));
        }

        // The three menu items act as a radio group, same pattern as the device-filter items above.
        public bool IdentifyAuto
        {
            get => ChipIdentificationMode == SamBaChipIdentificationMode.Auto;
            set
            {
                if (value) ChipIdentificationMode = SamBaChipIdentificationMode.Auto;
                else OnPropertyChanged();
            }
        }

        public bool IdentifyForceChipId
        {
            get => ChipIdentificationMode == SamBaChipIdentificationMode.ChipId;
            set
            {
                if (value) ChipIdentificationMode = SamBaChipIdentificationMode.ChipId;
                else OnPropertyChanged();
            }
        }

        public bool IdentifyForceCpuId
        {
            get => ChipIdentificationMode == SamBaChipIdentificationMode.CpuId;
            set
            {
                if (value) ChipIdentificationMode = SamBaChipIdentificationMode.CpuId;
                else OnPropertyChanged();
            }
        }

        // Which account wins when the device's reported flash geometry disagrees with the table
        // row. Table (the default) keeps the datasheet-sourced row; see SamBaGeometryPrecedence's
        // remarks.
        private SamBaGeometryPrecedence _geometryPrecedence = SamBaGeometryPrecedence.Table;
        public SamBaGeometryPrecedence GeometryPrecedence
        {
            get => _geometryPrecedence;
            set => SetProperty(ref _geometryPrecedence, value, onChanged: OnGeometryPrecedenceChanged);
        }

        private void OnGeometryPrecedenceChanged()
        {
            OnPropertyChanged(nameof(GeometryPrecedenceTable));
            OnPropertyChanged(nameof(GeometryPrecedenceDevice));
        }

        // The two "Flash Geometry Precedence" menu items act as a radio group, same pattern as the
        // "Chip Identification" items above.
        public bool GeometryPrecedenceTable
        {
            get => GeometryPrecedence == SamBaGeometryPrecedence.Table;
            set
            {
                if (value) GeometryPrecedence = SamBaGeometryPrecedence.Table;
                else OnPropertyChanged();
            }
        }

        public bool GeometryPrecedenceDevice
        {
            get => GeometryPrecedence == SamBaGeometryPrecedence.Device;
            set
            {
                if (value) GeometryPrecedence = SamBaGeometryPrecedence.Device;
                else OnPropertyChanged();
            }
        }

        private int _maxLogEntries = 1000;
        public int MaxLogEntries
        {
            get => _maxLogEntries;
            set => SetProperty(ref _maxLogEntries, value,
                validateValue: (_, v) => v >= 10 && v <= 20000,
                onChanged: () => TrimLog());
        }

        private bool _eraseAllFlash;
        public bool EraseAllFlash
        {
            get => _eraseAllFlash;
            set => SetProperty(ref _eraseAllFlash, value);
        }

        private bool _verifyAfterWrite = true;
        public bool VerifyAfterWrite
        {
            get => _verifyAfterWrite;
            set => SetProperty(ref _verifyAfterWrite, value);
        }

        private bool _resetAfterWrite = true;
        public bool ResetAfterWrite
        {
            get => _resetAfterWrite;
            set => SetProperty(ref _resetAfterWrite, value);
        }

        // Which lock regions to lock after programming. Written covers only the regions the
        // image occupies (not the whole flash); All locks every region; None leaves lock state
        // alone - see FlashLockScope's remarks.
        private FlashLockScope _fwLockScope;
        public FlashLockScope FwLockScope
        {
            get => _fwLockScope;
            set => SetProperty(ref _fwLockScope, value);
        }

        public static IEnumerable<FlashLockScope> LockScopeValues { get; } =
            (FlashLockScope[])Enum.GetValues(typeof(FlashLockScope));

        public const string DefaultOffsetHex = "00000000";

        // Offset into flash at which the image is written. Persisted; default 0.
        private string _fwOffsetHex = DefaultOffsetHex;
        public string FwOffsetHex
        {
            get => _fwOffsetHex;
            set => SetProperty(ref _fwOffsetHex, HexUtility.Normalize(value));
        }

        private bool _fwBootToFlash = true;
        public bool FwBootToFlash
        {
            get => _fwBootToFlash;
            set => SetProperty(ref _fwBootToFlash, value);
        }

        // Not persisted: setting the security bit is dangerous enough that each session must
        // opt in again - see ConfirmSecurity.
        private bool _fwSecurity;
        public bool FwSecurity
        {
            get => _fwSecurity;
            set => SetProperty(ref _fwSecurity, value);
        }

        private string _progressStage;
        public string ProgressStage
        {
            get => _progressStage;
            private set => SetProperty(ref _progressStage, value);
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            private set => SetProperty(ref _progressValue, value);
        }

        private double _progressMaximum = 100;
        public double ProgressMaximum
        {
            get => _progressMaximum;
            private set => SetProperty(ref _progressMaximum, value <= 0 ? 100 : value);
        }

        private bool _isProgressIndeterminate;
        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            private set => SetProperty(ref _isProgressIndeterminate, value);
        }

        private string _progressMessage = "Idle";
        public string ProgressMessage
        {
            get => _progressMessage;
            private set => SetProperty(ref _progressMessage, value);
        }
        #endregion Bindable properties

        #region Commands
        public Command OpenFirmwareCommand { get; }
        public Command SaveLogCommand { get; }
        public Command OpenSettingsLocationCommand { get; }

        public AsyncCommand RefreshDevicesCommand { get; }
        public Command ShowDevicePropertiesCommand { get; }
        public Command ShowSupportedChipsCommand { get; }
        public Command ShowAboutCommand { get; }

        public AsyncCommand EraseCommand { get; }
        public AsyncCommand ResetCommand { get; }
        public AsyncCommand ShowChipInfoCommand { get; }
        public AsyncCommand UpdateCommand { get; }
        public AsyncCommand UnlockCommand { get; }

        public Command<string> FirmwareFileDroppedCommand { get; }

        public Command ClearLogCommand { get; }
        public Command<IList> CopySelectedLogCommand { get; }
        #endregion Commands

        public void OnAppStarted()
        {
            if (_isDesignMode)
                return;

            _logFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(LogFlushIntervalMs)
            };
            _logFlushTimer.Tick += OnLogFlushTimerTick;

            if (!_pendingLogEntries.IsEmpty)
                _logFlushTimer.Start();

            // Apply the persisted device filter to the shared discovery defaults before the
            // first scan (Atmel SAM-BA VID 0x03EB / PID 0x6124 by default, or any port).
            ApplyDeviceFilter();
            _ = RefreshDevicesAsync();
            SetupWatcher();
        }

        public void OnAppClosing()
        {
            SaveSettings();
        }

        #region Watcher
        private void SetupWatcher()
        {
            try
            {
                _watcher = new SamBaDeviceWatcher();
                _watcher.DeviceArrived += OnDeviceArrived;
                _watcher.DeviceRemoved += OnDeviceRemoved;
                if (IsWatcherEnabled)
                {
                    _watcher.Start();
                    AddLog("Device watcher started");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Watcher error: {ex.Message}");
            }
        }

        private void ToggleWatcher(bool enable)
        {
            if (enable)
                _watcher?.Start();
            else
                _watcher?.Stop();
        }

        // Push the current filter mode into the shared discovery defaults used by both
        // Enumerate() and the watcher. Null VID/PID = match any.
        private void ApplyDeviceFilter()
        {
            ushort? vid, pid;
            switch (DeviceFilterMode)
            {
                case DeviceFilterMode.AnyDevice:
                    vid = null;
                    pid = null;
                    break;
                case DeviceFilterMode.Custom:
                    vid = ParseVidPid(CustomVendorIdHex);
                    pid = ParseVidPid(CustomProductIdHex);
                    break;
                default: // AtmelSamBa
                    vid = SamBaDevice.DefaultVendorId;
                    pid = SamBaDevice.DefaultProductId;
                    break;
            }

            SamBaDeviceDiscovery.ConfigureDefaults(o =>
            {
                o.VendorId = vid;
                o.ProductId = pid;
            });
        }

        // Parses a hex VID/PID (up to 0xFFFF); blank or invalid yields null (match any).
        private static ushort? ParseVidPid(string hex)
        {
            return HexUtility.TryParse(hex, out uint value) && value <= 0xFFFF
                ? (ushort?)value
                : (ushort?)null;
        }

        private string DescribeFilter()
        {
            switch (DeviceFilterMode)
            {
                case DeviceFilterMode.AnyDevice:
                    return "Device filter: any serial port (VID/PID filter off)";
                case DeviceFilterMode.Custom:
                    ushort? vid = ParseVidPid(CustomVendorIdHex);
                    ushort? pid = ParseVidPid(CustomProductIdHex);
                    string v = vid.HasValue ? $"{vid.Value:X4}" : "any";
                    string p = pid.HasValue ? $"{pid.Value:X4}" : "any";
                    return $"Device filter: custom (VID {v} / PID {p})";
                default:
                    return "Device filter: Atmel SAM-BA (03EB:6124)";
            }
        }

        // The watcher captures the VID/PID filter only at Start(), so a filter change
        // needs a stop/start to take effect. No-op when the watcher is off.
        private void RestartWatcher()
        {
            if (_watcher != null && IsWatcherEnabled)
            {
                _watcher.Stop();
                _watcher.Start();
            }
        }

        private void OnDeviceFilterChanged()
        {
            // Keep the three radio menu items in sync with the mode.
            OnPropertyChanged(nameof(UseAtmelFilter));
            OnPropertyChanged(nameof(MatchAnyDevice));
            OnPropertyChanged(nameof(UseCustomFilter));
            ApplyDeviceFilter();
            AddLog(DescribeFilter());
            _ = RefreshDevicesAsync();
            RestartWatcher();
        }

        // Custom VID/PID text changed (commits on focus-loss). Only meaningful in Custom mode.
        private void OnCustomFilterValueChanged()
        {
            if (DeviceFilterMode != DeviceFilterMode.Custom)
                return;
            ApplyDeviceFilter();
            AddLog(DescribeFilter());
            _ = RefreshDevicesAsync();
            RestartWatcher();
        }

        private void OnDeviceArrived(object sender, SamBaDeviceArrivedEventArgs e)
        {
            var dev = e?.Device;
            if (dev == null)
                return;

            _dispatcher.BeginInvoke(() =>
            {
                if (!DeviceList.Any(d => string.Equals(d.DevicePath, dev.DevicePath, StringComparison.OrdinalIgnoreCase)))
                {
                    DeviceList.Add(dev);
                    // if it's a single device, auto select it.
                    if (DeviceList.Count == 1)
                        SelectedDevice = dev;
                    AddLog($"Device arrived: {dev.DisplayName}");
                }
            });
        }

        private void OnDeviceRemoved(object sender, SamBaDeviceRemovedEventArgs e)
        {
            var path = e?.DevicePath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            _dispatcher.BeginInvoke(() =>
            {
                var existing = DeviceList.FirstOrDefault(
                    d => string.Equals(d.DevicePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    var wasSelected = SelectedDevice != null
                    && string.Equals(SelectedDevice.DevicePath, path, StringComparison.OrdinalIgnoreCase);
                    DeviceList.Remove(existing);
                    AddLog($"Device removed: {existing.DisplayName}");

                    if (wasSelected)
                        SelectedDevice = null;
                }
            });
        }
        #endregion Watcher

        #region Device refresh
        private async Task RefreshDevicesAsync()
        {
            try
            {
                // Enumerate devices off the UI thread using preconfigured defaults.
                var devices = await Task.Run(() => SamBaDeviceDiscovery.Enumerate().ToList());

                _dispatcher.Invoke(() =>
                {
                    var selectedPath = SelectedDevice?.DevicePath;
                    DeviceList.ReplaceRange(devices);

                    if (!string.IsNullOrWhiteSpace(selectedPath))
                        SelectedDevice = DeviceList.FirstOrDefault(
                            d => string.Equals(d.DevicePath, selectedPath, StringComparison.OrdinalIgnoreCase));

                    // if it's a single device, auto select it.
                    if (DeviceList.Count == 1)
                        SelectedDevice = DeviceList.FirstOrDefault();

                    AddLog($"Devices refreshed, found: {DeviceList.Count}");
                });
            }
            catch (Exception ex)
            {
                AddLog($"Device refresh error: {ex.Message}");
            }
        }
        #endregion Device refresh

        #region UI Command implementations
        private void ExecuteOpenFirmware()
        {
            var path = _dialogService.BrowseFirmwareFile();
            if (string.IsNullOrWhiteSpace(path))
                return;

            FirmwareFilePath = path;
        }

        private void ExecuteFirmwareFileDropped(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            FirmwareFilePath = filePath;
        }

        private void ExecuteSaveLog()
        {
            var suggestedFileName = $"SamBaEventLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            var path = _dialogService.BrowseSaveLogFile(suggestedFileName);
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                DrainLogQueue();
                File.WriteAllLines(path, LogEntries.ToArray(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Save Log", ex.Message);
            }
        }

        private void ExecuteOpenSettingsLocation()
        {
            _processService.OpenFolder(_settingsService.SettingsDirectoryPath);
        }

        private void ExecuteShowDeviceProperties()
        {
            if (SelectedDevice == null)
                return;

            _toolWindowService.ShowText("Device Properties", SelectedDevice.ToString());
        }

        private void ExecuteShowSupportedChips()
        {
            var chips = SupportedChips.Get();

            var groups = chips
                .GroupBy(c => c.Family)
                .Select(g => $"— {g.Key} —" + Environment.NewLine
                    + string.Join(Environment.NewLine, g.Select(c => c.Name)));

            string text = $"{chips.Count} supported chip types:" + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, groups);

            _toolWindowService.ShowText("Supported Chips", text);
        }

        private void ExecuteShowAbout()
        {
            string text = $"SAM-BA Firmware Updater v{_appVersion}" + Environment.NewLine
                + $"Atmel.SamBa library v{_libraryVersion}";

            _toolWindowService.ShowText("About", text);
        }

        private void ExecuteClearLog()
        {
            // Discard any buffered entries so they don't reappear on the next timer tick.
            while (_pendingLogEntries.TryDequeue(out _)) { }
            LogEntries.Clear();
            ProgressStage = string.Empty;
        }
        #endregion UI Command implementations

        #region Device operations
        private async Task ExecuteEraseAsync()
        {
            if (SelectedDevice == null)
                return;

            if (!_dialogService.Confirm("Erase", "This will erase the entire flash memory. Continue?"))
                return;

            await RunDeviceOperationAsync("Erasing", device =>
            {
                device.EraseAllFlash();
                AddLog("Erase completed");
            });
        }

        private async Task ExecuteResetAsync()
        {
            if (SelectedDevice == null)
                return;

            await RunDeviceOperationAsync("Resetting", device =>
            {
                device.Reset();
                AddLog("Reset command sent");
            });
        }

        private async Task ExecuteUpdateAsync()
        {
            if (SelectedDevice == null)
                return;

            var fwPath = FirmwareFilePath;
            if (string.IsNullOrWhiteSpace(fwPath) || !File.Exists(fwPath))
            {
                fwPath = _dialogService.BrowseFirmwareFile();
                if (string.IsNullOrWhiteSpace(fwPath))
                    return;

                FirmwareFilePath = fwPath;
            }

            byte[] image;
            try
            {
                image = File.ReadAllBytes(fwPath);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Open Firmware", ex.Message);
                return;
            }

            if (!HexUtility.TryParse(FwOffsetHex, out uint offset))
            {
                _dialogService.ShowError("Update", "Invalid offset.");
                return;
            }

            if (FwSecurity && !ConfirmSecurity())
                return;

            AddLog($"Starting update: {Path.GetFileName(fwPath)}");

            await RunDeviceOperationAsync("Updating", device =>
            {
                device.UpdateFirmware(image, new SamBaUpdateOptions
                {
                    BulkErase = EraseAllFlash,
                    Verify = VerifyAfterWrite,
                    UnlockBeforeWrite = AutoUnlock,
                    SetBootToFlash = FwBootToFlash,
                    Offset = offset,
                    Lock = FwLockScope,
                    SetSecurity = FwSecurity ? SecurityAction.SetPermanently : SecurityAction.Leave,
                    Reset = ResetAfterWrite,
                });

                AddLog("Update completed");
            });
        }

        private bool ConfirmSecurity()
        {
            return _dialogService.Confirm("Set Security Bit",
                "Setting the security bit blocks all further SAM-BA access.\n" +
                "Only the ERASE pin can recover the part. Continue?");
        }

        private async Task ExecuteShowChipInfoAsync()
        {
            if (SelectedDevice == null)
                return;

            await RunDeviceOperationAsync("Getting Chip Info", device =>
            {
                var text = device.ChipInfo + Environment.NewLine
                    + $"Monitor version: {device.MonitorVersion}";
                _toolWindowService.ShowText("Chip Info", text);
            });
        }

        private async Task ExecuteUnlockAsync()
        {
            if (SelectedDevice == null)
                return;

            await RunDeviceOperationAsync("Unlocking", device =>
            {
                device.SetLockRegions(false);
                AddLog("All lock regions unlocked");
            });
        }

        private async Task RunDeviceOperationAsync(
            string initialStage,
            Action<SamBaDevice> action,
            Type suppressedExceptionType = null)
        {
            var deviceSnapshot = SelectedDevice;
            if (deviceSnapshot == null)
                return;

            await _deviceLock.WaitAsync();

            Stopwatch timer = Stopwatch.StartNew();
            string resultStatus = "Complete";

            try
            {
                IsBusy = true;
                RaiseDeviceCanExecuteChanged();
                OpenFirmwareCommand.RaiseCanExecuteChanged();
                SaveLogCommand.RaiseCanExecuteChanged();
                RefreshDevicesCommand.RaiseCanExecuteChanged();

                UpdateProgress(initialStage, "", indeterminate: true);

                await Task.Run(() =>
                {
                    try
                    {
                        deviceSnapshot.ProgressChanged += OnDeviceProgressChanged;
                        deviceSnapshot.SafeMode = IsSafeModeEnabled;
                        deviceSnapshot.Open(ChipIdentificationMode, GeometryPrecedence);

                        action(deviceSnapshot);
                    }
                    finally
                    {
                        try { deviceSnapshot.Close(); } catch { /* ignore */ }
                        try { deviceSnapshot.ProgressChanged -= OnDeviceProgressChanged; } catch { /* ignore */ }
                    }
                });
            }
            catch (Exception ex)
            {
                if (suppressedExceptionType != null && suppressedExceptionType.IsInstanceOfType(ex))
                {
                    AddLog($"Expected error: {ex.Message}");
                }
                else
                {
                    AddLog($"Error: {ex.Message}");
                    resultStatus = "Error";
                    _dialogService.ShowError("Operation failed", ex.Message);
                }
            }
            finally
            {
                IsBusy = false;
                _deviceLock.Release();

                timer.Stop();
                AddLog($"Operation {resultStatus} in {timer.Elapsed.TotalSeconds:F1} s");
                UpdateProgress("Idle", $"{resultStatus}", indeterminate: false);

                // Drain any remaining buffered log entries before final status update.
                DrainLogQueue();

                RaiseDeviceCanExecuteChanged();
                OpenFirmwareCommand.RaiseCanExecuteChanged();
                SaveLogCommand.RaiseCanExecuteChanged();
                RefreshDevicesCommand.RaiseCanExecuteChanged();
            }
        }
        #endregion Device operations

        #region Helpers
        private bool CanInteract() => !IsBusy;

        private bool CanOperateOnDevice() => !IsBusy && SelectedDevice != null;

        private void RaiseDeviceCanExecuteChanged()
        {
            EraseCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            UpdateCommand.RaiseCanExecuteChanged();
            ShowChipInfoCommand.RaiseCanExecuteChanged();
            UnlockCommand.RaiseCanExecuteChanged();
        }

        private void OnDeviceProgressChanged(object sender, SamBaProgressEventArgs e)
        {
            if (e == null)
                return;

            var stage = string.IsNullOrWhiteSpace(e.Stage) ? "Working" : e.Stage;

            // The progress bar shows the stage only ("Erasing", "Writing", …); the running
            // count goes to the log.
            UpdateProgress(stage, stage, e.IsIndeterminate, e.Value, e.Maximum ?? 0);

            if (e.IsIndeterminate)
            {
                // Discrete stage updates (Erasing, Applying options, …) carry their own text.
                if (!string.IsNullOrWhiteSpace(e.Message))
                    AddLog(e.Message);
                return;
            }

            // Determinate ticks (per page/byte): log every update — no per-percent throttle, so
            // no page is dropped. The flush timer coalesces pending entries into a single batched
            // insert (LogEntries.AddBatch), so catching every tick doesn't thrash the UI even on a
            // multi-hundred-page write.
            string units = string.IsNullOrEmpty(e.Units) ? string.Empty : " " + e.Units;
            AddLog($"{stage}: {e.Value}/{e.Maximum}{units} ({e.Percentage ?? 0}%)");
        }

        // Must be called on the UI thread (modifies a bound collection).
        // Drains the entire queue in one pass. A per-tick cap could smooth out bursts from
        // a misbehaving device, but TrimLog already bounds the collection size, so the UI
        // cost is limited regardless of how many entries are dequeued.
        private void DrainLogQueue()
        {
            if (_pendingLogEntries.IsEmpty)
                return;

            // Marshal to the UI thread if called from a background thread.
            _dispatcher.Invoke(() =>
            {
                var batch = new List<string>();
                while (_pendingLogEntries.TryDequeue(out var entry))
                    batch.Add(entry);

                if (batch.Count > 0)
                {
                    LogEntries.AddBatch(batch);
                    TrimLog();
                }
            });
        }

        private void OnLogFlushTimerTick(object sender, EventArgs e)
        {
            DrainLogQueue();

            // Stop ticking when idle; AddLog will restart when new entries arrive.
            if (_pendingLogEntries.IsEmpty)
            {
                _logFlushTimer.Stop();
                Interlocked.Exchange(ref _logTimerRunningOrRequested, 0);
            }
        }

        private void UpdateProgress(string message, string stage, bool indeterminate, long value = 0, long max = 100)
        {
            _dispatcher.BeginInvoke(() =>
            {
                ProgressMessage = message ?? "";
                ProgressStage = stage ?? "";
                IsProgressIndeterminate = indeterminate;
                ProgressValue = value;
                ProgressMaximum = max;
            });
        }

        private void AddLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            _pendingLogEntries.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {message}");

            // Only request Start() once until the timer decides to stop
            if (Interlocked.Exchange(ref _logTimerRunningOrRequested, 1) == 0)
                _dispatcher.BeginInvoke(() => _logFlushTimer?.Start());
        }

        private void TrimLog()
        {
            var excess = LogEntries.Count - MaxLogEntries;

            if (excess <= 0)
                return;

            // Batch remove avoids per-item O(n) shifts and repeated CollectionChanged events.
            LogEntries.RemoveLeading(excess);
        }

        // Predicate doesn't work because the CommandParameter is always null.
        // This is a known WPF issue with SelectedItems — the binding in XAML uses
        // PlacementTarget.SelectedItems which resolves only at execution time, not at CanExecute.
        // Enable for now (harmless).
        private bool CanCopySelectedLog(IList selectedItems) => true;

        private void ExecuteCopyLog(IList selectedItems)
        {
            if (selectedItems == null || selectedItems.Count == 0)
                return;

            // Handles either string items or objects (uses ToString()).
            var lines = selectedItems
                .Cast<object>()
                .Select(x => x?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            if (lines.Length == 0)
                return;

            _clipboardService.SetText(string.Join(Environment.NewLine, lines));
        }
        #endregion Helpers

        #region Settings
        private void LoadSettings()
        {
            var s = _settingsService.LoadOrDefault();
            FirmwareFilePath = s.FirmwareFilePath;
            IsAdvancedMode = s.IsAdvancedMode;
            IsWatcherEnabled = s.IsWatcherEnabled;
            // Set the fields directly: OnAppStarted applies the filter and does the first scan,
            // so we must not trigger the setter's refresh/watcher-restart during construction.
            _deviceFilterMode = s.DeviceFilterMode;
            _customVendorIdHex = HexUtility.Normalize(s.CustomVendorIdHex);
            _customProductIdHex = HexUtility.Normalize(s.CustomProductIdHex);
            IsSafeModeEnabled = s.IsSafeModeEnabled;
            AutoUnlock = s.AutoUnlock;
            ChipIdentificationMode = s.ChipIdentificationMode;
            GeometryPrecedence = s.GeometryPrecedence;
            MaxLogEntries = s.MaxLogEntries;

            EraseAllFlash = s.EraseAllFlash;
            VerifyAfterWrite = s.VerifyAfterWrite;
            ResetAfterWrite = s.ResetAfterWrite;

            FwOffsetHex = NormalizeOrDefault(s.FwOffsetHex);
            FwLockScope = s.FwLockScope;
            FwBootToFlash = s.FwBootToFlash;
        }

        private static string NormalizeOrDefault(string hex)
        {
            var normalized = HexUtility.Normalize(hex);
            return string.IsNullOrWhiteSpace(normalized) ? DefaultOffsetHex : normalized;
        }

        private void SaveSettings()
        {
            try
            {
                var s = new AppSettings
                {
                    FirmwareFilePath = FirmwareFilePath,
                    IsAdvancedMode = IsAdvancedMode,
                    IsWatcherEnabled = IsWatcherEnabled,
                    DeviceFilterMode = DeviceFilterMode,
                    CustomVendorIdHex = CustomVendorIdHex,
                    CustomProductIdHex = CustomProductIdHex,
                    IsSafeModeEnabled = IsSafeModeEnabled,
                    AutoUnlock = AutoUnlock,
                    ChipIdentificationMode = ChipIdentificationMode,
                    GeometryPrecedence = GeometryPrecedence,
                    MaxLogEntries = MaxLogEntries,

                    EraseAllFlash = EraseAllFlash,
                    VerifyAfterWrite = VerifyAfterWrite,
                    ResetAfterWrite = ResetAfterWrite,

                    FwOffsetHex = FwOffsetHex,
                    FwLockScope = FwLockScope,
                    FwBootToFlash = FwBootToFlash,
                };

                _settingsService.Save(s);
            }
            catch
            {
                // ignore
            }
        }
        #endregion Settings

        #region IDataErrorInfo
        public string Error => null;

        // Validate individual properties
        public string this[string columnName]
        {
            get
            {
                if (columnName == nameof(FwOffsetHex))
                {
                    return HexUtility.TryParse(FwOffsetHex, out _) ? null : "Invalid hex address";
                }

                return null;
            }
        }
        #endregion IDataErrorInfo

        #region IDisposable support
        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                try
                {
                    if (_logFlushTimer != null)
                    {
                        _logFlushTimer.Stop();
                        _logFlushTimer.Tick -= OnLogFlushTimerTick;
                        _logFlushTimer = null;
                    }
                    if (_watcher != null)
                    {
                        _watcher.DeviceArrived -= OnDeviceArrived;
                        _watcher.DeviceRemoved -= OnDeviceRemoved;
                        _watcher.Stop();
                        _watcher.Dispose();
                        _watcher = null;
                    }
                }
                catch
                {
                    // ignore
                }
                try { _deviceLock.Dispose(); } catch { /* ignore */ }
            }
            _disposed = true;
        }
        #endregion IDisposable support
    }
}

using Anp.Atmel.SamBa.Configuration;
using Anp.Atmel.SamBa.Events;
using Anp.Atmel.SamBa.Lite.Models;
using Anp.Atmel.SamBa.Lite.Services;
using Anp.Atmel.SamBa.Lite.Utilities;
using MvvmHelpers;
using MvvmHelpers.Commands;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;


namespace Anp.Atmel.SamBa.Lite.ViewModels
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

            DeviceList = new ObservableRangeCollection<SamBaDevice>();
            LogEntries = new BatchObservableCollection<string>();

            OpenFirmwareCommand = new Command(ExecuteOpenFirmware, CanInteract);
            SaveViewBinaryCommand = new Command(ExecuteSaveViewBinary, () => HexViewData != null);
            SaveViewTextCommand = new Command(ExecuteSaveViewText, () => HexViewData != null);
            SaveLogCommand = new Command(ExecuteSaveLog, CanInteract);
            OpenSettingsLocationCommand = new Command(ExecuteOpenSettingsLocation);

            RefreshDevicesCommand = new AsyncCommand(RefreshDevicesAsync, (_) => CanInteract());
            ShowDevicePropertiesCommand = new Command(ExecuteShowDeviceProperties, () => SelectedDevice != null);
            ShowSupportedChipsCommand = new Command(ExecuteShowSupportedChips);
            ShowAboutCommand = new Command(ExecuteShowAbout);
            ShowChipInfoCommand = new AsyncCommand(ExecuteShowChipInfoAsync, (_) => CanOperateOnDevice());

            ReadCommand = new AsyncCommand(ExecuteReadAsync, (_) => CanOperateOnDevice());
            WriteWordCommand = new AsyncCommand(ExecuteWriteWordAsync, (_) => CanOperateOnDevice());
            WriteFileCommand = new AsyncCommand(ExecuteWriteFileAsync, (_) => CanOperateOnDevice());
            LockAllCommand = new AsyncCommand(ExecuteLockAllAsync, (_) => CanOperateOnDevice());
            UnlockAllCommand = new AsyncCommand(ExecuteUnlockAllAsync, (_) => CanOperateOnDevice());
            DisplayLockRegionsCommand = new AsyncCommand(
                ExecuteDisplayLockRegionsAsync, (_) => CanOperateOnDevice());
            GoCommand = new AsyncCommand(ExecuteGoAsync, (_) => CanOperateOnDevice());
            ResetCommand = new AsyncCommand(ExecuteResetAsync, (_) => CanOperateOnDevice());
            EraseCommand = new AsyncCommand(ExecuteEraseAsync, (_) => CanOperateOnDevice());
            ShowDeviceStatusCommand = new AsyncCommand(ExecuteShowDeviceStatusAsync, (_) => CanOperateOnDevice());
            BootFromFlashCommand = new AsyncCommand(ExecuteBootFromFlashAsync, (_) => CanOperateOnDevice());
            BootFromRomCommand = new AsyncCommand(ExecuteBootFromRomAsync, (_) => CanOperateOnDevice());
            SetSecurityCommand = new AsyncCommand(ExecuteSetSecurityAsync, (_) => CanOperateOnDevice());
            UpdateFirmwareCommand = new AsyncCommand(ExecuteUpdateFirmwareAsync, (_) => CanOperateOnDevice());

            FirmwareFileDroppedCommand = new Command<string>(ExecuteFirmwareFileDropped);

            ClearLogCommand = new Command(ExecuteClearLog);
            CopySelectedLogCommand = new Command<IList>(ExecuteCopyLog, (_) => true);

            SetReadAddressToFlashBaseCommand = new Command(
                () => ReadAddressHex = FlashBaseHexOrNull() ?? ReadAddressHex,
                () => FlashBaseHexOrNull() != null);
            SetWriteAddressToFlashBaseCommand = new Command(
                () => WriteAddressHex = FlashBaseHexOrNull() ?? WriteAddressHex,
                () => FlashBaseHexOrNull() != null);
            SetGoAddressToFlashBaseCommand = new Command(
                () => GoAddressHex = FlashBaseHexOrNull() ?? GoAddressHex,
                () => FlashBaseHexOrNull() != null);

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
        public string WindowTitle => $"SamBa Lite v{_appVersion}";

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
        private bool _autoUnlock = true;
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

        // The three "Chip Identification" menu items act as a radio group, same pattern as the
        // "Bytes per Line" items.
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

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => SetProperty(ref _selectedTabIndex, value);
        }

        #region Update FW options
        private bool _fwEraseAll;
        public bool FwEraseAll
        {
            get => _fwEraseAll;
            set => SetProperty(ref _fwEraseAll, value);
        }

        private bool _fwVerify = true;
        public bool FwVerify
        {
            get => _fwVerify;
            set => SetProperty(ref _fwVerify, value);
        }

        // Offset into flash at which the image is written. Persisted; default 0.
        private string _fwOffsetHex = DefaultAddressHex;
        public string FwOffsetHex
        {
            get => _fwOffsetHex;
            set => SetProperty(ref _fwOffsetHex, HexUtility.Normalize(value));
        }

        // Which lock regions to lock after programming. Persisted; default None.
        private FlashLockScope _fwLockScope;
        public FlashLockScope FwLockScope
        {
            get => _fwLockScope;
            set => SetProperty(ref _fwLockScope, value);
        }

        public static IEnumerable<FlashLockScope> LockScopeValues { get; } =
            (FlashLockScope[])Enum.GetValues(typeof(FlashLockScope));

        // Set the security bit after programming. Irreversible; confirmed before the update
        // runs and not persisted.
        private bool _fwSecurity;
        public bool FwSecurity
        {
            get => _fwSecurity;
            set => SetProperty(ref _fwSecurity, value);
        }

        private bool _fwBootToFlash = true;
        public bool FwBootToFlash
        {
            get => _fwBootToFlash;
            set => SetProperty(ref _fwBootToFlash, value);
        }

        private bool _fwResetAfterLoad = true;
        public bool ResetAfterWrite
        {
            get => _fwResetAfterLoad;
            set => SetProperty(ref _fwResetAfterLoad, value);
        }
        #endregion Update FW options

        #region Read / Write / Misc inputs
        private string _readAddressHex = DefaultAddressHex;
        public string ReadAddressHex
        {
            get => _readAddressHex;
            set => SetProperty(ref _readAddressHex, HexUtility.Normalize(value));
        }

        private string _readSizeText = "1024";
        public string ReadSizeText
        {
            get => _readSizeText;
            set => SetProperty(ref _readSizeText, value);
        }

        // When set, Read performs a single 32-bit word access (device.ReadWord) instead of a
        // block read, and shows the 4 resulting bytes in the memory view. Size is unused then.
        // Persisted: a standing preference for how Read behaves.
        private bool _isWordRead;
        public bool IsWordRead
        {
            get => _isWordRead;
            set => SetProperty(ref _isWordRead, value);
        }

        // Always selectable alongside Word or a block read. When set, Read prompts for a
        // binary file first, then compares it against the bytes just read (whichever way they
        // were read). Not persisted: a per-use choice tied to a file picked each time.
        private bool _isVerifyRead;
        public bool IsVerifyRead
        {
            get => _isVerifyRead;
            set => SetProperty(ref _isVerifyRead, value);
        }

        private string _writeAddressHex = DefaultAddressHex;
        public string WriteAddressHex
        {
            get => _writeAddressHex;
            set => SetProperty(ref _writeAddressHex, HexUtility.Normalize(value));
        }

        // 32-bit value for the word write, hex. Empty is allowed and means "browse for a file"
        // was intended — the Write Word button validates and complains instead of guessing.
        private string _writeWordHex = string.Empty;
        public string WriteWordHex
        {
            get => _writeWordHex;
            set => SetProperty(ref _writeWordHex, HexUtility.Normalize(value));
        }

        private string _miscAddressHex = DefaultAddressHex;
        public string GoAddressHex
        {
            get => _miscAddressHex;
            set => SetProperty(ref _miscAddressHex, HexUtility.Normalize(value));
        }

        // Not persisted: like the Lock and Security firmware options, a region list is a
        // per-session decision. Empty means all regions.
        private string _lockRegionsText = string.Empty;
        public string LockRegionsText
        {
            get => _lockRegionsText;
            set => SetProperty(ref _lockRegionsText, value);
        }

        public const string DefaultAddressHex = "00000000";
        #endregion Read / Write / Misc inputs

        #region Hex view
        // The bytes shown in the hex view and the device address of the first byte.
        // The view (code-behind) mirrors these into the WPFHexaEditor control, which has no
        // bindable data source; the view model stays the single source of truth for saving.
        private byte[] _hexViewData;
        public byte[] HexViewData
        {
            get => _hexViewData;
            private set
            {
                if (SetProperty(ref _hexViewData, value))
                {
                    SaveViewBinaryCommand.RaiseCanExecuteChanged();
                    SaveViewTextCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private long _hexViewBaseAddress;
        public long HexViewBaseAddress
        {
            get => _hexViewBaseAddress;
            private set => SetProperty(ref _hexViewBaseAddress, value);
        }

        private int _bytesPerLine = 16;
        public int BytesPerLine
        {
            get => _bytesPerLine;
            set => SetProperty(ref _bytesPerLine, value, onChanged: OnBytesPerLineChanged);
        }

        // The three "Bytes per Line" menu items act as a radio group, same pattern as the
        // device-filter items.
        public bool BytesPerLine4
        {
            get => BytesPerLine == 4;
            set { if (value) BytesPerLine = 4; else OnPropertyChanged(); }
        }

        public bool BytesPerLine8
        {
            get => BytesPerLine == 8;
            set { if (value) BytesPerLine = 8; else OnPropertyChanged(); }
        }

        public bool BytesPerLine16
        {
            get => BytesPerLine == 16;
            set { if (value) BytesPerLine = 16; else OnPropertyChanged(); }
        }

        // Prefix each line with its device address when saving the hex view as text.
        private bool _saveWithAddress = true;
        public bool SaveWithAddress
        {
            get => _saveWithAddress;
            set => SetProperty(ref _saveWithAddress, value);
        }

        // Shows/hides the hex editor's ASCII column (bound via BoolToVis to its
        // StringDataVisibility, a plain Visibility property with no bool counterpart).
        private bool _showAscii = true;
        public bool ShowAscii
        {
            get => _showAscii;
            set => SetProperty(ref _showAscii, value);
        }

        // Memory-view/log row heights in DIPs, mirrored from the GridSplitter drag in
        // code-behind (plain layout state, not something the hex editor or grid expose as a
        // bindable value). Both rows are Star: the splitter re-weights both together on drag,
        // so both must be captured and restored together, or the split ratio is lost. 0 means
        // "use the XAML default", same convention as AppSettings.
        private double _memoryViewHeight;
        public double MemoryViewHeight
        {
            get => _memoryViewHeight;
            set => SetProperty(ref _memoryViewHeight, value);
        }

        private double _logViewHeight;
        public double LogViewHeight
        {
            get => _logViewHeight;
            set => SetProperty(ref _logViewHeight, value);
        }
        #endregion Hex view

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
        public Command SaveViewBinaryCommand { get; }
        public Command SaveViewTextCommand { get; }
        public Command SaveLogCommand { get; }
        public Command OpenSettingsLocationCommand { get; }

        public AsyncCommand RefreshDevicesCommand { get; }
        public Command ShowDevicePropertiesCommand { get; }
        public Command ShowSupportedChipsCommand { get; }
        public Command ShowAboutCommand { get; }
        public AsyncCommand ShowChipInfoCommand { get; }

        public AsyncCommand ReadCommand { get; }
        public AsyncCommand WriteWordCommand { get; }
        public AsyncCommand WriteFileCommand { get; }
        public AsyncCommand LockAllCommand { get; }
        public AsyncCommand UnlockAllCommand { get; }
        public AsyncCommand DisplayLockRegionsCommand { get; }
        public AsyncCommand GoCommand { get; }
        public AsyncCommand ResetCommand { get; }
        public AsyncCommand EraseCommand { get; }
        public AsyncCommand ShowDeviceStatusCommand { get; }
        public AsyncCommand BootFromFlashCommand { get; }
        public AsyncCommand BootFromRomCommand { get; }
        public AsyncCommand SetSecurityCommand { get; }
        public AsyncCommand UpdateFirmwareCommand { get; }

        public Command<string> FirmwareFileDroppedCommand { get; }

        public Command ClearLogCommand { get; }
        public Command<IList> CopySelectedLogCommand { get; }

        public Command SetReadAddressToFlashBaseCommand { get; }
        public Command SetWriteAddressToFlashBaseCommand { get; }
        public Command SetGoAddressToFlashBaseCommand { get; }
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
                if (!DeviceList.Any(
                    d => string.Equals(d.DevicePath, dev.DevicePath, StringComparison.OrdinalIgnoreCase)))
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

        private void ExecuteSaveViewBinary()
        {
            var data = HexViewData;
            if (data == null || data.Length == 0)
                return;

            var suggested = $"Dump_0x{HexViewBaseAddress:X8}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            var path = _dialogService.BrowseSaveBinaryFile(suggested);
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                File.WriteAllBytes(path, data);
                AddLog($"Saved: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Save Binary", ex.Message);
            }
        }

        // Saves the hex view as text: one line per BytesPerLine bytes, optionally prefixed
        // with the device address. Saving with a .csv extension writes comma-separated cells.
        private void ExecuteSaveViewText()
        {
            var data = HexViewData;
            if (data == null || data.Length == 0)
                return;

            var suggested = $"Dump_0x{HexViewBaseAddress:X8}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var path = _dialogService.BrowseSaveTextFile(suggested);
            if (string.IsNullOrWhiteSpace(path))
                return;

            bool csv = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
            string separator = csv ? "," : " ";

            try
            {
                var text = new StringBuilder();
                int perLine = BytesPerLine;

                for (int lineStart = 0; lineStart < data.Length; lineStart += perLine)
                {
                    var cells = new List<string>();
                    if (SaveWithAddress)
                        cells.Add($"{HexViewBaseAddress + lineStart:X8}");

                    int count = Math.Min(perLine, data.Length - lineStart);
                    for (int i = 0; i < count; i++)
                        cells.Add(data[lineStart + i].ToString("X2"));

                    text.AppendLine(string.Join(separator, cells));
                }

                File.WriteAllText(path, text.ToString(), Encoding.UTF8);
                AddLog($"Saved: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Save Text", ex.Message);
            }
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
            string text = $"SamBa Lite v{_appVersion}" + Environment.NewLine
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
        private async Task ExecuteReadAsync()
        {
            if (!TryParseAddress(ReadAddressHex, "Read", out uint address))
                return;

            int size = 4;
            if (!IsWordRead && (!int.TryParse(ReadSizeText, out size) || size <= 0))
            {
                _dialogService.ShowError("Read", "Invalid size.");
                return;
            }

            byte[] expected = null;
            if (IsVerifyRead && !TryLoadVerifyFile(out expected))
                return;

            byte[] data = null;
            bool wordRead = IsWordRead;
            bool? verifyPassed = null;
            long verifyMismatchOffset = -1;

            await RunDeviceOperationAsync("Reading", device =>
            {
                if (wordRead)
                {
                    uint value = device.ReadWord(address);
                    data = BitConverter.GetBytes(value);
                    AddLog($"Word at 0x{address:X8}: 0x{value:X8}");
                }
                else
                {
                    data = device.ReadMemory(address, size);
                    AddLog($"Read {data.Length} bytes from 0x{address:X8}");
                }

                if (expected != null)
                    verifyPassed = CompareAndLogVerify(address, data, expected, out verifyMismatchOffset);
            });

            if (data == null)
                return;

            HexViewBaseAddress = address;
            HexViewData = data;

            if (verifyPassed == false)
                _dialogService.ShowError(
                    "Verify", $"Verification failed at 0x{address + (uint)verifyMismatchOffset:X8}.");
            else if (verifyPassed == true)
                _dialogService.ShowInfo("Verify", "Verification passed.");
        }

        // Loads the file to compare against before the read starts, so a bad/missing file is
        // caught up front instead of after spending time talking to the device.
        private bool TryLoadVerifyFile(out byte[] expected)
        {
            expected = null;

            var path = _dialogService.BrowseOpenBinaryFile("Select binary file to verify against");
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                expected = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Verify", ex.Message);
                expected = null;
                return false;
            }

            if (expected.Length == 0)
            {
                _dialogService.ShowError("Verify", "The selected file is empty.");
                expected = null;
                return false;
            }

            return true;
        }

        // Compares bytes just read (block or single word) against a file loaded before the
        // read. A length mismatch is reported as a mismatch at the first byte past the
        // shorter of the two, since neither side has anything trustworthy to compare there.
        private bool CompareAndLogVerify(uint address, byte[] actual, byte[] expected, out long mismatchOffset)
        {
            int count = Math.Min(actual.Length, expected.Length);
            mismatchOffset = -1;

            for (int i = 0; i < count; i++)
            {
                if (actual[i] != expected[i])
                {
                    mismatchOffset = i;
                    break;
                }
            }

            if (mismatchOffset < 0 && actual.Length != expected.Length)
                mismatchOffset = count;

            bool passed = mismatchOffset < 0;
            AddLog(passed
                ? $"Verification passed: {count} bytes at 0x{address:X8}"
                : $"Verification failed at 0x{address + (uint)mismatchOffset:X8}");

            return passed;
        }

        // Writes the 32-bit word straight to the bus (the monitor's word-write command), the
        // right access for peripheral registers and RAM. Flash needs page programming — use
        // Write File or Update FW for that.
        private async Task ExecuteWriteWordAsync()
        {
            if (!TryParseAddress(WriteAddressHex, "Write Word", out uint address))
                return;

            if (!HexUtility.TryParse(WriteWordHex, out uint word))
            {
                _dialogService.ShowError("Write Word", "Enter a valid hex word to write.");
                return;
            }

            await RunDeviceOperationAsync("Writing", device =>
            {
                device.WriteWord(address, word);
                AddLog($"Wrote 0x{word:X8} to 0x{address:X8}");
            });
        }

        // Writes a file at the given address through the library's routed write: ranges inside
        // the flash window go through the page programmer (partial pages read-modify-write),
        // anything else is a raw memory write.
        private async Task ExecuteWriteFileAsync()
        {
            if (!TryParseAddress(WriteAddressHex, "Write File", out uint address))
                return;

            var path = _dialogService.BrowseOpenBinaryFile("Select binary file to write");
            if (string.IsNullOrWhiteSpace(path))
                return;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Write File", ex.Message);
                return;
            }

            if (data.Length == 0)
            {
                _dialogService.ShowError("Write File", "The selected file is empty.");
                return;
            }

            AddLog($"Writing {Path.GetFileName(path)} ({data.Length} bytes) to 0x{address:X8}");

            await RunDeviceOperationAsync("Writing", device =>
            {
                device.WriteMemory(address, data);
                AddLog($"Wrote {data.Length} bytes to 0x{address:X8}");
            });
        }

        private Task ExecuteLockAllAsync() => SetLockRegionsAsync(true);

        private Task ExecuteUnlockAllAsync() => SetLockRegionsAsync(false);

        private async Task SetLockRegionsAsync(bool locked)
        {
            if (!TryParseRegionRanges(LockRegionsText, out int[] regions, out string error))
            {
                _dialogService.ShowError("Lock Regions", error);
                return;
            }

            string verb = locked ? "locked" : "unlocked";
            await RunDeviceOperationAsync(locked ? "Locking" : "Unlocking", device =>
            {
                if (regions == null)
                {
                    device.SetLockRegions(locked);
                    AddLog($"All lock regions {verb}");
                }
                else
                {
                    device.SetLockRegions(regions, locked);
                    AddLog($"Regions {string.Join(", ", regions)} {verb}");
                }
            });
        }

        /// <summary>
        /// Parses a region list like "0-3, 8" into indexes; empty or whitespace yields null,
        /// meaning all regions. The range syntax mirrors what the Display button logs, so its
        /// output can be pasted straight back in.
        /// </summary>
        private static bool TryParseRegionRanges(string text, out int[] regions, out string error)
        {
            regions = null;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
                return true;

            var indexes = new SortedSet<int>();
            foreach (string piece in text.Split(','))
            {
                string trimmed = piece.Trim();
                int dash = trimmed.IndexOf('-');

                bool valid;
                int first;
                int last = 0;
                if (dash < 0)
                {
                    valid = int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture,
                        out first);
                    last = first;
                }
                else
                {
                    valid = int.TryParse(trimmed.Substring(0, dash).TrimEnd(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out first)
                        && int.TryParse(trimmed.Substring(dash + 1).TrimStart(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out last)
                        && first <= last;
                }

                if (!valid)
                {
                    error = $"'{trimmed}' is not a region number or a range like 0-3.";
                    return false;
                }

                for (int region = first; region <= last; region++)
                    indexes.Add(region);
            }

            regions = new int[indexes.Count];
            indexes.CopyTo(regions);
            return true;
        }

        private async Task ExecuteDisplayLockRegionsAsync()
        {
            await RunDeviceOperationAsync("Reading lock regions", device =>
            {
                var locked = device.GetLockRegions();
                AddLog($"Locked regions: {FormatLockedRegions(locked)}");
            });
        }

        private async Task ExecuteGoAsync()
        {
            if (!TryParseAddress(GoAddressHex, "Go", out uint address))
                return;

            await RunDeviceOperationAsync("Jumping", device =>
            {
                device.Go(address);
                AddLog($"Go to 0x{address:X8} sent");
            });
        }

        private async Task ExecuteResetAsync()
        {
            await RunDeviceOperationAsync("Resetting", device =>
            {
                device.Reset();
                AddLog("Reset command sent");
            });
        }

        private async Task ExecuteEraseAsync()
        {
            if (!_dialogService.Confirm("Erase", "This will erase the entire flash memory. Continue?"))
                return;

            await RunDeviceOperationAsync("Erasing", device =>
            {
                device.EraseAllFlash();
                AddLog("Erase completed");
            });
        }

        // One-stop status readout: boot source, security, lock regions and unique id.
        private async Task ExecuteShowDeviceStatusAsync()
        {
            await RunDeviceOperationAsync("Reading status", device =>
            {
                AddLog($"Chip: {device.ChipInfo}");
                AddLog($"Monitor version: {device.MonitorVersion}");
                AddLog($"Boot source: {DescribeOrFallback(() => DescribeBootSource(device))}");
                AddLog($"Security bit: {DescribeOrFallback(() => device.GetSecurity() ? "set" : "clear")}");
                var locked = DescribeOrFallback(() => FormatLockedRegions(device.GetLockRegions()));
                AddLog($"Locked regions: {locked}");
                if (device.ChipInfo.HasUniqueId)
                    AddLog($"Unique id: {DescribeOrFallback(() => FormatUniqueId(device.GetUniqueId()))}");
            });
        }

        private async Task ExecuteBootFromFlashAsync()
        {
            await RunDeviceOperationAsync("Setting boot source", device =>
            {
                device.SetBootSource(SamBaChipBootSource.Flash);
                AddLog("Boot source set to flash");
            });
        }

        private async Task ExecuteBootFromRomAsync()
        {
            await RunDeviceOperationAsync("Setting boot source", device =>
            {
                device.SetBootSource(SamBaChipBootSource.Rom);
                AddLog("Boot source set to ROM (SAM-BA monitor)");
            });
        }

        private async Task ExecuteSetSecurityAsync()
        {
            if (!ConfirmSecurity())
                return;

            await RunDeviceOperationAsync("Setting security bit", device =>
            {
                device.SetSecurity();
                AddLog("Security bit set");
            });
        }

        private bool ConfirmSecurity()
        {
            return _dialogService.Confirm("Set Security Bit",
                "Setting the security bit blocks all further SAM-BA access.\n" +
                "Only the ERASE pin can recover the part. Continue?");
        }

        private async Task ExecuteUpdateFirmwareAsync()
        {
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

            if (!TryParseAddress(FwOffsetHex, "Update FW", out uint offset))
                return;

            if (FwSecurity && !ConfirmSecurity())
                return;

            AddLog($"Starting FW Update: {Path.GetFileName(fwPath)}");

            await RunDeviceOperationAsync("Loading FW", device =>
            {
                device.UpdateFirmware(image, new SamBaUpdateOptions
                {
                    Offset = offset,
                    BulkErase = FwEraseAll,
                    Verify = FwVerify,
                    UnlockBeforeWrite = AutoUnlock,
                    SetBootToFlash = FwBootToFlash,
                    Lock = FwLockScope,
                    SetSecurity = FwSecurity ? SecurityAction.SetPermanently : SecurityAction.Leave,
                    Reset = ResetAfterWrite,
                });

                AddLog("FW update completed");
            });
        }

        private async Task ExecuteShowChipInfoAsync()
        {
            await RunDeviceOperationAsync("Getting Chip Info", device =>
            {
                var text = device.ChipInfo + Environment.NewLine
                    + $"Monitor version: {device.MonitorVersion}";
                _toolWindowService.ShowText("Chip Info", text);
            });
        }

        private async Task RunDeviceOperationAsync(string initialStage, Action<SamBaDevice> action)
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
                RaiseBusyCanExecuteChanged();

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
                AddLog($"Error: {ex.Message}");
                resultStatus = "Error";
                _dialogService.ShowError("Operation failed", ex.Message);
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

                RaiseBusyCanExecuteChanged();
            }
        }
        #endregion Device operations

        #region Helpers
        private bool CanInteract() => !IsBusy;

        private bool CanOperateOnDevice() => !IsBusy && SelectedDevice != null;

        private void RaiseBusyCanExecuteChanged()
        {
            RaiseDeviceCanExecuteChanged();
            OpenFirmwareCommand.RaiseCanExecuteChanged();
            SaveLogCommand.RaiseCanExecuteChanged();
            RefreshDevicesCommand.RaiseCanExecuteChanged();
        }

        private void RaiseDeviceCanExecuteChanged()
        {
            ReadCommand.RaiseCanExecuteChanged();
            WriteWordCommand.RaiseCanExecuteChanged();
            WriteFileCommand.RaiseCanExecuteChanged();
            LockAllCommand.RaiseCanExecuteChanged();
            UnlockAllCommand.RaiseCanExecuteChanged();
            DisplayLockRegionsCommand.RaiseCanExecuteChanged();
            GoCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            EraseCommand.RaiseCanExecuteChanged();
            ShowDeviceStatusCommand.RaiseCanExecuteChanged();
            BootFromFlashCommand.RaiseCanExecuteChanged();
            BootFromRomCommand.RaiseCanExecuteChanged();
            SetSecurityCommand.RaiseCanExecuteChanged();
            UpdateFirmwareCommand.RaiseCanExecuteChanged();
            ShowChipInfoCommand.RaiseCanExecuteChanged();
            SetReadAddressToFlashBaseCommand.RaiseCanExecuteChanged();
            SetWriteAddressToFlashBaseCommand.RaiseCanExecuteChanged();
            SetGoAddressToFlashBaseCommand.RaiseCanExecuteChanged();
        }

        private bool TryParseAddress(string hex, string operation, out uint address)
        {
            if (HexUtility.TryParse(hex, out address))
                return true;

            _dialogService.ShowError(operation, "Enter a valid hex memory address.");
            return false;
        }

        // The flash base of the selected device, once the chip has been identified (any
        // completed operation identifies it). Null until then.
        private string FlashBaseHexOrNull()
        {
            var info = SelectedDevice?.ChipInfo;
            return info?.FlashAddress.ToString("X8");
        }

        private static string DescribeOrFallback(Func<string> describe)
        {
            try
            {
                return describe();
            }
            catch (Exception ex)
            {
                return $"(unavailable: {ex.Message})";
            }
        }

        private static string DescribeBootSource(SamBaDevice device)
        {
            if (!device.ChipInfo.CanSelectBootSource)
                return "flash (fixed)";

            return device.GetBootSource() == SamBaChipBootSource.Flash ? "flash" : "ROM (SAM-BA monitor)";
        }

        // Compresses the locked-region list to ranges, e.g. "none" or "0-3, 8, 12-15".
        private static string FormatLockedRegions(IReadOnlyList<bool> regions)
        {
            var parts = new List<string>();
            int start = -1;

            for (int i = 0; i <= regions.Count; i++)
            {
                bool locked = i < regions.Count && regions[i];
                if (locked && start < 0)
                {
                    start = i;
                }
                else if (!locked && start >= 0)
                {
                    int end = i - 1;
                    parts.Add(start == end ? start.ToString() : $"{start}-{end}");
                    start = -1;
                }
            }

            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static string FormatUniqueId(IReadOnlyList<uint> words)
        {
            return string.Join(" ", words.Select(w => w.ToString("X8")));
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

        private void UpdateProgress(
            string message, string stage, bool indeterminate, long value = 0, long max = 100)
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

            FwEraseAll = s.FwEraseAll;
            FwVerify = s.FwVerify;
            FwOffsetHex = NormalizeOrDefault(s.FwOffsetHex);
            FwLockScope = s.FwLockScope;
            FwBootToFlash = s.FwBootToFlash;
            ResetAfterWrite = s.ResetAfterWrite;

            SelectedTabIndex = s.SelectedTabIndex;

            ReadAddressHex = NormalizeOrDefault(s.ReadAddressHex);
            ReadSizeText = (s.ReadSizeBytes <= 0 ? 1024 : s.ReadSizeBytes).ToString();
            IsWordRead = s.IsWordRead;
            WriteAddressHex = NormalizeOrDefault(s.WriteAddressHex);
            WriteWordHex = HexUtility.Normalize(s.WriteWordHex);
            GoAddressHex = NormalizeOrDefault(s.GoAddressHex);

            BytesPerLine = s.BytesPerLine == 4 || s.BytesPerLine == 8 ? s.BytesPerLine : 16;
            SaveWithAddress = s.SaveWithAddress;
            ShowAscii = s.ShowAscii;
            MemoryViewHeight = s.MemoryViewHeight;
            LogViewHeight = s.LogViewHeight;
        }

        private static string NormalizeOrDefault(string hex)
        {
            var normalized = HexUtility.Normalize(hex);
            return string.IsNullOrWhiteSpace(normalized) ? DefaultAddressHex : normalized;
        }

        private void SaveSettings()
        {
            try
            {
                var s = new AppSettings
                {
                    FirmwareFilePath = FirmwareFilePath,
                    IsWatcherEnabled = IsWatcherEnabled,
                    DeviceFilterMode = DeviceFilterMode,
                    CustomVendorIdHex = CustomVendorIdHex,
                    CustomProductIdHex = CustomProductIdHex,
                    IsSafeModeEnabled = IsSafeModeEnabled,
                    AutoUnlock = AutoUnlock,
                    ChipIdentificationMode = ChipIdentificationMode,
                    GeometryPrecedence = GeometryPrecedence,
                    MaxLogEntries = MaxLogEntries,

                    FwEraseAll = FwEraseAll,
                    FwVerify = FwVerify,
                    FwOffsetHex = FwOffsetHex,
                    FwLockScope = FwLockScope,
                    FwBootToFlash = FwBootToFlash,
                    ResetAfterWrite = ResetAfterWrite,

                    SelectedTabIndex = SelectedTabIndex,

                    ReadAddressHex = ReadAddressHex,
                    ReadSizeBytes = int.TryParse(ReadSizeText, out var n) ? n : 1024,
                    IsWordRead = IsWordRead,
                    WriteAddressHex = WriteAddressHex,
                    WriteWordHex = WriteWordHex,
                    GoAddressHex = GoAddressHex,

                    BytesPerLine = BytesPerLine,
                    SaveWithAddress = SaveWithAddress,
                    ShowAscii = ShowAscii,
                    MemoryViewHeight = MemoryViewHeight,
                    LogViewHeight = LogViewHeight,
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
                if (columnName == nameof(ReadAddressHex))
                    return HexUtility.TryParse(ReadAddressHex, out _) ? null : "Invalid hex address";

                if (columnName == nameof(WriteAddressHex))
                    return HexUtility.TryParse(WriteAddressHex, out _) ? null : "Invalid hex address";

                if (columnName == nameof(GoAddressHex))
                    return HexUtility.TryParse(GoAddressHex, out _) ? null : "Invalid hex address";

                if (columnName == nameof(FwOffsetHex))
                    return HexUtility.TryParse(FwOffsetHex, out _) ? null : "Invalid hex address";

                if (columnName == nameof(WriteWordHex))
                {
                    if (string.IsNullOrEmpty(WriteWordHex))
                        return null;

                    return HexUtility.TryParse(WriteWordHex, out _) ? null : "Invalid hex word";
                }

                if (columnName == nameof(ReadSizeText))
                {
                    if (!int.TryParse(ReadSizeText, out var size) || size <= 0)
                        return "Invalid size";

                    return null;
                }

                return null;
            }
        }
        #endregion IDataErrorInfo

        private void OnBytesPerLineChanged()
        {
            OnPropertyChanged(nameof(BytesPerLine4));
            OnPropertyChanged(nameof(BytesPerLine8));
            OnPropertyChanged(nameof(BytesPerLine16));
        }

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

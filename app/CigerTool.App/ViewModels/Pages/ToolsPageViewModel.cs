using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Models;

namespace CigerTool.App.ViewModels.Pages;

public sealed class ToolsPageViewModel : ViewModelBase
{
    private readonly IUsbCreationService _usbCreationService;
    private readonly IDiskInventoryService _diskInventoryService;
    private readonly IWindowsDeploymentService _windowsDeploymentService;
    private readonly AsyncRelayCommand _refreshDevicesCommand;
    private readonly AsyncRelayCommand _startCommand;
    private readonly RelayCommand _cancelCurrentOperationCommand;
    private UsbCreatorWorkspaceSnapshot _snapshot;
    private UsbDeviceEntry? _selectedDevice;
    private DiskSummary? _selectedTargetDisk;
    private OperationProgressSnapshot? _currentOperationProgress;
    private CancellationTokenSource? _operationCancellationTokenSource;
    private bool _isOperationRunning;
    private string _statusMessage;
    private InstallMediaModeOption _selectedMode;
    private IReadOnlyList<DiskSummary> _deploymentCandidates;
    private IReadOnlyList<WindowsImageEditionOption> _availableEditions;
    private WindowsImageEditionOption? _selectedEdition;
    private bool _portableModeEnabled;

    public ToolsPageViewModel(
        IUsbCreationService usbCreationService,
        IDiskInventoryService diskInventoryService,
        IWindowsDeploymentService windowsDeploymentService)
    {
        _usbCreationService = usbCreationService;
        _diskInventoryService = diskInventoryService;
        _windowsDeploymentService = windowsDeploymentService;
        _snapshot = usbCreationService.GetSnapshot();
        _selectedDevice = _snapshot.Devices.FirstOrDefault(device => device.CanWrite);
        _deploymentCandidates = BuildDeploymentCandidates(diskInventoryService.GetCurrentDisks());
        _selectedTargetDisk = _deploymentCandidates.FirstOrDefault();
        _availableEditions = [];
        _selectedMode = Modes[0];
        _statusMessage = "Kurulum medyası kaynağı ve hedef aygıtlar hazırlanıyor.";

        BrowseImageCommand = new AsyncRelayCommand(_ => BrowseImageAsync(), _ => !IsOperationRunning);
        ClearImageCommand = new AsyncRelayCommand(_ => ClearImageAsync(), _ => HasPreparedImage && !IsOperationRunning);
        _refreshDevicesCommand = new AsyncRelayCommand(_ => RefreshTargetsAsync(), _ => !IsOperationRunning);
        _startCommand = new AsyncRelayCommand(_ => StartAsync(), _ => CanStart);
        _cancelCurrentOperationCommand = new RelayCommand(_ => CancelCurrentOperation(), _ => IsOperationRunning);

        _ = WarmUpAsync();
    }

    public IReadOnlyList<InstallMediaModeOption> Modes { get; } =
    [
        new("usb", "USB kurulum medyası", "ISO veya disk imajını USB belleğe önyüklenebilir şekilde hazırlar."),
        new("disk", "Doğrudan diske kur", "Windows imajını doğrudan seçilen hedef diske yerleştirir."),
        new("portable", "Taşınabilir Windows", "Windows imajını USB/SATA diske taşınabilir düzenle uygular.")
    ];

    public UsbCreatorWorkspaceSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public InstallMediaModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            SetProperty(ref _selectedMode, value);
            RaiseDerivedStateChanged();
        }
    }

    public UsbDeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            SetProperty(ref _selectedDevice, value);
            RaiseDerivedStateChanged();
        }
    }

    public IReadOnlyList<DiskSummary> DeploymentCandidates
    {
        get => _deploymentCandidates;
        private set => SetProperty(ref _deploymentCandidates, value);
    }

    public DiskSummary? SelectedTargetDisk
    {
        get => _selectedTargetDisk;
        set
        {
            SetProperty(ref _selectedTargetDisk, value);
            RaiseDerivedStateChanged();
        }
    }

    public IReadOnlyList<WindowsImageEditionOption> AvailableEditions
    {
        get => _availableEditions;
        private set => SetProperty(ref _availableEditions, value);
    }

    public WindowsImageEditionOption? SelectedEdition
    {
        get => _selectedEdition;
        set
        {
            SetProperty(ref _selectedEdition, value);
            RaiseDerivedStateChanged();
        }
    }

    public bool PortableModeEnabled
    {
        get => _portableModeEnabled;
        set
        {
            SetProperty(ref _portableModeEnabled, value);
            RaiseDerivedStateChanged();
        }
    }

    public OperationProgressSnapshot? CurrentOperationProgress
    {
        get => _currentOperationProgress;
        private set
        {
            SetProperty(ref _currentOperationProgress, value);
            RaiseDerivedStateChanged();
        }
    }

    public bool IsOperationRunning
    {
        get => _isOperationRunning;
        private set
        {
            SetProperty(ref _isOperationRunning, value);
            RaiseDerivedStateChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasPreparedImage =>
        !string.IsNullOrWhiteSpace(Snapshot.Release.PreparedImagePath) &&
        File.Exists(Snapshot.Release.PreparedImagePath);

    public bool IsWindowsSource =>
        Snapshot.Release.BootProfileLabel.Contains("Windows", StringComparison.OrdinalIgnoreCase);

    public bool ShowUsbTargetSelector => SelectedMode.Id == "usb";

    public bool ShowDiskTargetSelector => SelectedMode.Id is "disk" or "portable";

    public bool ShowEditionSelector => ShowDiskTargetSelector && AvailableEditions.Count > 0;

    public bool CanStart =>
        !IsOperationRunning &&
        HasPreparedImage &&
        Snapshot.Release.CanDirectWrite &&
        (ShowUsbTargetSelector
            ? SelectedDevice?.CanWrite == true
            : SelectedTargetDisk is not null && (IsWindowsSource || string.Equals(Path.GetExtension(Snapshot.Release.PreparedImagePath), ".wim", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(Snapshot.Release.PreparedImagePath), ".esd", StringComparison.OrdinalIgnoreCase)));

    public string TargetHint =>
        ShowUsbTargetSelector
            ? "Kurulum USB'si, canlı Linux USB'si, WinPE ortamı ve araç ISO'ları bu modda hazırlanır."
            : "Bu mod Windows kaynağını doğrudan hedef diske uygular. Seçilen diskteki tüm veriler silinir.";

    public string SourceWorkflowHint =>
        ShowUsbTargetSelector
            ? "Windows, Linux, WinPE ve araç ISO'ları için doğrudan USB hazırlama akışı kullanılır."
            : "Windows ISO veya WIM/ESD kaynağı gereki̇r. Kurulumdan önce hedef disk temizlenir ve yeniden bölümlendirilir.";

    public double ProgressPercent => CurrentOperationProgress?.Percent ?? 0;
    public bool IsProgressIndeterminate => CurrentOperationProgress?.IsIndeterminate ?? false;
    public string ProgressSummary => CurrentOperationProgress?.Summary ?? "Henüz çalışan bir kurulum medyası işlemi yok.";
    public string ProgressDetail => CurrentOperationProgress is null
        ? "Kaynak analizi, yazma veya kurulum adımları burada görünür."
        : $"{CurrentOperationProgress.ProcessedLabel} / {CurrentOperationProgress.TotalLabel} · {CurrentOperationProgress.SpeedLabel} · Kalan {CurrentOperationProgress.RemainingLabel}";

    public ICommand BrowseImageCommand { get; }
    public ICommand ClearImageCommand { get; }
    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;
    public ICommand StartCommand => _startCommand;
    public ICommand CancelCurrentOperationCommand => _cancelCurrentOperationCommand;

    private async Task WarmUpAsync()
    {
        await RefreshTargetsAsync();
        StatusMessage = "Kurulum medyası bölümü hazır.";
    }

    private async Task BrowseImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ISO, WIM, ESD veya disk imajı seçin",
            Filter = "Desteklenen kaynaklar|*.iso;*.wim;*.esd;*.img;*.bin;*.raw|Tüm dosyalar|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Dosya seçimi iptal edildi.";
            return;
        }

        _usbCreationService.SetManualImagePath(dialog.FileName);
        await _usbCreationService.RefreshReleaseInfoAsync();
        RefreshSnapshot();
        await RefreshEditionsAsync();

        StatusMessage = Snapshot.Release.CanDirectWrite
            ? "Kaynak analiz edildi. İşlem türünü ve hedefi seçip devam edebilirsiniz."
            : Snapshot.Release.CompatibilityDetails;
    }

    private async Task ClearImageAsync()
    {
        _usbCreationService.ClearManualImageSelection();
        await _usbCreationService.RefreshReleaseInfoAsync();
        RefreshSnapshot();
        AvailableEditions = [];
        SelectedEdition = null;
        StatusMessage = "Seçilen kaynak temizlendi.";
    }

    private async Task RefreshTargetsAsync()
    {
        var usbResult = await _usbCreationService.RefreshUsbDevicesAsync();
        RefreshSnapshot();
        DeploymentCandidates = BuildDeploymentCandidates(_diskInventoryService.GetCurrentDisks());
        SelectedTargetDisk = DeploymentCandidates.FirstOrDefault(disk => disk.DiskNumber == SelectedTargetDisk?.DiskNumber) ?? DeploymentCandidates.FirstOrDefault();
        StatusMessage = usbResult.Message;
    }

    private async Task RefreshEditionsAsync()
    {
        AvailableEditions = [];
        SelectedEdition = null;

        if (!HasPreparedImage || string.IsNullOrWhiteSpace(Snapshot.Release.PreparedImagePath))
        {
            return;
        }

        if (!IsWindowsSource &&
            !string.Equals(Path.GetExtension(Snapshot.Release.PreparedImagePath), ".wim", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(Snapshot.Release.PreparedImagePath), ".esd", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var editions = await _windowsDeploymentService.GetAvailableEditionsAsync(Snapshot.Release.PreparedImagePath);
            AvailableEditions = editions;
            SelectedEdition = editions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Windows sürümleri listelenemedi: {ex.Message}";
        }
    }

    private async Task StartAsync()
    {
        if (!CanStart)
        {
            StatusMessage = "Önce kaynak, işlem türü ve hedef seçimini tamamlayın.";
            return;
        }

        IsOperationRunning = true;
        CurrentOperationProgress = null;
        _operationCancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<OperationProgressSnapshot>(snapshot => CurrentOperationProgress = snapshot);

        try
        {
            UsbCreatorOperationResult result;

            if (ShowUsbTargetSelector)
            {
                if (MessageBox.Show(
                        $"Seçilen USB aygıtındaki tüm veriler silinecek.\n\nKaynak: {Snapshot.Release.ImageName}\nHedef: {SelectedDevice!.DisplayName}\n\nDevam etmek istiyor musunuz?",
                        "USB hazırlama onayı",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    StatusMessage = "İşlem iptal edildi.";
                    return;
                }

                var verify = await _usbCreationService.VerifyPreparedImageAsync(progress, _operationCancellationTokenSource.Token);
                RefreshSnapshot();
                if (!verify.Succeeded)
                {
                    StatusMessage = verify.Message;
                    return;
                }

                result = await _usbCreationService.WriteImageAsync(SelectedDevice!.Id, true, progress, _operationCancellationTokenSource.Token);
            }
            else
            {
                if (MessageBox.Show(
                        $"Seçilen hedef diskteki tüm veriler silinecek.\n\nKaynak: {Snapshot.Release.ImageName}\nHedef disk: {SelectedTargetDisk!.DeviceModel}\n\nDevam etmek istiyor musunuz?",
                        "Doğrudan kurulum onayı",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    StatusMessage = "İşlem iptal edildi.";
                    return;
                }

                var imageIndex = SelectedEdition?.Index ?? 1;
                result = await _windowsDeploymentService.DeployToDiskAsync(
                    Snapshot.Release.PreparedImagePath!,
                    SelectedTargetDisk!.DiskNumber,
                    imageIndex,
                    SelectedMode.Id == "portable",
                    progress,
                    _operationCancellationTokenSource.Token);
            }

            RefreshSnapshot();
            StatusMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "İşlem iptal edildi.";
        }
        finally
        {
            IsOperationRunning = false;
            _operationCancellationTokenSource?.Dispose();
            _operationCancellationTokenSource = null;
            RaiseDerivedStateChanged();
        }
    }

    private void CancelCurrentOperation()
    {
        _operationCancellationTokenSource?.Cancel();
        StatusMessage = "İptal isteği gönderildi.";
    }

    private void RefreshSnapshot()
    {
        var previousDeviceId = SelectedDevice?.Id;
        Snapshot = _usbCreationService.GetSnapshot();
        SelectedDevice = Snapshot.Devices.FirstOrDefault(device => device.Id == previousDeviceId)
                         ?? Snapshot.Devices.FirstOrDefault(device => device.CanWrite)
                         ?? Snapshot.Devices.FirstOrDefault();
        RaiseDerivedStateChanged();
    }

    private static IReadOnlyList<DiskSummary> BuildDeploymentCandidates(IReadOnlyList<DiskSummary> disks)
    {
        return disks
            .Where(disk => !disk.IsSystemVolume)
            .GroupBy(disk => disk.DiskNumber)
            .Select(group => group.First())
            .OrderBy(disk => disk.IsRemovable)
            .ThenBy(disk => disk.DiskNumber)
            .ToArray();
    }

    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(HasPreparedImage));
        RaisePropertyChanged(nameof(IsWindowsSource));
        RaisePropertyChanged(nameof(ShowUsbTargetSelector));
        RaisePropertyChanged(nameof(ShowDiskTargetSelector));
        RaisePropertyChanged(nameof(ShowEditionSelector));
        RaisePropertyChanged(nameof(CanStart));
        RaisePropertyChanged(nameof(TargetHint));
        RaisePropertyChanged(nameof(SourceWorkflowHint));
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
        RaisePropertyChanged(nameof(ProgressSummary));
        RaisePropertyChanged(nameof(ProgressDetail));
        _refreshDevicesCommand.RaiseCanExecuteChanged();
        _startCommand.RaiseCanExecuteChanged();
        _cancelCurrentOperationCommand.RaiseCanExecuteChanged();
        (BrowseImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed record InstallMediaModeOption(
    string Id,
    string Title,
    string Description);

using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;

namespace CigerTool.App.ViewModels.Pages;

public sealed class UsbCreatorPageViewModel : ViewModelBase
{
    private readonly IUsbCreationService _usbCreationService;
    private readonly AsyncRelayCommand _refreshReleaseCommand;
    private readonly AsyncRelayCommand _refreshDevicesCommand;
    private readonly AsyncRelayCommand _downloadOnlyCommand;
    private readonly AsyncRelayCommand _downloadAndWriteCommand;
    private readonly AsyncRelayCommand _writePreparedImageCommand;
    private readonly RelayCommand _cancelCurrentOperationCommand;
    private UsbCreatorWorkspaceSnapshot _snapshot;
    private UsbDeviceEntry? _selectedDevice;
    private OperationProgressSnapshot? _currentOperationProgress;
    private CancellationTokenSource? _operationCancellationTokenSource;
    private bool _isOperationRunning;
    private string _statusMessage;

    public UsbCreatorPageViewModel(IUsbCreationService usbCreationService)
    {
        _usbCreationService = usbCreationService;
        _snapshot = usbCreationService.GetSnapshot();
        _selectedDevice = _snapshot.Devices.FirstOrDefault(device => device.CanWrite);
        _statusMessage = "Ortam kaynağı ve USB aygıtları hazırlanıyor.";

        _refreshReleaseCommand = new AsyncRelayCommand(_ => RefreshReleaseAsync(), _ => !IsOperationRunning);
        _refreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync(), _ => !IsOperationRunning);
        _downloadOnlyCommand = new AsyncRelayCommand(_ => DownloadOnlyAsync(), _ => CanDownloadOnly);
        _downloadAndWriteCommand = new AsyncRelayCommand(_ => DownloadAndWriteAsync(), _ => CanDownloadAndWrite);
        _writePreparedImageCommand = new AsyncRelayCommand(_ => WritePreparedImageAsync(), _ => CanWritePreparedImage);
        _cancelCurrentOperationCommand = new RelayCommand(_ => CancelCurrentOperation(), _ => CanCancelCurrentOperation);

        BrowseManualImageCommand = new AsyncRelayCommand(_ => BrowseManualImageAsync(), _ => !IsOperationRunning);
        ClearManualImageCommand = new AsyncRelayCommand(_ => ClearManualImageAsync(), _ => CanClearManualImage);

        _ = WarmUpAsync();
    }

    public UsbCreatorWorkspaceSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
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

    public bool HasUsbDevices => Snapshot.Devices.Count > 0;

    public bool HasPreparedImage =>
        !string.IsNullOrWhiteSpace(Snapshot.Release.PreparedImagePath) &&
        File.Exists(Snapshot.Release.PreparedImagePath);

    public bool HasRemoteImageSource => !string.IsNullOrWhiteSpace(Snapshot.Release.ImageUrl);

    public bool CanClearManualImage =>
        !IsOperationRunning &&
        string.Equals(Snapshot.Release.ModeLabel, "Elle seçilen dosya", StringComparison.OrdinalIgnoreCase);

    public bool CanDownloadOnly => !IsOperationRunning && HasRemoteImageSource;

    public bool CanDownloadAndWrite =>
        !IsOperationRunning &&
        Snapshot.IsAdministrator &&
        SelectedDevice?.CanWrite == true &&
        ((HasPreparedImage && Snapshot.Release.CanDirectWrite) || HasRemoteImageSource);

    public bool CanWritePreparedImage =>
        !IsOperationRunning &&
        Snapshot.IsAdministrator &&
        SelectedDevice?.CanWrite == true &&
        HasPreparedImage &&
        Snapshot.Release.CanDirectWrite;

    public bool CanCancelCurrentOperation => IsOperationRunning;

    public string SourceStatusLabel =>
        HasPreparedImage
            ? Snapshot.Release.CanDirectWrite
                ? "İmaj hazır"
                : "Ek hazırlık gerekir"
            : HasRemoteImageSource
                ? "İndirilmeyi bekliyor"
                : "Elle dosya seçin";

    public string SourceStatusMessage
    {
        get
        {
            if (HasPreparedImage)
            {
                return Snapshot.Release.CompatibilityDetails;
            }

            return Snapshot.Release.Status;
        }
    }

    public string SelectedDeviceTitle =>
        SelectedDevice?.DisplayName ?? "Henüz bir USB aygıtı seçilmedi.";

    public string SelectedDeviceMessage
    {
        get
        {
            if (SelectedDevice is null)
            {
                return HasUsbDevices
                    ? "Listeden hedef USB aygıtını seçin. Uygunluk otomatik olarak denetlenir."
                    : "Takılı USB aygıtı bulunamadı. Aygıtı bağlayıp listeyi yenileyin.";
            }

            return SelectedDevice.CanWrite
                ? "Seçilen aygıt yazmaya uygun görünüyor."
                : SelectedDevice.SafetyStatus;
        }
    }

    public string SelectedDeviceDetail
    {
        get
        {
            if (SelectedDevice is null)
            {
                return "Model, bağlı sürücü harfleri ve uygunluk durumu burada gösterilir.";
            }

            return $"{SelectedDevice.Model} · {SelectedDevice.SizeLabel} · {SelectedDevice.MountedVolumesLabel}";
        }
    }

    public string UsbDiscoveryHint =>
        HasUsbDevices
            ? "Hedef aygıt seçildiğinde boyut ve güvenlik uygunluğu otomatik olarak denetlenir."
            : "USB aygıtı görünmüyorsa bağlantıyı kontrol edin ve listeyi yenileyin.";

    public double ProgressPercent => CurrentOperationProgress?.Percent ?? 0;

    public bool IsProgressIndeterminate => CurrentOperationProgress?.IsIndeterminate ?? false;

    public string ProgressSummary =>
        CurrentOperationProgress?.Summary ?? "Henüz çalışan bir USB hazırlama işlemi yok.";

    public string ProgressDetail
    {
        get
        {
            if (CurrentOperationProgress is null)
            {
                return "İndirme, yazma ve doğrulama durumları burada gösterilir.";
            }

            return $"{CurrentOperationProgress.ProcessedLabel} / {CurrentOperationProgress.TotalLabel} · {CurrentOperationProgress.SpeedLabel} · Kalan {CurrentOperationProgress.RemainingLabel}";
        }
    }

    public ICommand RefreshReleaseCommand => _refreshReleaseCommand;

    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;

    public ICommand DownloadOnlyCommand => _downloadOnlyCommand;

    public ICommand DownloadAndWriteCommand => _downloadAndWriteCommand;

    public ICommand WritePreparedImageCommand => _writePreparedImageCommand;

    public ICommand CancelCurrentOperationCommand => _cancelCurrentOperationCommand;

    public ICommand BrowseManualImageCommand { get; }

    public ICommand ClearManualImageCommand { get; }

    private async Task WarmUpAsync()
    {
        try
        {
            await RefreshReleaseAsync();
            await RefreshDevicesAsync();
            StatusMessage = "Ortam kaynağı ve USB aygıtları hazır.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Açılış denetimi tamamlanamadı: {ex.Message}";
        }
    }

    private async Task RefreshReleaseAsync()
    {
        var result = await _usbCreationService.RefreshReleaseInfoAsync();
        RefreshSnapshot();
        StatusMessage = result.Message;
    }

    private async Task RefreshDevicesAsync()
    {
        var result = await _usbCreationService.RefreshUsbDevicesAsync();
        RefreshSnapshot();
        StatusMessage = result.Message;
    }

    private async Task BrowseManualImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "CigerTool OS imajını seçin",
            Filter = "Desteklenen imajlar|*.img;*.iso;*.bin;*.raw;*.wim|Tüm dosyalar|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Elle dosya seçimi iptal edildi.";
            return;
        }

        var setResult = _usbCreationService.SetManualImagePath(dialog.FileName);
        StatusMessage = setResult.Message;
        await RefreshReleaseAsync();
    }

    private async Task ClearManualImageAsync()
    {
        var clearResult = _usbCreationService.ClearManualImageSelection();
        StatusMessage = clearResult.Message;
        await RefreshReleaseAsync();
    }

    private async Task DownloadOnlyAsync()
    {
        if (!HasRemoteImageSource)
        {
            StatusMessage = "Bu kaynakta indirilecek bir imaj adresi bulunmuyor.";
            return;
        }

        await ExecuteOperationAsync(
            "İmaj indiriliyor",
            progress => _usbCreationService.DownloadImageAsync(progress, _operationCancellationTokenSource!.Token));
    }

    private async Task WritePreparedImageAsync()
    {
        if (!EnsureWritableDeviceSelected())
        {
            return;
        }

        if (!HasPreparedImage)
        {
            StatusMessage = "Önce hazır bir imaj seçin veya indirin.";
            return;
        }

        if (!Snapshot.Release.CanDirectWrite)
        {
            StatusMessage = Snapshot.Release.CompatibilityDetails;
            return;
        }

        if (!ConfirmWrite())
        {
            StatusMessage = "USB yazma işlemi iptal edildi.";
            return;
        }

        await ExecuteOperationAsync(
            "USB'ye yazılıyor",
            async progress =>
            {
                SetManualProgress("Bütünlük doğrulanıyor", "Hazırlanan imaj dosyası kontrol ediliyor.", isIndeterminate: true);
                var verify = await _usbCreationService.VerifyPreparedImageAsync(progress, _operationCancellationTokenSource!.Token);
                RefreshSnapshot();
                if (!verify.Succeeded)
                {
                    return verify;
                }

                return await _usbCreationService.WriteImageAsync(
                    SelectedDevice!.Id,
                    confirmedByUser: true,
                    progress,
                    _operationCancellationTokenSource!.Token);
            });
    }

    private async Task DownloadAndWriteAsync()
    {
        if (!EnsureWritableDeviceSelected())
        {
            return;
        }

        if (!ConfirmWrite())
        {
            StatusMessage = "USB yazma işlemi iptal edildi.";
            return;
        }

        await ExecuteOperationAsync(
            "Ortam hazırlanıyor",
            async progress =>
            {
                if (!HasPreparedImage)
                {
                    var refresh = await _usbCreationService.RefreshReleaseInfoAsync(_operationCancellationTokenSource!.Token);
                    RefreshSnapshot();
                    if (!refresh.Succeeded && !HasPreparedImage)
                    {
                        return refresh;
                    }
                }

                if (!HasPreparedImage)
                {
                    var download = await _usbCreationService.DownloadImageAsync(progress, _operationCancellationTokenSource!.Token);
                    RefreshSnapshot();
                    if (!download.Succeeded)
                    {
                        return download;
                    }
                }

                if (!Snapshot.Release.CanDirectWrite)
                {
                    return new UsbCreatorOperationResult(false, OperationSeverity.Warning, Snapshot.Release.CompatibilityDetails);
                }

                SetManualProgress("Bütünlük doğrulanıyor", "Hazırlanan imaj dosyası kontrol ediliyor.", isIndeterminate: true);
                var verify = await _usbCreationService.VerifyPreparedImageAsync(progress, _operationCancellationTokenSource!.Token);
                RefreshSnapshot();
                if (!verify.Succeeded)
                {
                    return verify;
                }

                return await _usbCreationService.WriteImageAsync(
                    SelectedDevice!.Id,
                    confirmedByUser: true,
                    progress,
                    _operationCancellationTokenSource!.Token);
            });
    }

    private async Task ExecuteOperationAsync(
        string initialStatus,
        Func<IProgress<OperationProgressSnapshot>, Task<UsbCreatorOperationResult>> operation)
    {
        IsOperationRunning = true;
        CurrentOperationProgress = null;
        StatusMessage = initialStatus;
        _operationCancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<OperationProgressSnapshot>(snapshot => CurrentOperationProgress = snapshot);

        try
        {
            var result = await operation(progress);
            RefreshSnapshot();
            StatusMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "İşlem iptal edildi.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"İşlem tamamlanamadı: {ex.Message}";
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

    private bool EnsureWritableDeviceSelected()
    {
        if (SelectedDevice is null)
        {
            StatusMessage = "Önce hedef USB aygıtını seçin.";
            return false;
        }

        if (!SelectedDevice.CanWrite)
        {
            StatusMessage = SelectedDevice.SafetyStatus;
            return false;
        }

        if (!Snapshot.IsAdministrator)
        {
            StatusMessage = "USB yazmak için uygulama yönetici olarak çalışmalıdır.";
            return false;
        }

        return true;
    }

    private bool ConfirmWrite()
    {
        if (SelectedDevice is null)
        {
            return false;
        }

        var message =
            $"Seçilen USB aygıtındaki tüm veriler silinecek.\n\nAygıt: {SelectedDevice.DisplayName}\nSürücü harfleri: {SelectedDevice.MountedVolumesLabel}\n\nDevam etmek istiyor musunuz?";

        return MessageBox.Show(
                   message,
                   "USB yazma onayı",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void RefreshSnapshot()
    {
        var previousId = SelectedDevice?.Id;
        Snapshot = _usbCreationService.GetSnapshot();
        SelectedDevice = Snapshot.Devices.FirstOrDefault(device => device.Id == previousId)
                         ?? Snapshot.Devices.FirstOrDefault(device => device.CanWrite)
                         ?? Snapshot.Devices.FirstOrDefault();
        RaisePropertyChanged(nameof(HasUsbDevices));
        RaisePropertyChanged(nameof(HasPreparedImage));
        RaisePropertyChanged(nameof(HasRemoteImageSource));
        RaiseDerivedStateChanged();
    }

    private void SetManualProgress(string phaseLabel, string summary, bool isIndeterminate)
    {
        CurrentOperationProgress = new OperationProgressSnapshot(
            phaseLabel,
            summary,
            0,
            isIndeterminate,
            0,
            0,
            "0 B",
            "Bilinmiyor",
            "Hazırlanıyor",
            "Hesaplanıyor",
            null);
    }

    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(CanClearManualImage));
        RaisePropertyChanged(nameof(CanDownloadOnly));
        RaisePropertyChanged(nameof(CanDownloadAndWrite));
        RaisePropertyChanged(nameof(CanWritePreparedImage));
        RaisePropertyChanged(nameof(CanCancelCurrentOperation));
        RaisePropertyChanged(nameof(SourceStatusLabel));
        RaisePropertyChanged(nameof(SourceStatusMessage));
        RaisePropertyChanged(nameof(SelectedDeviceTitle));
        RaisePropertyChanged(nameof(SelectedDeviceMessage));
        RaisePropertyChanged(nameof(SelectedDeviceDetail));
        RaisePropertyChanged(nameof(UsbDiscoveryHint));
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
        RaisePropertyChanged(nameof(ProgressSummary));
        RaisePropertyChanged(nameof(ProgressDetail));
        _refreshReleaseCommand.RaiseCanExecuteChanged();
        _refreshDevicesCommand.RaiseCanExecuteChanged();
        _downloadOnlyCommand.RaiseCanExecuteChanged();
        _downloadAndWriteCommand.RaiseCanExecuteChanged();
        _writePreparedImageCommand.RaiseCanExecuteChanged();
        _cancelCurrentOperationCommand.RaiseCanExecuteChanged();
        (BrowseManualImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearManualImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

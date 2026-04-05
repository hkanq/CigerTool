using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;

namespace CigerTool.App.ViewModels.Pages;

public sealed class ToolsPageViewModel : ViewModelBase
{
    private readonly IUsbCreationService _usbCreationService;
    private readonly AsyncRelayCommand _refreshDevicesCommand;
    private readonly AsyncRelayCommand _writePreparedImageCommand;
    private readonly RelayCommand _cancelCurrentOperationCommand;
    private UsbCreatorWorkspaceSnapshot _snapshot;
    private UsbDeviceEntry? _selectedDevice;
    private OperationProgressSnapshot? _currentOperationProgress;
    private CancellationTokenSource? _operationCancellationTokenSource;
    private bool _isOperationRunning;
    private string _statusMessage;

    public ToolsPageViewModel(IUsbCreationService usbCreationService)
    {
        _usbCreationService = usbCreationService;
        _snapshot = usbCreationService.GetSnapshot();
        _selectedDevice = _snapshot.Devices.FirstOrDefault(device => device.CanWrite);
        _statusMessage = "Kurulum medyası kaynağı ve hedef USB aygıtları hazırlanıyor.";

        BrowseImageCommand = new AsyncRelayCommand(_ => BrowseImageAsync(), _ => !IsOperationRunning);
        ClearImageCommand = new AsyncRelayCommand(_ => ClearImageAsync(), _ => HasPreparedImage && !IsOperationRunning);
        _refreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync(), _ => !IsOperationRunning);
        _writePreparedImageCommand = new AsyncRelayCommand(_ => WritePreparedImageAsync(), _ => CanWritePreparedImage);
        _cancelCurrentOperationCommand = new RelayCommand(_ => CancelCurrentOperation(), _ => IsOperationRunning);

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

    public bool HasPreparedImage =>
        !string.IsNullOrWhiteSpace(Snapshot.Release.PreparedImagePath) &&
        File.Exists(Snapshot.Release.PreparedImagePath);

    public bool IsInstallMediaSource =>
        HasPreparedImage &&
        !Snapshot.Release.BootProfileLabel.Contains("CigerTool", StringComparison.OrdinalIgnoreCase);

    public bool CanWritePreparedImage =>
        !IsOperationRunning &&
        Snapshot.IsAdministrator &&
        SelectedDevice?.CanWrite == true &&
        HasPreparedImage &&
        Snapshot.Release.CanDirectWrite;

    public string SelectedDeviceTitle =>
        SelectedDevice?.DisplayName ?? "Henüz bir hedef USB aygıtı seçilmedi.";

    public string SelectedDeviceMessage =>
        SelectedDevice is null
            ? "Listeden hedef USB aygıtını seçin. Uygunluk otomatik olarak denetlenir."
            : SelectedDevice.CanWrite
                ? "Seçilen aygıt yazmaya uygun görünüyor."
                : SelectedDevice.SafetyStatus;

    public double ProgressPercent => CurrentOperationProgress?.Percent ?? 0;

    public bool IsProgressIndeterminate => CurrentOperationProgress?.IsIndeterminate ?? false;

    public string ProgressSummary =>
        CurrentOperationProgress?.Summary ?? "Henüz çalışan bir kurulum medyası hazırlama işlemi yok.";

    public string ProgressDetail
    {
        get
        {
            if (CurrentOperationProgress is null)
            {
                return "İndirme yerine doğrudan seçilen ISO veya disk imajı kullanılır.";
            }

            return $"{CurrentOperationProgress.ProcessedLabel} / {CurrentOperationProgress.TotalLabel} · {CurrentOperationProgress.SpeedLabel} · Kalan {CurrentOperationProgress.RemainingLabel}";
        }
    }

    public ICommand BrowseImageCommand { get; }

    public ICommand ClearImageCommand { get; }

    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;

    public ICommand WritePreparedImageCommand => _writePreparedImageCommand;

    public ICommand CancelCurrentOperationCommand => _cancelCurrentOperationCommand;

    private async Task WarmUpAsync()
    {
        try
        {
            await RefreshDevicesAsync();
            StatusMessage = "Kurulum medyası bölümü hazır.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Kurulum medyası bölümü hazırlanamadı: {ex.Message}";
        }
    }

    private async Task BrowseImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ISO veya disk imajı seçin",
            Filter = "Desteklenen kaynaklar|*.iso;*.img;*.bin;*.raw|Tüm dosyalar|*.*",
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

        StatusMessage = Snapshot.Release.CanDirectWrite
            ? "Kaynak analiz edildi. Hedef USB seçip yazma işlemine geçebilirsiniz."
            : Snapshot.Release.CompatibilityDetails;
    }

    private async Task ClearImageAsync()
    {
        _usbCreationService.ClearManualImageSelection();
        await _usbCreationService.RefreshReleaseInfoAsync();
        RefreshSnapshot();
        StatusMessage = "Seçilen kaynak temizlendi.";
    }

    private async Task RefreshDevicesAsync()
    {
        var result = await _usbCreationService.RefreshUsbDevicesAsync();
        RefreshSnapshot();
        StatusMessage = result.Message;
    }

    private async Task WritePreparedImageAsync()
    {
        if (SelectedDevice is null)
        {
            StatusMessage = "Önce hedef USB aygıtını seçin.";
            return;
        }

        if (!SelectedDevice.CanWrite)
        {
            StatusMessage = SelectedDevice.SafetyStatus;
            return;
        }

        if (!Snapshot.IsAdministrator)
        {
            StatusMessage = "Kurulum USB'si yazmak için uygulama yönetici olarak çalışmalıdır.";
            return;
        }

        if (!HasPreparedImage)
        {
            StatusMessage = "Önce ISO veya disk imajı seçin.";
            return;
        }

        if (!Snapshot.Release.CanDirectWrite)
        {
            StatusMessage = Snapshot.Release.CompatibilityDetails;
            return;
        }

        var message =
            $"Seçilen USB aygıtındaki tüm veriler silinecek.\n\nKaynak: {Snapshot.Release.ImageName}\nHedef: {SelectedDevice.DisplayName}\n\nDevam etmek istiyor musunuz?";

        if (MessageBox.Show(message, "Kurulum medyası onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            StatusMessage = "Kurulum medyası yazma işlemi iptal edildi.";
            return;
        }

        IsOperationRunning = true;
        CurrentOperationProgress = null;
        _operationCancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<OperationProgressSnapshot>(snapshot => CurrentOperationProgress = snapshot);

        try
        {
            var verify = await _usbCreationService.VerifyPreparedImageAsync(progress, _operationCancellationTokenSource.Token);
            RefreshSnapshot();
            if (!verify.Succeeded)
            {
                StatusMessage = verify.Message;
                return;
            }

            var result = await _usbCreationService.WriteImageAsync(SelectedDevice.Id, true, progress, _operationCancellationTokenSource.Token);
            RefreshSnapshot();
            StatusMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Kurulum medyası yazma işlemi iptal edildi.";
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
        var previousId = SelectedDevice?.Id;
        Snapshot = _usbCreationService.GetSnapshot();
        SelectedDevice = Snapshot.Devices.FirstOrDefault(device => device.Id == previousId)
                         ?? Snapshot.Devices.FirstOrDefault(device => device.CanWrite)
                         ?? Snapshot.Devices.FirstOrDefault();
        RaiseDerivedStateChanged();
    }

    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(HasPreparedImage));
        RaisePropertyChanged(nameof(IsInstallMediaSource));
        RaisePropertyChanged(nameof(CanWritePreparedImage));
        RaisePropertyChanged(nameof(SelectedDeviceTitle));
        RaisePropertyChanged(nameof(SelectedDeviceMessage));
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
        RaisePropertyChanged(nameof(ProgressSummary));
        RaisePropertyChanged(nameof(ProgressDetail));
        _refreshDevicesCommand.RaiseCanExecuteChanged();
        _writePreparedImageCommand.RaiseCanExecuteChanged();
        _cancelCurrentOperationCommand.RaiseCanExecuteChanged();
        (BrowseImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;

namespace CigerTool.App.ViewModels.Pages;

public sealed class UsbCreatorPageViewModel : ViewModelBase
{
    private readonly IUsbCreationService _usbCreationService;
    private readonly AsyncRelayCommand _refreshReleaseCommand;
    private readonly AsyncRelayCommand _refreshDevicesCommand;
    private readonly AsyncRelayCommand _downloadImageCommand;
    private readonly AsyncRelayCommand _verifyImageCommand;
    private readonly AsyncRelayCommand _writeImageCommand;
    private UsbCreatorWorkspaceSnapshot _snapshot;
    private UsbDeviceEntry? _selectedDevice;
    private bool _confirmDestructiveWrite;
    private string _statusMessage;

    public UsbCreatorPageViewModel(IUsbCreationService usbCreationService)
    {
        _usbCreationService = usbCreationService;
        _snapshot = usbCreationService.GetSnapshot();
        _selectedDevice = _snapshot.Devices.FirstOrDefault(device => device.CanWrite);
        _statusMessage = _snapshot.ReleaseSourceStatus;
        _refreshReleaseCommand = new AsyncRelayCommand(_ => ExecuteOperationAsync(() => _usbCreationService.RefreshReleaseInfoAsync(), "Kurulum kaynağı bilgisi yenilendi."));
        _refreshDevicesCommand = new AsyncRelayCommand(_ => ExecuteOperationAsync(() => _usbCreationService.RefreshUsbDevicesAsync(), "USB aygıt listesi yenilendi."));
        _downloadImageCommand = new AsyncRelayCommand(_ => ExecuteOperationAsync(() => _usbCreationService.DownloadImageAsync(), "İmaj indirme akışı tamamlandı."));
        _verifyImageCommand = new AsyncRelayCommand(_ => ExecuteOperationAsync(() => _usbCreationService.VerifyPreparedImageAsync(), "Bütünlük doğrulaması tamamlandı."));
        _writeImageCommand = new AsyncRelayCommand(_ => WriteImageAsync(), _ => CanWriteImage());
        BrowseManualImageCommand = new AsyncRelayCommand(_ => BrowseManualImageAsync());
        ClearManualImageCommand = new AsyncRelayCommand(_ => ClearManualImageAsync());
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
            _writeImageCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ConfirmDestructiveWrite
    {
        get => _confirmDestructiveWrite;
        set
        {
            SetProperty(ref _confirmDestructiveWrite, value);
            _writeImageCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasUsbDevices => Snapshot.Devices.Count > 0;

    public string UsbDiscoveryHint => HasUsbDevices
        ? "Bağlı aygıtın modelini, kapasitesini ve güvenlik durumunu aşağıdan kontrol edin."
        : "USB aygıtı görünmüyorsa bağlantıyı yenileyin, yönetici yetkisini doğrulayın ve Windows'un aygıtı algıladığından emin olun.";

    public ICommand RefreshReleaseCommand => _refreshReleaseCommand;

    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;

    public ICommand BrowseManualImageCommand { get; }

    public ICommand ClearManualImageCommand { get; }

    public ICommand DownloadImageCommand => _downloadImageCommand;

    public ICommand VerifyImageCommand => _verifyImageCommand;

    public ICommand WriteImageCommand => _writeImageCommand;

    private async Task BrowseManualImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "CigerTool OS imajını seç",
            Filter = "Disk İmajları|*.img;*.iso;*.bin;*.wim|Tüm Dosyalar|*.*",
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
        await ExecuteOperationAsync(() => _usbCreationService.RefreshReleaseInfoAsync(), "Elle seçilen imaj bilgisi güncellendi.");
    }

    private async Task ClearManualImageAsync()
    {
        var clearResult = _usbCreationService.ClearManualImageSelection();
        StatusMessage = clearResult.Message;
        await ExecuteOperationAsync(() => _usbCreationService.RefreshReleaseInfoAsync(), "Elle seçilen imaj temizlendi.");
    }

    private async Task WriteImageAsync()
    {
        if (!CanWriteImage() || SelectedDevice is null)
        {
            StatusMessage = "Yazma işlemi için uygun aygıt seçin ve onay kutusunu işaretleyin.";
            return;
        }

        var confirmation = MessageBox.Show(
            $"Seçilen USB aygıttaki tüm veriler silinecek.\n\nAygıt: {SelectedDevice.DisplayName}\nYol: {SelectedDevice.PhysicalPath}\n\nDevam etmek istiyor musunuz?",
            "USB yazma onayı",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            StatusMessage = "USB yazma işlemi iptal edildi.";
            return;
        }

        var result = await _usbCreationService.WriteImageAsync(SelectedDevice.Id, confirmedByUser: true);
        RefreshSnapshot();
        StatusMessage = result.Message;
        _writeImageCommand.RaiseCanExecuteChanged();
    }

    private bool CanWriteImage()
    {
        return Snapshot.CanWriteFromCurrentState &&
               SelectedDevice?.CanWrite == true &&
               ConfirmDestructiveWrite;
    }

    private async Task ExecuteOperationAsync(Func<Task<UsbCreatorOperationResult>> operation, string fallbackSuccessMessage)
    {
        var result = await operation();
        RefreshSnapshot();
        StatusMessage = string.IsNullOrWhiteSpace(result.Message) && result.Succeeded
            ? fallbackSuccessMessage
            : result.Message;
        _writeImageCommand.RaiseCanExecuteChanged();
    }

    private void RefreshSnapshot()
    {
        var previousId = SelectedDevice?.Id;
        Snapshot = _usbCreationService.GetSnapshot();
        SelectedDevice = Snapshot.Devices.FirstOrDefault(device => device.Id == previousId)
                         ?? Snapshot.Devices.FirstOrDefault(device => device.CanWrite);
        RaisePropertyChanged(nameof(HasUsbDevices));
        RaisePropertyChanged(nameof(UsbDiscoveryHint));
    }

    private async Task WarmUpAsync()
    {
        try
        {
            var result = await _usbCreationService.RefreshUsbDevicesAsync();
            RefreshSnapshot();
            StatusMessage = result.Message;
            _writeImageCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"USB aygıt listesi açılışta yenilenemedi: {ex.Message}";
        }
    }
}

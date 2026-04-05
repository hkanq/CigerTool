using System.Windows.Input;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;

namespace CigerTool.App.ViewModels.Pages;

public sealed class DisksPageViewModel : ViewModelBase
{
    private readonly IDiskInventoryService _diskInventoryService;
    private readonly IDiskBenchmarkService _diskBenchmarkService;
    private readonly IDiskSurfaceScanService _diskSurfaceScanService;
    private readonly AsyncRelayCommand _startBenchmarkCommand;
    private readonly RelayCommand _cancelBenchmarkCommand;
    private readonly AsyncRelayCommand _startSurfaceScanCommand;
    private readonly RelayCommand _cancelSurfaceScanCommand;
    private DiskWorkspaceSnapshot _snapshot;
    private DiskSummary? _selectedBenchmarkDisk;
    private DiskBenchmarkProfileOption _selectedBenchmarkProfile;
    private DiskBenchmarkResult? _lastBenchmarkResult;
    private OperationProgressSnapshot? _currentBenchmarkProgress;
    private DiskSurfaceScanResult? _lastSurfaceScanResult;
    private OperationProgressSnapshot? _currentSurfaceScanProgress;
    private CancellationTokenSource? _benchmarkCancellationTokenSource;
    private CancellationTokenSource? _surfaceScanCancellationTokenSource;
    private bool _isBenchmarkRunning;
    private bool _isSurfaceScanRunning;
    private string _statusMessage;

    public DisksPageViewModel(
        IDiskInventoryService diskInventoryService,
        IDiskBenchmarkService diskBenchmarkService,
        IDiskSurfaceScanService diskSurfaceScanService)
    {
        _diskInventoryService = diskInventoryService;
        _diskBenchmarkService = diskBenchmarkService;
        _diskSurfaceScanService = diskSurfaceScanService;
        _snapshot = diskInventoryService.GetSnapshot();
        _statusMessage = "Disk listesi ve sağlık özeti hazır.";

        BenchmarkProfiles =
        [
            new DiskBenchmarkProfileOption("quick", "Hızlı test", "Kısa sürede temel SEQ1M ve RND4K Q1T1 sonucu verir.", 256L * 1024 * 1024, 12_000),
            new DiskBenchmarkProfileOption("standard", "Standart test", "Günlük kullanım için dengeli ve daha kararlı ölçüm alır.", 1024L * 1024 * 1024, 24_000),
            new DiskBenchmarkProfileOption("deep", "Derin test", "Daha uzun çalışır, önbellek etkisini azaltmaya yardımcı olur.", 4L * 1024 * 1024 * 1024, 48_000),
            new DiskBenchmarkProfileOption("stress", "Süreklilik testi", "Uzun yazma davranışını ve SSD önbellek düşüşünü daha görünür hale getirir.", 8L * 1024 * 1024 * 1024, 96_000)
        ];

        _selectedBenchmarkProfile = BenchmarkProfiles[0];
        _selectedBenchmarkDisk = PickPreferredBenchmarkDisk(_snapshot.Disks);

        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsBenchmarkRunning);
        _startBenchmarkCommand = new AsyncRelayCommand(_ => StartBenchmarkAsync(), _ => CanStartBenchmark);
        _cancelBenchmarkCommand = new RelayCommand(_ => CancelBenchmark(), _ => CanCancelBenchmark);
        _startSurfaceScanCommand = new AsyncRelayCommand(_ => StartSurfaceScanAsync(), _ => CanStartSurfaceScan);
        _cancelSurfaceScanCommand = new RelayCommand(_ => CancelSurfaceScan(), _ => CanCancelSurfaceScan);
    }

    public ICommand RefreshCommand { get; }

    public ICommand StartBenchmarkCommand => _startBenchmarkCommand;

    public ICommand CancelBenchmarkCommand => _cancelBenchmarkCommand;

    public ICommand StartSurfaceScanCommand => _startSurfaceScanCommand;

    public ICommand CancelSurfaceScanCommand => _cancelSurfaceScanCommand;

    public IReadOnlyList<DiskBenchmarkProfileOption> BenchmarkProfiles { get; }

    public DiskWorkspaceSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public DiskSummary? SelectedBenchmarkDisk
    {
        get => _selectedBenchmarkDisk;
        set
        {
            SetProperty(ref _selectedBenchmarkDisk, value);
            RaiseDerivedStateChanged();
        }
    }

    public DiskBenchmarkProfileOption SelectedBenchmarkProfile
    {
        get => _selectedBenchmarkProfile;
        set
        {
            SetProperty(ref _selectedBenchmarkProfile, value);
            RaiseDerivedStateChanged();
        }
    }

    public DiskBenchmarkResult? LastBenchmarkResult
    {
        get => _lastBenchmarkResult;
        private set
        {
            SetProperty(ref _lastBenchmarkResult, value);
            RaiseDerivedStateChanged();
        }
    }

    public DiskSurfaceScanResult? LastSurfaceScanResult
    {
        get => _lastSurfaceScanResult;
        private set
        {
            SetProperty(ref _lastSurfaceScanResult, value);
            RaiseDerivedStateChanged();
        }
    }

    public OperationProgressSnapshot? CurrentBenchmarkProgress
    {
        get => _currentBenchmarkProgress;
        private set
        {
            SetProperty(ref _currentBenchmarkProgress, value);
            RaisePropertyChanged(nameof(BenchmarkProgressPercent));
            RaisePropertyChanged(nameof(BenchmarkProgressSummary));
            RaisePropertyChanged(nameof(BenchmarkProgressDetail));
            RaiseDerivedStateChanged();
        }
    }

    public OperationProgressSnapshot? CurrentSurfaceScanProgress
    {
        get => _currentSurfaceScanProgress;
        private set
        {
            SetProperty(ref _currentSurfaceScanProgress, value);
            RaisePropertyChanged(nameof(SurfaceScanProgressPercent));
            RaisePropertyChanged(nameof(SurfaceScanProgressSummary));
            RaisePropertyChanged(nameof(SurfaceScanProgressDetail));
            RaiseDerivedStateChanged();
        }
    }

    public bool IsBenchmarkRunning
    {
        get => _isBenchmarkRunning;
        private set
        {
            SetProperty(ref _isBenchmarkRunning, value);
            RaiseDerivedStateChanged();
        }
    }

    public bool IsSurfaceScanRunning
    {
        get => _isSurfaceScanRunning;
        private set
        {
            SetProperty(ref _isSurfaceScanRunning, value);
            RaiseDerivedStateChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public double BenchmarkProgressPercent => CurrentBenchmarkProgress?.Percent ?? 0;

    public string BenchmarkProgressSummary => CurrentBenchmarkProgress?.Summary ?? "Seçilen diskte test başlatıldığında ilerleme burada görünür.";

    public string BenchmarkProgressDetail
    {
        get
        {
            if (CurrentBenchmarkProgress is null)
            {
                return "Henüz çalışan benchmark yok.";
            }

            return $"{CurrentBenchmarkProgress.ProcessedLabel} / {CurrentBenchmarkProgress.TotalLabel} · {CurrentBenchmarkProgress.SpeedLabel} · Kalan {CurrentBenchmarkProgress.RemainingLabel}";
        }
    }

    public bool CanStartBenchmark => !IsBenchmarkRunning && SelectedBenchmarkDisk is not null && !string.IsNullOrWhiteSpace(SelectedBenchmarkDisk.DriveLetter);

    public bool CanCancelBenchmark => IsBenchmarkRunning;

    public bool CanStartSurfaceScan =>
        !IsSurfaceScanRunning &&
        SelectedBenchmarkDisk is not null &&
        !SelectedBenchmarkDisk.IsSystemVolume &&
        SelectedBenchmarkDisk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(SelectedBenchmarkDisk.DriveLetter);

    public bool CanCancelSurfaceScan => IsSurfaceScanRunning;

    public double SurfaceScanProgressPercent => CurrentSurfaceScanProgress?.Percent ?? 0;

    public string SurfaceScanProgressSummary =>
        CurrentSurfaceScanProgress?.Summary
        ?? "HDD seçildiğinde derin yüzey taramasını başlatabilirsiniz.";

    public string SurfaceScanProgressDetail
    {
        get
        {
            if (CurrentSurfaceScanProgress is null)
            {
                return "Tarama ilerlemesi burada görünür.";
            }

            return $"{CurrentSurfaceScanProgress.ProcessedLabel} / {CurrentSurfaceScanProgress.TotalLabel} · {CurrentSurfaceScanProgress.SpeedLabel} · Kalan {CurrentSurfaceScanProgress.RemainingLabel}";
        }
    }

    public string SurfaceScanHint =>
        SelectedBenchmarkDisk is null
            ? "Önce bir disk seçin."
            : SelectedBenchmarkDisk.IsSystemVolume
                ? "Çalışan sistem diski için derin yüzey taramasını WinPE içinde çalıştırın."
                : SelectedBenchmarkDisk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase)
                    ? "Bu tarama seçilen HDD üzerinde okunamayan alan olup olmadığını denetler."
                    : "Yüzey taraması yalnızca HDD ve USB HDD sürücüler için gösterilir.";

    private async Task StartBenchmarkAsync()
    {
        if (!CanStartBenchmark || SelectedBenchmarkDisk is null)
        {
            StatusMessage = "Performans testi için erişilebilir bir sürücü seçin.";
            return;
        }

        IsBenchmarkRunning = true;
        LastBenchmarkResult = null;
        CurrentBenchmarkProgress = null;
        _benchmarkCancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<OperationProgressSnapshot>(snapshot => CurrentBenchmarkProgress = snapshot);

        try
        {
            LastBenchmarkResult = await _diskBenchmarkService.RunAsync(
                SelectedBenchmarkDisk,
                SelectedBenchmarkProfile,
                progress,
                _benchmarkCancellationTokenSource.Token);

            StatusMessage = LastBenchmarkResult.Summary;
        }
        finally
        {
            IsBenchmarkRunning = false;
            _benchmarkCancellationTokenSource?.Dispose();
            _benchmarkCancellationTokenSource = null;
        }
    }

    private void CancelBenchmark()
    {
        _benchmarkCancellationTokenSource?.Cancel();
        StatusMessage = "Performans testi için iptal isteği gönderildi.";
    }

    private async Task StartSurfaceScanAsync()
    {
        if (!CanStartSurfaceScan || SelectedBenchmarkDisk is null)
        {
            StatusMessage = "Yüzey taraması için erişilebilir bir HDD seçin.";
            return;
        }

        IsSurfaceScanRunning = true;
        LastSurfaceScanResult = null;
        CurrentSurfaceScanProgress = null;
        _surfaceScanCancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<OperationProgressSnapshot>(snapshot => CurrentSurfaceScanProgress = snapshot);

        try
        {
            LastSurfaceScanResult = await _diskSurfaceScanService.RunAsync(
                SelectedBenchmarkDisk,
                progress,
                _surfaceScanCancellationTokenSource.Token);

            StatusMessage = LastSurfaceScanResult.Summary;
        }
        finally
        {
            IsSurfaceScanRunning = false;
            _surfaceScanCancellationTokenSource?.Dispose();
            _surfaceScanCancellationTokenSource = null;
        }
    }

    private void CancelSurfaceScan()
    {
        _surfaceScanCancellationTokenSource?.Cancel();
        StatusMessage = "Yüzey taraması için iptal isteği gönderildi.";
    }

    private void Refresh()
    {
        try
        {
            var previousDiskId = SelectedBenchmarkDisk?.Id;
            Snapshot = _diskInventoryService.GetSnapshot();
            SelectedBenchmarkDisk = Snapshot.Disks.FirstOrDefault(disk => disk.Id == previousDiskId)
                                    ?? PickPreferredBenchmarkDisk(Snapshot.Disks);
            StatusMessage = "Sürücü listesi ve sağlık özeti yenilendi.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sürücü bilgisi alınamadı: {ex.Message}";
        }
    }

    private static DiskSummary? PickPreferredBenchmarkDisk(IReadOnlyList<DiskSummary> disks)
    {
        return disks.FirstOrDefault(disk => !string.IsNullOrWhiteSpace(disk.DriveLetter) && disk.IsReady)
            ?? disks.FirstOrDefault();
    }

    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(BenchmarkProgressPercent));
        RaisePropertyChanged(nameof(BenchmarkProgressSummary));
        RaisePropertyChanged(nameof(BenchmarkProgressDetail));
        RaisePropertyChanged(nameof(CanStartBenchmark));
        RaisePropertyChanged(nameof(CanCancelBenchmark));
        RaisePropertyChanged(nameof(SurfaceScanProgressPercent));
        RaisePropertyChanged(nameof(SurfaceScanProgressSummary));
        RaisePropertyChanged(nameof(SurfaceScanProgressDetail));
        RaisePropertyChanged(nameof(CanStartSurfaceScan));
        RaisePropertyChanged(nameof(CanCancelSurfaceScan));
        RaisePropertyChanged(nameof(SurfaceScanHint));
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _startBenchmarkCommand.RaiseCanExecuteChanged();
        _cancelBenchmarkCommand.RaiseCanExecuteChanged();
        _startSurfaceScanCommand.RaiseCanExecuteChanged();
        _cancelSurfaceScanCommand.RaiseCanExecuteChanged();
    }
}

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
    private readonly AsyncRelayCommand _startBenchmarkCommand;
    private readonly RelayCommand _cancelBenchmarkCommand;
    private DiskWorkspaceSnapshot _snapshot;
    private DiskSummary? _selectedBenchmarkDisk;
    private DiskBenchmarkProfileOption _selectedBenchmarkProfile;
    private DiskBenchmarkResult? _lastBenchmarkResult;
    private OperationProgressSnapshot? _currentBenchmarkProgress;
    private CancellationTokenSource? _benchmarkCancellationTokenSource;
    private bool _isBenchmarkRunning;
    private string _statusMessage;

    public DisksPageViewModel(
        IDiskInventoryService diskInventoryService,
        IDiskBenchmarkService diskBenchmarkService)
    {
        _diskInventoryService = diskInventoryService;
        _diskBenchmarkService = diskBenchmarkService;
        _snapshot = diskInventoryService.GetSnapshot();
        _statusMessage = "Sürücü listesi hazır.";

        BenchmarkProfiles =
        [
            new DiskBenchmarkProfileOption("quick", "Hızlı test", "256 MB test dosyasıyla kısa sıralı ve 4K rastgele ölçüm yapar.", 256L * 1024 * 1024, 12_000),
            new DiskBenchmarkProfileOption("standard", "Standart test", "1 GB test dosyasıyla günlük kullanım için daha kararlı sonuç verir.", 1024L * 1024 * 1024, 24_000),
            new DiskBenchmarkProfileOption("deep", "Derin test", "4 GB test dosyasıyla daha uzun okuma-yazma ve 4K rastgele ölçüm yapar.", 4L * 1024 * 1024 * 1024, 48_000)
        ];

        _selectedBenchmarkProfile = BenchmarkProfiles[0];
        _selectedBenchmarkDisk = PickPreferredBenchmarkDisk(_snapshot.Disks);

        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsBenchmarkRunning);
        _startBenchmarkCommand = new AsyncRelayCommand(_ => StartBenchmarkAsync(), _ => CanStartBenchmark);
        _cancelBenchmarkCommand = new RelayCommand(_ => CancelBenchmark(), _ => CanCancelBenchmark);
    }

    public ICommand RefreshCommand { get; }

    public ICommand StartBenchmarkCommand => _startBenchmarkCommand;

    public ICommand CancelBenchmarkCommand => _cancelBenchmarkCommand;

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

    public bool IsBenchmarkRunning
    {
        get => _isBenchmarkRunning;
        private set
        {
            SetProperty(ref _isBenchmarkRunning, value);
            RaiseDerivedStateChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public double BenchmarkProgressPercent => CurrentBenchmarkProgress?.Percent ?? 0;

    public string BenchmarkProgressSummary => CurrentBenchmarkProgress?.Summary ?? "Seçilen sürücüde performans testi başlatıldığında ilerleme burada görünür.";

    public string BenchmarkProgressDetail
    {
        get
        {
            if (CurrentBenchmarkProgress is null)
            {
                return "Henüz çalışan performans testi yok.";
            }

            return $"{CurrentBenchmarkProgress.ProcessedLabel} / {CurrentBenchmarkProgress.TotalLabel} · {CurrentBenchmarkProgress.SpeedLabel} · Kalan {CurrentBenchmarkProgress.RemainingLabel}";
        }
    }

    public bool CanStartBenchmark => !IsBenchmarkRunning && SelectedBenchmarkDisk is not null && !string.IsNullOrWhiteSpace(SelectedBenchmarkDisk.DriveLetter);

    public bool CanCancelBenchmark => IsBenchmarkRunning;

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
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _startBenchmarkCommand.RaiseCanExecuteChanged();
        _cancelBenchmarkCommand.RaiseCanExecuteChanged();
    }
}

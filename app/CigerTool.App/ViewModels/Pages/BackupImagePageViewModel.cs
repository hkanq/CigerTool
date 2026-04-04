using System.IO;
using System.Text;
using System.Windows.Input;
using Microsoft.Win32;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.App.ViewModels.Pages;

public sealed class BackupImagePageViewModel : ViewModelBase
{
    private readonly IBackupWorkflowService _backupWorkflowService;
    private readonly IOperationLogService _operationLogService;
    private readonly AsyncRelayCommand _startOperationCommand;
    private readonly RelayCommand _cancelOperationCommand;
    private readonly RelayCommand _saveExecutionReportCommand;
    private CancellationTokenSource? _executionCancellationTokenSource;
    private BackupWorkspaceSnapshot _snapshot;
    private BackupOperationOption _selectedOperation;
    private ImageFormatOption _selectedFormat;
    private ImageCaptureModeOption _selectedCaptureMode;
    private DiskSummary? _selectedSource;
    private DiskSummary? _selectedTarget;
    private BackupPlanResult? _plan;
    private ImageExecutionResult? _lastExecution;
    private OperationProgressSnapshot? _currentProgress;
    private string? _sourceImagePath;
    private string? _destinationPath;
    private bool _confirmDestructiveRestore;
    private bool _isOperationRunning;
    private string _statusMessage;

    public BackupImagePageViewModel(
        IBackupWorkflowService backupWorkflowService,
        IOperationLogService operationLogService)
    {
        _backupWorkflowService = backupWorkflowService;
        _operationLogService = operationLogService;

        _snapshot = backupWorkflowService.GetSnapshot();

        Formats =
        [
            new ImageFormatOption(ImageContainerFormat.RawImage, "Ham imaj (.img)", "Sürücünün sektör düzeyindeki içeriğini tek dosyada saklar.", ".img"),
            new ImageFormatOption(ImageContainerFormat.CigerPackage, "CigerTool imajı (.ctimg)", "Ham veya akıllı içerik taşıyabilen CigerTool imaj paketi.", ".ctimg")
        ];

        CaptureModes =
        [
            new ImageCaptureModeOption(ImageCaptureMode.Raw, "Ham", "Tüm içeriği sektör düzeyinde kaydeder.", "Hazır", true),
            new ImageCaptureModeOption(ImageCaptureMode.Smart, "Akıllı", "Sistem dışı sürücülerde yalnızca kullanılan dosyaları kaydeder.", "Hazır", true)
        ];

        _selectedOperation = _snapshot.Operations[0];
        _selectedFormat = Formats[1];
        _selectedSource = PickPreferredSource(_snapshot.CandidateDisks);
        _selectedTarget = PickPreferredTarget(_snapshot.CandidateDisks);
        _selectedCaptureMode = DeterminePreferredCaptureMode(_selectedSource, preferSmartWhenPossible: true);
        _statusMessage = "İşlem türünü seçip kaynak ve hedef alanlarını doldurun.";

        AnalyzeCommand = new RelayCommand(_ => Analyze(), _ => !IsOperationRunning);
        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsOperationRunning);
        SavePlanCommand = new RelayCommand(_ => SavePlan(), _ => CanSavePlan);
        BrowseSourceImageCommand = new RelayCommand(_ => BrowseSourceImage(), _ => !IsOperationRunning);
        BrowseDestinationCommand = new RelayCommand(_ => BrowseDestination(), _ => !IsOperationRunning);
        ClearSourceImageCommand = new RelayCommand(_ => SourceImagePath = null, _ => !string.IsNullOrWhiteSpace(SourceImagePath) && !IsOperationRunning);
        ClearDestinationCommand = new RelayCommand(_ => DestinationPath = null, _ => !string.IsNullOrWhiteSpace(DestinationPath) && !IsOperationRunning);
        _startOperationCommand = new AsyncRelayCommand(_ => StartOperationAsync(), _ => CanStartOperation);
        _cancelOperationCommand = new RelayCommand(_ => CancelOperation(), _ => CanCancelOperation);
        _saveExecutionReportCommand = new RelayCommand(_ => SaveExecutionReport(), _ => CanSaveExecutionReport);
    }

    public ICommand AnalyzeCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SavePlanCommand { get; }

    public ICommand BrowseSourceImageCommand { get; }

    public ICommand BrowseDestinationCommand { get; }

    public ICommand ClearSourceImageCommand { get; }

    public ICommand ClearDestinationCommand { get; }

    public ICommand StartOperationCommand => _startOperationCommand;

    public ICommand CancelOperationCommand => _cancelOperationCommand;

    public ICommand SaveExecutionReportCommand => _saveExecutionReportCommand;

    public IReadOnlyList<ImageFormatOption> Formats { get; }

    public IReadOnlyList<ImageCaptureModeOption> CaptureModes { get; }

    public BackupWorkspaceSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public BackupOperationOption SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            SetProperty(ref _selectedOperation, value);
            EnsureValidSelectionState(resetToPreferredMode: true);
            InvalidatePlan();
        }
    }

    public ImageFormatOption SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            SetProperty(ref _selectedFormat, value);
            EnsureValidSelectionState(resetToPreferredMode: true);
            InvalidatePlan();
        }
    }

    public ImageCaptureModeOption SelectedCaptureMode
    {
        get => _selectedCaptureMode;
        set
        {
            SetProperty(ref _selectedCaptureMode, value);
            InvalidatePlan();
        }
    }

    public DiskSummary? SelectedSource
    {
        get => _selectedSource;
        set
        {
            SetProperty(ref _selectedSource, value);
            EnsureValidSelectionState(resetToPreferredMode: false);
            InvalidatePlan();
        }
    }

    public DiskSummary? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            SetProperty(ref _selectedTarget, value);
            InvalidatePlan();
        }
    }

    public string? SourceImagePath
    {
        get => _sourceImagePath;
        set
        {
            SetProperty(ref _sourceImagePath, value);
            InvalidatePlan();
        }
    }

    public string? DestinationPath
    {
        get => _destinationPath;
        set
        {
            SetProperty(ref _destinationPath, value);
            InvalidatePlan();
        }
    }

    public bool ConfirmDestructiveRestore
    {
        get => _confirmDestructiveRestore;
        set
        {
            SetProperty(ref _confirmDestructiveRestore, value);
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

    public BackupPlanResult? Plan
    {
        get => _plan;
        private set
        {
            SetProperty(ref _plan, value);
            RaisePropertyChanged(nameof(PlanWarnings));
            RaisePropertyChanged(nameof(PlanSteps));
            RaiseDerivedStateChanged();
        }
    }

    public ImageExecutionResult? LastExecution
    {
        get => _lastExecution;
        private set
        {
            SetProperty(ref _lastExecution, value);
            RaisePropertyChanged(nameof(ExecutionWarnings));
            RaiseDerivedStateChanged();
        }
    }

    public OperationProgressSnapshot? CurrentProgress
    {
        get => _currentProgress;
        private set
        {
            SetProperty(ref _currentProgress, value);
            RaisePropertyChanged(nameof(ProgressPercent));
            RaisePropertyChanged(nameof(ProgressSummary));
            RaisePropertyChanged(nameof(ProgressDetail));
            RaiseDerivedStateChanged();
        }
    }

    public IReadOnlyList<string> PlanWarnings => Plan?.Warnings ?? Array.Empty<string>();

    public string PrimaryPlanWarning => PlanWarnings.FirstOrDefault() ?? "Belirgin bir engel bulunmuyor.";

    public IReadOnlyList<string> ExecutionWarnings => LastExecution?.Warnings ?? Array.Empty<string>();

    public IReadOnlyList<WorkflowStepItem> PlanSteps => Plan?.Steps ?? Array.Empty<WorkflowStepItem>();

    public bool CanSavePlan => Plan?.CanExportPlan == true;

    public bool HasPlanBlockingWarning => Plan is not null && !Plan.CanStartNow && PlanWarnings.Count > 0;

    public bool CanStartOperation => !IsOperationRunning && Plan?.CanStartNow == true && (!IsDestructiveOperation || ConfirmDestructiveRestore);

    public bool CanCancelOperation => IsOperationRunning;

    public bool CanSaveExecutionReport => LastExecution is not null;

    public bool ShowSourceDiskSelector => SelectedOperation.Value is BackupWorkflowKind.CreateImage or BackupWorkflowKind.Transfer;

    public bool ShowTargetDiskSelector => SelectedOperation.Value == BackupWorkflowKind.RestoreImage;

    public bool ShowSourceImageSelector => SelectedOperation.Value is BackupWorkflowKind.RestoreImage or BackupWorkflowKind.ConvertImage;

    public bool ShowDestinationSelector => SelectedOperation.Value is BackupWorkflowKind.CreateImage or BackupWorkflowKind.ConvertImage;

    public bool ShowFormatSelector => SelectedOperation.Value is BackupWorkflowKind.CreateImage or BackupWorkflowKind.ConvertImage;

    public bool ShowCaptureModeSelector => SelectedOperation.Value == BackupWorkflowKind.CreateImage && SelectedFormat.Value == ImageContainerFormat.CigerPackage;

    public bool IsDestructiveOperation => SelectedOperation.Value == BackupWorkflowKind.RestoreImage;

    public double ProgressPercent => CurrentProgress?.Percent ?? 0;

    public string ProgressSummary => CurrentProgress?.Summary ?? "İşlem başladığında ilerleme durumu burada görünür.";

    public string ProgressDetail
    {
        get
        {
            if (CurrentProgress is null)
            {
                return "Henüz çalışan işlem yok.";
            }

            return $"{CurrentProgress.ProcessedLabel} / {CurrentProgress.TotalLabel} · {CurrentProgress.SpeedLabel} · Kalan {CurrentProgress.RemainingLabel}";
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private void Analyze()
    {
        try
        {
            Plan = _backupWorkflowService.Analyze(BuildRequest());
            StatusMessage = !Plan.CanStartNow && PlanWarnings.Count > 0
                ? $"{Plan.Summary} İlk neden: {PlanWarnings[0]}"
                : Plan.Summary;
        }
        catch (Exception ex)
        {
            Plan = null;
            StatusMessage = $"Denetim tamamlanamadı: {ex.Message}";
        }
    }

    private async Task StartOperationAsync()
    {
        if (!CanStartOperation)
        {
            StatusMessage = IsDestructiveOperation
                ? "Geri yüklemeyi başlatmadan önce onay kutusunu işaretleyin."
                : "İşlemi başlatmak için önce denetimi tamamlayın.";
            return;
        }

        IsOperationRunning = true;
        LastExecution = null;
        CurrentProgress = null;
        _executionCancellationTokenSource = new CancellationTokenSource();
        var progress = new Progress<OperationProgressSnapshot>(snapshot => CurrentProgress = snapshot);

        try
        {
            LastExecution = await _backupWorkflowService.ExecuteAsync(BuildRequest(), progress, _executionCancellationTokenSource.Token);
            StatusMessage = LastExecution.Summary;
        }
        finally
        {
            IsOperationRunning = false;
            _executionCancellationTokenSource?.Dispose();
            _executionCancellationTokenSource = null;
        }
    }

    private void CancelOperation()
    {
        _executionCancellationTokenSource?.Cancel();
        StatusMessage = "İptal isteği gönderildi.";
    }

    private void Refresh()
    {
        try
        {
            var previousSourceId = SelectedSource?.Id;
            var previousTargetId = SelectedTarget?.Id;

            Snapshot = _backupWorkflowService.GetSnapshot();
            SelectedSource = Snapshot.CandidateDisks.FirstOrDefault(disk => disk.Id == previousSourceId)
                ?? PickPreferredSource(Snapshot.CandidateDisks);
            SelectedTarget = Snapshot.CandidateDisks.FirstOrDefault(disk => disk.Id == previousTargetId)
                ?? PickPreferredTarget(Snapshot.CandidateDisks);

            EnsureValidSelectionState(resetToPreferredMode: true);
            StatusMessage = "Sürücü listesi ve işlem alanları yenilendi.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Yenileme tamamlanamadı: {ex.Message}";
        }
    }

    private void BrowseSourceImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "İmaj dosyasını seçin",
            Filter = "Desteklenen imajlar (*.img;*.ctimg)|*.img;*.ctimg|Tüm dosyalar (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            SourceImagePath = dialog.FileName;
        }
    }

    private void BrowseDestination()
    {
        var dialog = new SaveFileDialog
        {
            Title = GetDestinationDialogTitle(),
            Filter = GetDestinationDialogFilter(),
            DefaultExt = SelectedFormat.DefaultExtension,
            AddExtension = true,
            FileName = GetSuggestedDestinationName()
        };

        if (dialog.ShowDialog() == true)
        {
            DestinationPath = dialog.FileName;
        }
    }

    private void SavePlan()
    {
        if (Plan is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "İmaj denetim raporunu kaydet",
            Filter = "Metin dosyası (*.txt)|*.txt",
            FileName = $"CigerTool-Imaj-Denetim-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Denetim raporu kaydetme işlemi iptal edildi.";
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("CigerTool İmaj İş Akışı Denetim Raporu");
        builder.AppendLine(new string('=', 40));
        builder.AppendLine($"İşlem: {SelectedOperation.Title}");
        builder.AppendLine($"Biçim: {SelectedFormat.Title}");
        builder.AppendLine($"Yöntem: {(ShowCaptureModeSelector ? SelectedCaptureMode.Title : "Ham")}");
        builder.AppendLine($"Kaynak sürücü: {SelectedSource?.Name ?? "Belirtilmedi"}");
        builder.AppendLine($"Hedef sürücü: {SelectedTarget?.Name ?? "Belirtilmedi"}");
        builder.AppendLine($"Kaynak imaj: {SourceImagePath ?? "Belirtilmedi"}");
        builder.AppendLine($"Kayıt yolu: {DestinationPath ?? "Belirtilmedi"}");
        builder.AppendLine($"Durum: {Plan.StatusLabel}");
        builder.AppendLine($"Özet: {Plan.Summary}");
        builder.AppendLine();
        builder.AppendLine("Sonraki adım");
        builder.AppendLine(Plan.NextAction);
        builder.AppendLine();
        builder.AppendLine("Kapsam notu");
        builder.AppendLine(Plan.ScopeNote);

        if (PlanWarnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Uyarılar");
            foreach (var warning in PlanWarnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Adımlar");
        foreach (var step in PlanSteps)
        {
            builder.AppendLine($"- {step.Title}: {step.Description}");
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);
        StatusMessage = "Denetim raporu kaydedildi.";
    }

    private void SaveExecutionReport()
    {
        if (LastExecution is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "İmaj işlem sonucu raporunu kaydet",
            Filter = "Metin dosyası (*.txt)|*.txt",
            FileName = $"CigerTool-Imaj-Sonuc-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "Sonuç raporu kaydetme işlemi iptal edildi.";
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("CigerTool İmaj İşlem Sonuç Raporu");
        builder.AppendLine(new string('=', 38));
        builder.AppendLine($"İşlem: {SelectedOperation.Title}");
        builder.AppendLine($"Durum: {LastExecution.StatusLabel}");
        builder.AppendLine($"Özet: {LastExecution.Summary}");
        builder.AppendLine($"Biçim: {LastExecution.FormatLabel}");
        builder.AppendLine($"Kaynak: {LastExecution.SourceLabel}");
        builder.AppendLine($"Hedef: {LastExecution.DestinationLabel}");
        builder.AppendLine($"İşlenen veri: {ByteSizeFormatter.Format(LastExecution.ProcessedBytes)} / {ByteSizeFormatter.Format(LastExecution.TotalBytes)}");

        if (LastExecution.Notes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Notlar");
            foreach (var note in LastExecution.Notes)
            {
                builder.AppendLine($"- {note}");
            }
        }

        if (ExecutionWarnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Uyarılar");
            foreach (var warning in ExecutionWarnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);

        _operationLogService.Record(
            OperationSeverity.Info,
            "İmaj",
            "İmaj sonuç raporu kaydedildi.",
            "image.result.saved",
            new Dictionary<string, string>
            {
                ["path"] = dialog.FileName,
                ["state"] = LastExecution.State.ToString()
            });

        StatusMessage = "İmaj sonuç raporu kaydedildi.";
    }

    private BackupWorkflowRequest BuildRequest()
    {
        return new BackupWorkflowRequest(
            Operation: SelectedOperation.Value,
            SourceDiskId: SelectedSource?.Id,
            TargetDiskId: SelectedTarget?.Id,
            SourceImagePath: SourceImagePath,
            DestinationPath: DestinationPath,
            Format: SelectedFormat.Value,
            CaptureMode: ShowCaptureModeSelector ? SelectedCaptureMode.Value : ImageCaptureMode.Raw);
    }

    private void EnsureValidSelectionState(bool resetToPreferredMode)
    {
        if (!ShowCaptureModeSelector)
        {
            SetCaptureMode(CaptureModes[0]);
            return;
        }

        if (SelectedSource?.IsSystemVolume == true && SelectedCaptureMode.Value == ImageCaptureMode.Smart)
        {
            SetCaptureMode(CaptureModes[0]);
            return;
        }

        if (resetToPreferredMode)
        {
            SetCaptureMode(DeterminePreferredCaptureMode(SelectedSource, preferSmartWhenPossible: true));
        }
    }

    private void SetCaptureMode(ImageCaptureModeOption captureMode)
    {
        if (!ReferenceEquals(_selectedCaptureMode, captureMode))
        {
            _selectedCaptureMode = captureMode;
            RaisePropertyChanged(nameof(SelectedCaptureMode));
        }
    }

    private static DiskSummary? PickPreferredSource(IReadOnlyList<DiskSummary> disks)
    {
        return disks.FirstOrDefault(disk => !disk.IsSystemVolume && disk.IsReady)
            ?? disks.FirstOrDefault(disk => !disk.IsSystemVolume)
            ?? disks.FirstOrDefault();
    }

    private static DiskSummary? PickPreferredTarget(IReadOnlyList<DiskSummary> disks)
    {
        return disks.FirstOrDefault(disk => !disk.IsSystemVolume && disk.IsReady)
            ?? disks.FirstOrDefault(disk => !disk.IsSystemVolume)
            ?? disks.FirstOrDefault();
    }

    private ImageCaptureModeOption DeterminePreferredCaptureMode(DiskSummary? source, bool preferSmartWhenPossible)
    {
        if (!ShowCaptureModeSelector || !preferSmartWhenPossible || source is null || source.IsSystemVolume)
        {
            return CaptureModes[0];
        }

        return CaptureModes[1];
    }

    private string GetDestinationDialogTitle()
    {
        return SelectedOperation.Value == BackupWorkflowKind.ConvertImage
            ? "Dönüştürülen dosyayı kaydet"
            : "İmaj dosyasını kaydet";
    }

    private string GetDestinationDialogFilter()
    {
        if (SelectedFormat.Value == ImageContainerFormat.RawImage)
        {
            return "Ham imaj (*.img)|*.img|Tüm dosyalar (*.*)|*.*";
        }

        return SelectedCaptureMode.Value == ImageCaptureMode.Smart
            ? "Akıllı CigerTool imajı (*.ctimg)|*.ctimg|Tüm dosyalar (*.*)|*.*"
            : "CigerTool imajı (*.ctimg)|*.ctimg|Tüm dosyalar (*.*)|*.*";
    }

    private string GetSuggestedDestinationName()
    {
        var extension = SelectedFormat.DefaultExtension;

        return SelectedOperation.Value switch
        {
            BackupWorkflowKind.ConvertImage => $"CigerTool-Donusturulmus-{DateTime.Now:yyyyMMdd-HHmm}{extension}",
            BackupWorkflowKind.CreateImage when SelectedCaptureMode.Value == ImageCaptureMode.Smart => $"CigerTool-Akilli-Imaj-{DateTime.Now:yyyyMMdd-HHmm}{extension}",
            _ => $"CigerTool-Imaj-{DateTime.Now:yyyyMMdd-HHmm}{extension}"
        };
    }

    private void InvalidatePlan()
    {
        Plan = null;
        LastExecution = null;
        CurrentProgress = null;
        ConfirmDestructiveRestore = false;
        StatusMessage = "Seçimler güncellendi. Devam etmek için denetimi yeniden çalıştırın.";
        RaiseDerivedStateChanged();
    }

    private void RaiseDerivedStateChanged()
    {
        RaisePropertyChanged(nameof(PlanWarnings));
        RaisePropertyChanged(nameof(PrimaryPlanWarning));
        RaisePropertyChanged(nameof(ExecutionWarnings));
        RaisePropertyChanged(nameof(CanSavePlan));
        RaisePropertyChanged(nameof(HasPlanBlockingWarning));
        RaisePropertyChanged(nameof(CanStartOperation));
        RaisePropertyChanged(nameof(CanCancelOperation));
        RaisePropertyChanged(nameof(CanSaveExecutionReport));
        RaisePropertyChanged(nameof(ShowSourceDiskSelector));
        RaisePropertyChanged(nameof(ShowTargetDiskSelector));
        RaisePropertyChanged(nameof(ShowSourceImageSelector));
        RaisePropertyChanged(nameof(ShowDestinationSelector));
        RaisePropertyChanged(nameof(ShowFormatSelector));
        RaisePropertyChanged(nameof(ShowCaptureModeSelector));
        RaisePropertyChanged(nameof(IsDestructiveOperation));
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(ProgressSummary));
        RaisePropertyChanged(nameof(ProgressDetail));
        (AnalyzeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SavePlanCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseSourceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseDestinationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearSourceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearDestinationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _startOperationCommand.RaiseCanExecuteChanged();
        _cancelOperationCommand.RaiseCanExecuteChanged();
        _saveExecutionReportCommand.RaiseCanExecuteChanged();
    }
}

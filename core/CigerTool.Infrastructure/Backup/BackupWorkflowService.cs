using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Backup;

public sealed class BackupWorkflowService(
    IDiskInventoryService diskInventoryService,
    IEnvironmentProfileService environmentProfileService,
    IOperationLogService operationLogService) : IBackupWorkflowService
{
    private readonly ImageContainerService _imageContainerService = new();

    public BackupWorkspaceSnapshot GetSnapshot()
    {
        var disks = diskInventoryService.GetCurrentDisks();

        return new BackupWorkspaceSnapshot(
            Heading: "Yedekleme ve İmaj",
            Summary: "Diski dosyaya alma, imajı geri yükleme ve desteklenen biçimler arasında dönüştürme işlemlerini buradan yönetin.",
            Metrics:
            [
                new CardMetric("Görünen sürücüler", disks.Count.ToString(), "İmaj alma ve geri yükleme için değerlendirilen sürücüler."),
                new CardMetric("Ham imaj", "Hazır", "Uygun sürücüler .img veya ham .ctimg olarak alınabilir."),
                new CardMetric("Akıllı imaj", "Hazır", "Sistem dışı sürücüler yalnızca kullanılan alan kadar .ctimg olarak alınabilir."),
                new CardMetric("Geri yükleme", "Hazır", "Ham veya akıllı desteklenen imajlar uygun hedefe geri uygulanabilir.")
            ],
            Operations:
            [
                new BackupOperationOption(BackupWorkflowKind.CreateImage, "İmaj oluştur", "Seçtiğiniz sürücüyü dosyaya alın.", "Yürütme açık", true),
                new BackupOperationOption(BackupWorkflowKind.RestoreImage, "İmaj geri yükle", "Bir imajı seçilen hedefe uygulayın.", "Yürütme açık", true),
                new BackupOperationOption(BackupWorkflowKind.ConvertImage, "İmaj dönüştür", "Desteklenen biçimler arasında dönüştürme yapın.", "Yürütme açık", true),
                new BackupOperationOption(BackupWorkflowKind.Transfer, "Taşıma ve geçiş", "Yaygın kaynak-hedef senaryoları için yönlendirme alın.", "Hazırlık desteği", false)
            ],
            Capabilities:
            [
                new BackupCapabilityItem("Ham imaj alma", "Hazır", "Sürücü içeriği sektör düzeyinde .img veya ham .ctimg olarak alınabilir."),
                new BackupCapabilityItem("Akıllı imaj alma", "Hazır", "Sistem dışı sürücüler dosya tabanlı olarak kullanılan alan kadar .ctimg paketine alınabilir."),
                new BackupCapabilityItem("Ham geri yükleme", "Hazır", "Ham .img ve ham .ctimg içerikleri uygun hedefe yazılabilir."),
                new BackupCapabilityItem("Akıllı paket geri yükleme", "Hazır", "Akıllı .ctimg paketleri sistem dışı hedefe dosya tabanlı olarak geri yerleştirilebilir."),
                new BackupCapabilityItem("Taşıma ve geçiş", "Kısmi", "Bu alan şimdilik karar desteği sunar; gerçek veri taşıma için Klonlama bölümü kullanılır.")
            ],
            CandidateDisks: disks,
            SupportedScenarios:
            [
                "Bir sürücüyü ham .img olarak alma",
                "Bir sürücüyü ham CigerTool imajı (.ctimg) olarak alma",
                "Sistem dışı sürücüyü akıllı CigerTool imajı (.ctimg) olarak alma",
                "Ham .img veya ham .ctimg dosyasını hedef sürücüye geri yükleme",
                "Akıllı .ctimg imajını sistem dışı hedefe geri yerleştirme",
                ".img ile ham .ctimg arasında dönüştürme"
            ]);
    }

    public BackupPlanResult Analyze(BackupWorkflowRequest request)
    {
        var disks = diskInventoryService.GetCurrentDisks();
        var environment = environmentProfileService.GetCurrentProfile();
        var source = disks.FirstOrDefault(disk => string.Equals(disk.Id, request.SourceDiskId, StringComparison.OrdinalIgnoreCase));
        var target = disks.FirstOrDefault(disk => string.Equals(disk.Id, request.TargetDiskId, StringComparison.OrdinalIgnoreCase));
        var warnings = new List<string>();

        return request.Operation switch
        {
            BackupWorkflowKind.CreateImage => AnalyzeCreateImage(request, source, environment, warnings),
            BackupWorkflowKind.RestoreImage => AnalyzeRestoreImage(request, target, environment, warnings),
            BackupWorkflowKind.ConvertImage => AnalyzeConvertImage(request, warnings),
            _ => BuildTransferPlan(source, environment, warnings)
        };
    }

    public async Task<ImageExecutionResult> ExecuteAsync(
        BackupWorkflowRequest request,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = Analyze(request);
        if (!plan.CanStartNow)
        {
            return new ImageExecutionResult(
                request.Operation,
                ExecutionState.Failed,
                "Başlatılamadı",
                plan.Summary,
                _imageContainerService.GetFormatLabel(request.Format, request.CaptureMode),
                request.SourceImagePath ?? request.SourceDiskId ?? "Kaynak",
                request.DestinationPath ?? request.TargetDiskId ?? "Hedef",
                0,
                0,
                plan.Warnings,
                [plan.ScopeNote]);
        }

        try
        {
            return request.Operation switch
            {
                BackupWorkflowKind.CreateImage => await ExecuteCreateImageAsync(request, progress, cancellationToken),
                BackupWorkflowKind.RestoreImage => await ExecuteRestoreImageAsync(request, progress, cancellationToken),
                BackupWorkflowKind.ConvertImage => await ExecuteConvertImageAsync(request, progress, cancellationToken),
                _ => new ImageExecutionResult(
                    request.Operation,
                    ExecutionState.Failed,
                    "Desteklenmiyor",
                    "Taşıma ve geçiş bölümü bu sürümde doğrudan yürütme açmıyor.",
                    "Hazırlık akışı",
                    request.SourceDiskId ?? "Kaynak",
                    request.TargetDiskId ?? "Hedef",
                    0,
                    0,
                    [],
                    ["Gerçek veri taşıma için Klonlama bölümünü kullanın."])
            };
        }
        catch (OperationCanceledException)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "İmaj",
                "İşlem kullanıcı tarafından iptal edildi.",
                "image.canceled",
                new Dictionary<string, string>
                {
                    ["operation"] = request.Operation.ToString()
                });

            return new ImageExecutionResult(
                request.Operation,
                ExecutionState.Canceled,
                "İptal edildi",
                "İşlem durduruldu. Hedefte kısmi veri kalmış olabilir.",
                _imageContainerService.GetFormatLabel(request.Format, request.CaptureMode),
                request.SourceImagePath ?? request.SourceDiskId ?? "Kaynak",
                request.DestinationPath ?? request.TargetDiskId ?? "Hedef",
                0,
                0,
                ["İptal edilen işlemden sonra hedefi yeniden doğrulamanız önerilir."],
                ["Ayrıntılar Günlükler bölümüne kaydedildi."]);
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Error,
                "İmaj",
                $"İşlem başarısız oldu: {ex.Message}",
                "image.failed",
                new Dictionary<string, string>
                {
                    ["operation"] = request.Operation.ToString()
                });

            return new ImageExecutionResult(
                request.Operation,
                ExecutionState.Failed,
                "Başarısız",
                $"İşlem tamamlanamadı: {ex.Message}",
                _imageContainerService.GetFormatLabel(request.Format, request.CaptureMode),
                request.SourceImagePath ?? request.SourceDiskId ?? "Kaynak",
                request.DestinationPath ?? request.TargetDiskId ?? "Hedef",
                0,
                0,
                [ex.Message],
                ["Ayrıntılar Günlükler bölümüne kaydedildi."]);
        }
    }

    private BackupPlanResult AnalyzeCreateImage(
        BackupWorkflowRequest request,
        DiskSummary? source,
        AppEnvironmentProfile environment,
        List<string> warnings)
    {
        if (source is null)
        {
            return BuildResult(
                request.Operation,
                "Kaynak seçin",
                "İmaj almak için önce kaynak sürücüyü seçin.",
                "Kaynak sürücüyü seçin.",
                "Bu bölüm ham imaj ve akıllı CigerTool imajı hazırlayabilir.",
                false,
                false,
                warnings,
                BuildExecutionSteps(0, true));
        }

        if (!source.IsReady)
        {
            warnings.Add("Seçilen sürücü şu anda hazır görünmüyor veya erişilemiyor.");
        }

        if (string.IsNullOrWhiteSpace(source.DriveLetter))
        {
            warnings.Add("Seçilen sürücü için erişilebilir bir sürücü harfi bulunamadı.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return BuildResult(
                request.Operation,
                "Kayıt yolunu seçin",
                "İmaj dosyasının kaydedileceği yolu seçmeden devam edilemez.",
                "Kaydetme yolunu belirleyin.",
                "Ham imaj .img veya .ctimg olabilir; akıllı imaj yalnızca .ctimg olarak kaydedilir.",
                false,
                true,
                warnings,
                BuildExecutionSteps(1, true));
        }

        if (File.Exists(request.DestinationPath))
        {
            warnings.Add("Seçilen dosya zaten var; işlem başlarsa üzerine yazılacaktır.");
        }

        if (request.CaptureMode == ImageCaptureMode.Smart)
        {
            if (request.Format != ImageContainerFormat.CigerPackage)
            {
                warnings.Add("Akıllı imaj yalnızca CigerTool imajı (.ctimg) olarak kaydedilebilir.");
            }

            if (source.IsSystemVolume)
            {
                warnings.Add("Akıllı imaj alma şu anda sistem dışı sürücüler için açıktır.");
            }

            var canStartSmart = !HasBlockingWarnings(warnings);
            return BuildResult(
                request.Operation,
                canStartSmart ? "Hazır" : "Hazır değil",
                canStartSmart
                    ? $"{source.Name} sürücüsü kullanılan alan kadar akıllı imaj olarak alınabilir."
                    : "Akıllı imaj için önce uyarıları çözün.",
                canStartSmart ? "İşlemi başlatın ve ilerlemeyi izleyin." : "Biçimi veya kaynağı düzeltip yeniden denetleyin.",
                "Akıllı imaj, sistem dışı sürücülerde dosya tabanlı çalışır; boş alanı ham olarak taşımaz.",
                canStartSmart,
                true,
                warnings,
                BuildExecutionSteps(canStartSmart ? 3 : 2, true));
        }

        if (!RawVolumeAccessScope.IsAdministrator())
        {
            warnings.Add("Ham imaj alma için uygulama yönetici yetkisiyle çalışmalıdır.");
        }

        if (!source.SupportsRawAccess)
        {
            warnings.Add("Seçilen sürücü ham erişim için uygun görünmüyor.");
        }

        if (source.IsSystemVolume && !environment.IsWinPe)
        {
            warnings.Add("Çalışan sistem sürücüsünü masaüstünde ham olarak almak desteklenmez. Bu işlem için CigerTool OS kullanın.");
        }

        var canStartRaw = !HasBlockingWarnings(warnings);
        return BuildResult(
            request.Operation,
            canStartRaw ? "Hazır" : "Hazır değil",
            canStartRaw
                ? $"{source.Name} sürücüsü {_imageContainerService.GetFormatLabel(request.Format, request.CaptureMode)} biçiminde alınabilir."
                : "Ham imaj alma koşulları henüz güvenli değil.",
            canStartRaw ? "İşlemi başlatın ve ilerlemeyi izleyin." : "Uyarıları çözün ve yeniden doğrulayın.",
            "Ham imaj, seçilen sürücünün sektör düzeyindeki içeriğini .img veya .ctimg olarak kaydeder.",
            canStartRaw,
            true,
            warnings,
            BuildExecutionSteps(canStartRaw ? 3 : 2, true));
    }

    private BackupPlanResult AnalyzeRestoreImage(
        BackupWorkflowRequest request,
        DiskSummary? target,
        AppEnvironmentProfile environment,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.SourceImagePath) || !File.Exists(request.SourceImagePath))
        {
            return BuildResult(
                request.Operation,
                "İmaj dosyasını seçin",
                "Geri yüklemek için bir .img veya .ctimg dosyası seçin.",
                "İmaj dosyasını seçin.",
                "Ham .img ve .ctimg ile akıllı .ctimg imajları desteklenir.",
                false,
                false,
                warnings,
                BuildExecutionSteps(0, true));
        }

        if (target is null)
        {
            return BuildResult(
                request.Operation,
                "Hedef seçin",
                "Geri yükleme için hedef sürücü seçilmelidir.",
                "Hedef sürücüyü seçin.",
                "Geri yükleme işlemi seçilen hedefin mevcut içeriğini değiştirir.",
                false,
                true,
                warnings,
                BuildExecutionSteps(1, true));
        }

        if (!target.IsReady)
        {
            warnings.Add("Seçilen hedef sürücü şu anda hazır görünmüyor veya erişilemiyor.");
        }

        if (string.IsNullOrWhiteSpace(target.DriveLetter))
        {
            warnings.Add("Seçilen hedef için erişilebilir bir sürücü harfi bulunamadı.");
        }

        var sourceFormat = _imageContainerService.DetectFormat(request.SourceImagePath);
        var sourceCaptureMode = _imageContainerService.DetectCaptureMode(request.SourceImagePath);
        var payloadLength = _imageContainerService.GetPayloadLength(request.SourceImagePath);

        if (sourceCaptureMode == ImageCaptureMode.Smart)
        {
            if (target.IsSystemVolume)
            {
                warnings.Add("Akıllı imaj geri yükleme şu anda sistem dışı hedef sürücüler için açıktır.");
            }

            if (target.TotalBytes < payloadLength)
            {
                warnings.Add("Hedef sürücünün kapasitesi akıllı imaj içeriğinden küçük.");
            }

            var canStartSmartRestore = !HasBlockingWarnings(warnings);
            return BuildResult(
                request.Operation,
                canStartSmartRestore ? "Hazır" : "Hazır değil",
                canStartSmartRestore
                    ? $"{Path.GetFileName(request.SourceImagePath)} seçilen hedefe akıllı imaj olarak geri yüklenebilir."
                    : "Akıllı imaj geri yükleme için önce uyarıları çözün.",
                canStartSmartRestore ? "Onayı verin, işlemi başlatın ve ilerlemeyi izleyin." : "Hedef sürücüyü değiştirin veya uyarıları giderin.",
                "Akıllı imaj geri yükleme hedef sürücüyü temizler ve dosyaları yeniden yerleştirir; bölüm tablosunu değiştirmez.",
                canStartSmartRestore,
                true,
                warnings,
                BuildExecutionSteps(canStartSmartRestore ? 3 : 2, true));
        }

        if (!RawVolumeAccessScope.IsAdministrator())
        {
            warnings.Add("Ham geri yükleme için uygulama yönetici yetkisiyle çalışmalıdır.");
        }

        if (!target.SupportsRawAccess)
        {
            warnings.Add("Seçilen hedef sürücü ham geri yükleme için uygun görünmüyor.");
        }

        if (target.IsSystemVolume && !environment.IsWinPe)
        {
            warnings.Add("Aktif sistem sürücüsüne masaüstünde ham geri yükleme yapılamaz. Bu işlem için CigerTool OS kullanın.");
        }

        if (target.TotalBytes < payloadLength)
        {
            warnings.Add("Hedef sürücü seçilen imaj içeriğinden daha küçük.");
        }

        var canStartRawRestore = !HasBlockingWarnings(warnings);
        return BuildResult(
            request.Operation,
            canStartRawRestore ? "Hazır" : "Hazır değil",
            canStartRawRestore
                ? $"{Path.GetFileName(request.SourceImagePath)} hedef sürücüye geri yüklenebilir."
                : "Geri yükleme koşulları henüz güvenli değil.",
            canStartRawRestore ? "Onayı verin, işlemi başlatın ve ilerlemeyi izleyin." : "Uyarıları çözün ve yeniden doğrulayın.",
            sourceFormat == ImageContainerFormat.RawImage
                ? "Ham geri yükleme, imaj içeriğini hedef sürücüye sektör düzeyinde yazar."
                : "Ham CigerTool imajı geri yükleme, paket içeriğini hedef sürücüye sektör düzeyinde yazar.",
            canStartRawRestore,
            true,
            warnings,
            BuildExecutionSteps(canStartRawRestore ? 3 : 2, true));
    }

    private BackupPlanResult AnalyzeConvertImage(BackupWorkflowRequest request, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.SourceImagePath) || !File.Exists(request.SourceImagePath))
        {
            return BuildResult(
                request.Operation,
                "Kaynak imajı seçin",
                "Dönüştürmek için .img veya .ctimg dosyası seçin.",
                "Kaynak imajı seçin.",
                "Bu bölüm ham .img ile ham .ctimg arasında dönüştürme yapar.",
                false,
                false,
                warnings,
                BuildExecutionSteps(0, true));
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return BuildResult(
                request.Operation,
                "Hedef dosyayı seçin",
                "Dönüştürülen dosyanın kaydedileceği yolu seçin.",
                "Hedef dosyayı belirleyin.",
                "Kaynak ve hedef biçim aynı olamaz.",
                false,
                true,
                warnings,
                BuildExecutionSteps(1, true));
        }

        var sourceFormat = _imageContainerService.DetectFormat(request.SourceImagePath);
        var sourceCaptureMode = _imageContainerService.DetectCaptureMode(request.SourceImagePath);

        if (sourceFormat == request.Format)
        {
            warnings.Add("Kaynak ve hedef biçim aynı. Farklı bir hedef biçim seçin.");
        }

        if (sourceCaptureMode == ImageCaptureMode.Smart)
        {
            warnings.Add("Akıllı CigerTool imajı şu anda başka bir biçime dönüştürülemez.");
        }

        if (File.Exists(request.DestinationPath))
        {
            warnings.Add("Seçilen hedef dosya zaten var; işlem başlarsa üzerine yazılacaktır.");
        }

        var canStart = !HasBlockingWarnings(warnings);
        return BuildResult(
            request.Operation,
            canStart ? "Hazır" : "Hazır değil",
            canStart
                ? $"{Path.GetFileName(request.SourceImagePath)} dosyası {_imageContainerService.GetFormatLabel(request.Format)} biçimine dönüştürülebilir."
                : "Dönüştürme için ayarlar henüz uygun değil.",
            canStart ? "İşlemi başlatın ve ilerlemeyi izleyin." : "Uyarıları çözün ve yeniden doğrulayın.",
            "Dönüştürme işlemi ham imaj içeriğini yeni kapsayıcıya taşır; akıllı imaj dönüştürme bu sürümde açık değildir.",
            canStart,
            true,
            warnings,
            BuildExecutionSteps(canStart ? 3 : 2, true));
    }

    private static BackupPlanResult BuildTransferPlan(
        DiskSummary? source,
        AppEnvironmentProfile environment,
        IReadOnlyList<string> warnings)
    {
        var localWarnings = warnings.ToList();
        if (source is not null && source.IsSystemVolume && !environment.IsWinPe)
        {
            localWarnings.Add("Aktif sistem sürücüsünü taşırken en güvenli yol servis ortamıdır.");
        }

        return BuildResult(
            BackupWorkflowKind.Transfer,
            "Hazırlık akışı",
            source is null ? "Taşıma ve geçiş bölümü genel yönlendirme sunar." : $"{source.Name} için taşıma senaryolarını gözden geçirebilirsiniz.",
            "Gerçek veri taşıma için Klonlama bölümüne geçin.",
            "Bu bölüm şu an karar desteği içindir; gerçek yürütme Klonlama ekranında açılmıştır.",
            false,
            true,
            localWarnings,
            BuildExecutionSteps(2, false));
    }

    private async Task<ImageExecutionResult> ExecuteCreateImageAsync(
        BackupWorkflowRequest request,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var source = diskInventoryService.FindById(request.SourceDiskId!)!;

        operationLogService.Record(
            OperationSeverity.Info,
            "İmaj",
            "İmaj alma başlatıldı.",
            "image.capture.started",
            new Dictionary<string, string>
            {
                ["source"] = source.Name,
                ["format"] = request.Format.ToString(),
                ["captureMode"] = request.CaptureMode.ToString(),
                ["destination"] = request.DestinationPath!
            });

        var outcome = await _imageContainerService.CaptureVolumeAsync(
            source,
            request.DestinationPath!,
            request.Format,
            request.CaptureMode,
            progress,
            cancellationToken);

        operationLogService.Record(
            outcome.Warnings.Count == 0 ? OperationSeverity.Info : OperationSeverity.Warning,
            "İmaj",
            outcome.Warnings.Count == 0 ? "İmaj alma tamamlandı." : "İmaj alma uyarılarla tamamlandı.",
            "image.capture.completed",
            new Dictionary<string, string>
            {
                ["source"] = source.Name,
                ["destination"] = request.DestinationPath!,
                ["bytes"] = ByteSizeFormatter.Format(outcome.ProcessedBytes),
                ["captureMode"] = request.CaptureMode.ToString()
            });

        IReadOnlyList<string> notes = request.CaptureMode == ImageCaptureMode.Smart
            ? ["Akıllı imaj yalnızca kullanılan dosyaları içerir; boş alanı ham olarak taşımaz."]
            : ["Ham imaj seçilen sürücünün sektör düzeyindeki içeriğini kaydeder."];

        return new ImageExecutionResult(
            request.Operation,
            ExecutionState.Succeeded,
            "Tamamlandı",
            "İmaj alma işlemi başarıyla tamamlandı.",
            outcome.FormatLabel,
            source.Name,
            request.DestinationPath!,
            outcome.ProcessedBytes,
            outcome.TotalBytes,
            outcome.Warnings,
            notes);
    }

    private async Task<ImageExecutionResult> ExecuteRestoreImageAsync(
        BackupWorkflowRequest request,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var target = diskInventoryService.FindById(request.TargetDiskId!)!;
        var outcome = await _imageContainerService.RestoreImageAsync(request.SourceImagePath!, target, progress, cancellationToken);

        operationLogService.Record(
            outcome.Warnings.Count == 0 ? OperationSeverity.Info : OperationSeverity.Warning,
            "İmaj",
            outcome.Warnings.Count == 0 ? "İmaj geri yükleme tamamlandı." : "İmaj geri yükleme uyarılarla tamamlandı.",
            "image.restore.completed",
            new Dictionary<string, string>
            {
                ["source"] = request.SourceImagePath!,
                ["target"] = target.Name,
                ["bytes"] = ByteSizeFormatter.Format(outcome.ProcessedBytes)
            });

        IReadOnlyList<string> notes = outcome.FormatLabel.Contains("Akıllı", StringComparison.OrdinalIgnoreCase)
            ? ["Akıllı imaj geri yükleme hedef sürücüyü temizler ve dosyaları yeniden yerleştirir."]
            : ["Ham geri yüklemede hedef kapasite imajdan büyükse kalan alan olduğu gibi kalır."];

        return new ImageExecutionResult(
            request.Operation,
            ExecutionState.Succeeded,
            "Tamamlandı",
            "İmaj geri yükleme işlemi başarıyla tamamlandı.",
            outcome.FormatLabel,
            request.SourceImagePath!,
            target.Name,
            outcome.ProcessedBytes,
            outcome.TotalBytes,
            outcome.Warnings,
            notes);
    }

    private async Task<ImageExecutionResult> ExecuteConvertImageAsync(
        BackupWorkflowRequest request,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var outcome = await _imageContainerService.ConvertAsync(
            request.SourceImagePath!,
            request.DestinationPath!,
            request.Format,
            progress,
            cancellationToken);

        operationLogService.Record(
            OperationSeverity.Info,
            "İmaj",
            "İmaj dönüştürme tamamlandı.",
            "image.convert.completed",
            new Dictionary<string, string>
            {
                ["source"] = request.SourceImagePath!,
                ["destination"] = request.DestinationPath!,
                ["format"] = request.Format.ToString(),
                ["bytes"] = ByteSizeFormatter.Format(outcome.ProcessedBytes)
            });

        return new ImageExecutionResult(
            request.Operation,
            ExecutionState.Succeeded,
            "Tamamlandı",
            "İmaj dönüştürme işlemi başarıyla tamamlandı.",
            outcome.FormatLabel,
            request.SourceImagePath!,
            request.DestinationPath!,
            outcome.ProcessedBytes,
            outcome.TotalBytes,
            outcome.Warnings,
            ["Dönüştürme ham imaj içeriğini yeni kapsayıcıya taşır."]);
    }

    private static bool HasBlockingWarnings(IEnumerable<string> warnings)
    {
        return warnings.Any(warning => !warning.Contains("üzerine yazılacaktır", StringComparison.OrdinalIgnoreCase));
    }

    private static BackupPlanResult BuildResult(
        BackupWorkflowKind operation,
        string statusLabel,
        string summary,
        string nextAction,
        string scopeNote,
        bool canStartNow,
        bool canExportPlan,
        IReadOnlyList<string> warnings,
        IReadOnlyList<WorkflowStepItem> steps)
    {
        var effectiveSummary = summary;
        var effectiveNextAction = nextAction;

        if (!canStartNow && warnings.Count > 0)
        {
            var primaryWarning = warnings[0];
            effectiveSummary = $"{summary} Neden: {primaryWarning}";
            effectiveNextAction = $"{nextAction} Önce şu noktayı çözün: {primaryWarning}";
        }

        return new BackupPlanResult(operation, statusLabel, effectiveSummary, effectiveNextAction, scopeNote, canStartNow, canExportPlan, warnings, steps);
    }

    private static IReadOnlyList<WorkflowStepItem> BuildExecutionSteps(int activeIndex, bool endsWithExecution)
    {
        var steps = new List<WorkflowStepItem>
        {
            new("Kaynağı seçin", "Sürücüyü veya imaj dosyasını belirleyin.", activeIndex == 0, activeIndex > 0),
            new("Hedefi belirleyin", "Hedef sürücüyü veya kayıt yolunu seçin.", activeIndex == 1, activeIndex > 1),
            new("Denetimi çalıştırın", "Boyut, güvenlik ve kapsam kuralları doğrulanır.", activeIndex == 2, activeIndex > 2)
        };

        if (endsWithExecution)
        {
            steps.Add(new WorkflowStepItem("İşlemi başlatın", "İlerlemeyi izleyin, gerekirse iptal edin ve sonucu kaydedin.", activeIndex == 3, activeIndex > 3));
        }

        return steps;
    }
}

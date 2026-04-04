using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;
using System.Runtime.Versioning;

namespace CigerTool.Infrastructure.Cloning;

[SupportedOSPlatform("windows")]
public sealed class CloneWorkflowService(
    IDiskInventoryService diskInventoryService,
    IEnvironmentProfileService environmentProfileService,
    IOperationLogService operationLogService) : ICloneWorkflowService
{
    private const long SmartCloneSafetyBufferBytes = 2L * 1024 * 1024 * 1024;
    private readonly SmartCloneFileSystemExecutor _smartCloneExecutor = new();

    public CloningWorkspaceSnapshot GetSnapshot()
    {
        var candidates = diskInventoryService.GetCurrentDisks();
        var environment = environmentProfileService.GetCurrentProfile();

        return new CloningWorkspaceSnapshot(
            Heading: "Klonlama",
            Summary: "Ham kopya ve akıllı kopya için doğrulama, gerçek yürütme, ilerleme takibi ve sonuç alma adımlarını buradan yönetebilirsiniz.",
            Metrics:
            [
                new CardMetric("Görünen sürücüler", candidates.Count.ToString(), "Klonlama için seçilebilir sürücüler."),
                new CardMetric("Ham kopya", "Gerçek bayt kopyası", "Kaynağın tüm içeriği hedefe yazılır. Hedef en az kaynak kadar büyük olmalıdır."),
                new CardMetric("Akıllı kopya", "Dosya temelli eşleme", "Hedef kökü temizlenir ve erişilebilen dosyalar aynalanır."),
                new CardMetric(
                    "Çalışma ortamı",
                    environment.IsWinPe ? "Servis ortamı" : "Masaüstü",
                    environment.IsWinPe
                        ? "Çevrim dışı disk işlemleri için en güvenli çalışma biçimi."
                        : "Aktif sistem sürücüsü için bazı canlı işlem sınırları uygulanır.")
            ],
            Candidates: candidates,
            Recommendations:
            [
                "Ham kopya yalnızca yönetici yetkisiyle ve uygun boyuttaki hedefte başlatılır.",
                "Akıllı kopya hedef kökünü temizler; işlemi başlatmadan önce hedefi doğrulayın.",
                "Aktif Windows sistemini en tutarlı biçimde taşımak için CigerTool OS ile açmak önerilir."
            ],
            RawCloneEnabled: candidates.Count >= 2,
            SmartCloneEnabled: candidates.Count >= 2);
    }

    public CloneAnalysisResult Analyze(CloneWorkflowRequest request)
    {
        var mode = request.Mode;
        var candidates = diskInventoryService.GetCurrentDisks();
        var environment = environmentProfileService.GetCurrentProfile();
        var source = candidates.FirstOrDefault(disk => string.Equals(disk.Id, request.SourceId, StringComparison.OrdinalIgnoreCase));
        var target = candidates.FirstOrDefault(disk => string.Equals(disk.Id, request.TargetId, StringComparison.OrdinalIgnoreCase));
        var checks = new List<CloneAnalysisCheck>();

        if (source is null || target is null)
        {
            return BuildBlockedResult(
                mode,
                source?.Name ?? "Kaynak seçilmedi",
                target?.Name ?? "Hedef seçilmedi",
                "Devam etmek için hem kaynak hem hedef seçilmelidir.",
                "Kaynak ve hedef seçimlerini tamamlayın.",
                checks);
        }

        if (string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new CloneAnalysisCheck("Seçim hatası", "Kaynak ve hedef aynı sürücü olamaz.", OperationSeverity.Error));
            return BuildBlockedResult(
                mode,
                source.Name,
                target.Name,
                "Kaynak ve hedef aynı sürücü olduğu için işlem durduruldu.",
                "Farklı bir hedef seçin.",
                checks);
        }

        if (!source.IsReady || !target.IsReady)
        {
            checks.Add(new CloneAnalysisCheck("Hazırlık", "Sürücülerden biri şu an kullanılamıyor.", OperationSeverity.Error));
            return BuildBlockedResult(
                mode,
                source.Name,
                target.Name,
                "Hazır olmayan sürücüler ile klonlama yapılamaz.",
                "Sürücüyü yeniden bağlayıp yenileyin.",
                checks);
        }

        if (!environment.IsWinPe && target.IsSystemVolume)
        {
            checks.Add(new CloneAnalysisCheck("Aktif sistem hedefi", "Çalışan sistem sürücüsü masaüstünde hedef olarak seçilemez.", OperationSeverity.Error));
            return BuildBlockedResult(
                mode,
                source.Name,
                target.Name,
                "Aktif sistem sürücüsüne masaüstünde yazılamaz.",
                $"Hedef {target.CapacityLabel} / Kaynak {source.CapacityLabel}",
                checks);
        }

        if (source.IsSystemVolume && !environment.IsWinPe && mode == CloneMode.Smart)
        {
            checks.Add(new CloneAnalysisCheck("Servis ortamı önerisi", "Aktif sistem kaynağı için CigerTool OS ile başlatmak daha güvenlidir.", OperationSeverity.Warning));
        }

        return mode == CloneMode.Raw
            ? AnalyzeRawClone(source, target, checks, environment.IsWinPe)
            : AnalyzeSmartClone(source, target, checks);
    }

    public async Task<CloneExecutionResult> ExecuteAsync(
        CloneWorkflowRequest request,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var analysis = Analyze(request);
        if (!analysis.CanProceed)
        {
            return new CloneExecutionResult(
                State: ExecutionState.Failed,
                StatusLabel: "Başlatılamadı",
                Summary: analysis.Summary,
                ModeLabel: GetModeLabel(request.Mode),
                SourceName: analysis.SourceName,
                TargetName: analysis.TargetName,
                ProcessedBytes: 0,
                TotalBytes: 0,
                CopiedItems: 0,
                Warnings: analysis.Checks.Where(check => check.Severity != OperationSeverity.Info).Select(check => check.Message).ToArray(),
                Notes: ["İşlem başlatılmadan önce engeller kaldırılmalıdır."]);
        }

        var source = diskInventoryService.FindById(request.SourceId!);
        var target = diskInventoryService.FindById(request.TargetId!);
        if (source is null || target is null)
        {
            return new CloneExecutionResult(
                ExecutionState.Failed,
                "Başlatılamadı",
                "İşlem başlatılırken kaynak veya hedef sürücü yeniden bulunamadı.",
                GetModeLabel(request.Mode),
                source?.Name ?? "Kaynak",
                target?.Name ?? "Hedef",
                0,
                0,
                0,
                [],
                ["Sürücü listesini yenileyip yeniden deneyin."]);
        }

        try
        {
            return request.Mode == CloneMode.Raw
                ? await ExecuteRawCloneAsync(source, target, progress, cancellationToken)
                : await ExecuteSmartCloneAsync(source, target, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "Klonlama",
                "Klonlama işlemi kullanıcı tarafından iptal edildi.",
                "cloning.canceled",
                new Dictionary<string, string>
                {
                    ["mode"] = request.Mode.ToString(),
                    ["source"] = source.Name,
                    ["target"] = target.Name
                });

            return new CloneExecutionResult(
                ExecutionState.Canceled,
                "İptal edildi",
                "Klonlama işlemi yarıda durduruldu. Hedef sürücüde kısmi veri bulunabilir.",
                GetModeLabel(request.Mode),
                source.Name,
                target.Name,
                0,
                request.Mode == CloneMode.Raw ? source.TotalBytes : source.UsedBytes,
                0,
                ["İptal edilen işlemlerde hedef içerik tutarsız olabilir."],
                ["İşlemi yeniden çalıştırmadan önce hedefi yeniden hazırlamanız önerilir."]);
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Error,
                "Klonlama",
                $"Klonlama başarısız oldu: {ex.Message}",
                "cloning.failed",
                new Dictionary<string, string>
                {
                    ["mode"] = request.Mode.ToString(),
                    ["source"] = source.Name,
                    ["target"] = target.Name
                });

            return new CloneExecutionResult(
                ExecutionState.Failed,
                "Başarısız",
                $"Klonlama tamamlanamadı: {ex.Message}",
                GetModeLabel(request.Mode),
                source.Name,
                target.Name,
                0,
                request.Mode == CloneMode.Raw ? source.TotalBytes : source.UsedBytes,
                0,
                [ex.Message],
                ["Ayrıntılar Günlükler bölümüne kaydedildi."]);
        }
    }

    private CloneAnalysisResult AnalyzeRawClone(DiskSummary source, DiskSummary target, List<CloneAnalysisCheck> checks, bool isWinPe)
    {
        checks.Add(new CloneAnalysisCheck("Kural", "Ham kopya kaynak sürücünün tüm bayt içeriğini hedefe yazar.", OperationSeverity.Info));

        if (!RawVolumeAccessScope.IsAdministrator())
        {
            checks.Add(new CloneAnalysisCheck("Yönetici izni", "Ham kopya için uygulamanın yönetici yetkisiyle çalışması gerekir.", OperationSeverity.Error));
            return FinalizeResult(
                CloneMode.Raw,
                source,
                target,
                CloneSuitabilityStatus.Blocked,
                "Ham kopya başlatılamadı; yönetici yetkisi gerekiyor.",
                source.TotalBytes,
                target.TotalBytes,
                checks);
        }

        if (source.IsSystemVolume && !isWinPe)
        {
            checks.Add(new CloneAnalysisCheck("Aktif sistem kaynağı", "Masaüstünde çalışan sistem sürücüsü için ham kopya engellidir.", OperationSeverity.Error));
            return FinalizeResult(
                CloneMode.Raw,
                source,
                target,
                CloneSuitabilityStatus.Blocked,
                "Aktif sistem sürücüsü ham kopya için yalnızca servis ortamında desteklenir.",
                source.TotalBytes,
                target.TotalBytes,
                checks);
        }

        if (target.TotalBytes < source.TotalBytes)
        {
            checks.Add(new CloneAnalysisCheck("Boyut yetersiz", "Hedef kapasite kaynak sürücünün toplam boyutundan küçük.", OperationSeverity.Error));
            return FinalizeResult(
                CloneMode.Raw,
                source,
                target,
                CloneSuitabilityStatus.Blocked,
                "Ham kopya için hedef sürücünün en az kaynak kadar büyük olması gerekir.",
                source.TotalBytes,
                target.TotalBytes,
                checks);
        }

        if (target.IsRemovable)
        {
            checks.Add(new CloneAnalysisCheck("Çıkarılabilir hedef", "USB hedeflerde yazma süresi daha uzun olabilir.", OperationSeverity.Warning));
        }

        checks.Add(new CloneAnalysisCheck("Boyut uygunluğu", "Hedef kapasite ham kopya için yeterli.", OperationSeverity.Info));
        return FinalizeResult(
            CloneMode.Raw,
            source,
            target,
            checks.Any(check => check.Severity == OperationSeverity.Warning) ? CloneSuitabilityStatus.Caution : CloneSuitabilityStatus.Ready,
            "Ham kopya başlatılabilir görünüyor.",
            source.TotalBytes,
            target.TotalBytes,
            checks);
    }

    private CloneAnalysisResult AnalyzeSmartClone(DiskSummary source, DiskSummary target, List<CloneAnalysisCheck> checks)
    {
        checks.Add(new CloneAnalysisCheck("Kural", "Akıllı kopya erişilebilen dosyaları hedef köküne eşler.", OperationSeverity.Info));

        if (!string.Equals(source.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new CloneAnalysisCheck("Dosya sistemi", "Bu sürümde akıllı kopya yalnızca NTFS kaynaklar için açılır.", OperationSeverity.Error));
            return FinalizeResult(
                CloneMode.Smart,
                source,
                target,
                CloneSuitabilityStatus.Blocked,
                "Akıllı kopya bu kaynak için kullanılamıyor.",
                source.UsedBytes,
                target.TotalBytes,
                checks);
        }

        var requiredBytes = source.UsedBytes + Math.Max(SmartCloneSafetyBufferBytes, source.TotalBytes / 20);

        if (target.TotalBytes < requiredBytes)
        {
            checks.Add(new CloneAnalysisCheck("Kapasite", "Hedef kapasite kullanılan veri ve güvenlik tamponunu karşılamıyor.", OperationSeverity.Error));
            return FinalizeResult(
                CloneMode.Smart,
                source,
                target,
                CloneSuitabilityStatus.Blocked,
                "Akıllı kopya için hedefte yeterli alan yok.",
                requiredBytes,
                target.TotalBytes,
                checks);
        }

        if (target.TotalBytes < source.TotalBytes)
        {
            checks.Add(new CloneAnalysisCheck("Daha küçük hedef", "Hedef kaynak sürücüden küçük, ancak kullanılan veriler şu an sığıyor.", OperationSeverity.Warning));
        }

        if (target.FreeBytes < source.UsedBytes)
        {
            checks.Add(new CloneAnalysisCheck("Boş alan görünümü", "Hedefte görünen boş alan kullanılan veri miktarından daha düşük.", OperationSeverity.Warning));
        }

        checks.Add(new CloneAnalysisCheck("Uygunluk", "Akıllı kopya için temel kapasite denetimi olumlu sonuç verdi.", OperationSeverity.Info));
        return FinalizeResult(
            CloneMode.Smart,
            source,
            target,
            checks.Any(check => check.Severity == OperationSeverity.Warning) ? CloneSuitabilityStatus.Caution : CloneSuitabilityStatus.Ready,
            "Akıllı kopya başlatılabilir görünüyor.",
            requiredBytes,
            target.TotalBytes,
            checks);
    }

    private async Task<CloneExecutionResult> ExecuteRawCloneAsync(
        DiskSummary source,
        DiskSummary target,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        operationLogService.Record(
            OperationSeverity.Info,
            "Klonlama",
            "Ham kopya başlatıldı.",
            "cloning.raw.started",
            new Dictionary<string, string>
            {
                ["source"] = source.Name,
                ["target"] = target.Name
            });

        using var sourceScope = RawVolumeAccessScope.OpenRead(source.DriveLetter);
        using var targetScope = RawVolumeAccessScope.OpenWrite(target.DriveLetter);
        sourceScope.Stream.Position = 0;
        targetScope.Stream.Position = 0;

        var processedBytes = await StreamCopyExecutor.CopyAsync(
            sourceScope.Stream,
            targetScope.Stream,
            source.TotalBytes,
            "Ham kopya",
            "Kaynak sürücünün bayt içeriği hedefe yazılıyor.",
            progress,
            cancellationToken,
            $"{source.DriveLetter} → {target.DriveLetter}");

        operationLogService.Record(
            OperationSeverity.Info,
            "Klonlama",
            "Ham kopya tamamlandı.",
            "cloning.raw.completed",
            new Dictionary<string, string>
            {
                ["source"] = source.Name,
                ["target"] = target.Name,
                ["bytes"] = ByteSizeFormatter.Format(processedBytes)
            });

        return new CloneExecutionResult(
            ExecutionState.Succeeded,
            "Tamamlandı",
            "Ham kopya işlemi başarıyla tamamlandı.",
            GetModeLabel(CloneMode.Raw),
            source.Name,
            target.Name,
            processedBytes,
            source.TotalBytes,
            1,
            [],
            ["Hedef kapasite kaynaktan büyükse kalan bölüm değiştirilmeden kalır."]);
    }

    private async Task<CloneExecutionResult> ExecuteSmartCloneAsync(
        DiskSummary source,
        DiskSummary target,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        operationLogService.Record(
            OperationSeverity.Info,
            "Klonlama",
            "Akıllı kopya başlatıldı.",
            "cloning.smart.started",
            new Dictionary<string, string>
            {
                ["source"] = source.Name,
                ["target"] = target.Name
            });

        var result = await _smartCloneExecutor.MirrorAsync(
            NormalizeRootPath(source.DriveLetter),
            NormalizeRootPath(target.DriveLetter),
            progress,
            cancellationToken);

        var state = result.Warnings.Count == 0 ? ExecutionState.Succeeded : ExecutionState.CompletedWithWarnings;
        operationLogService.Record(
            result.Warnings.Count == 0 ? OperationSeverity.Info : OperationSeverity.Warning,
            "Klonlama",
            result.Warnings.Count == 0
                ? "Akıllı kopya tamamlandı."
                : "Akıllı kopya uyarılarla tamamlandı.",
            "cloning.smart.completed",
            new Dictionary<string, string>
            {
                ["source"] = source.Name,
                ["target"] = target.Name,
                ["bytes"] = ByteSizeFormatter.Format(result.ProcessedBytes),
                ["warnings"] = result.Warnings.Count.ToString()
            });

        return new CloneExecutionResult(
            state,
            state == ExecutionState.Succeeded ? "Tamamlandı" : "Uyarılarla tamamlandı",
            state == ExecutionState.Succeeded
                ? "Akıllı kopya işlemi başarıyla tamamlandı."
                : "Akıllı kopya bitti, ancak bazı dosyalar taşınamadı veya temizlenemedi.",
            GetModeLabel(CloneMode.Smart),
            source.Name,
            target.Name,
            result.ProcessedBytes,
            result.TotalBytes,
            result.CopiedItems,
            result.Warnings,
            [
                "Akıllı kopya hedef kökünü temizler ve erişilebilen dosyaları eşler.",
                "Masaüstünde çalışan sistem kaynağında kilitli dosyalar atlanabilir."
            ]);
    }

    private CloneAnalysisResult FinalizeResult(
        CloneMode mode,
        DiskSummary source,
        DiskSummary target,
        CloneSuitabilityStatus status,
        string summary,
        long requiredBytes,
        long targetBytes,
        IReadOnlyList<CloneAnalysisCheck> checks)
    {
        operationLogService.Record(
            status == CloneSuitabilityStatus.Blocked ? OperationSeverity.Warning : OperationSeverity.Info,
            "Klonlama",
            summary,
            "cloning.analysis",
            new Dictionary<string, string>
            {
                ["mode"] = mode.ToString(),
                ["source"] = source.Name,
                ["target"] = target.Name,
                ["required"] = ByteSizeFormatter.Format(requiredBytes),
                ["targetCapacity"] = ByteSizeFormatter.Format(targetBytes),
                ["status"] = status.ToString()
            });

        return new CloneAnalysisResult(
            Mode: mode,
            Status: status,
            CanProceed: status != CloneSuitabilityStatus.Blocked,
            Summary: summary,
            SourceName: source.Name,
            TargetName: target.Name,
            RequiredCapacityLabel: ByteSizeFormatter.Format(requiredBytes),
            ComparisonLabel: $"Hedef {ByteSizeFormatter.Format(targetBytes)} / Gereken {ByteSizeFormatter.Format(requiredBytes)}",
            Checks: checks);
    }

    private CloneAnalysisResult BuildBlockedResult(
        CloneMode mode,
        string sourceName,
        string targetName,
        string summary,
        string comparisonLabel,
        IReadOnlyList<CloneAnalysisCheck> checks)
    {
        operationLogService.Record(
            OperationSeverity.Warning,
            "Klonlama",
            summary,
            "cloning.analysis.blocked",
            new Dictionary<string, string>
            {
                ["mode"] = mode.ToString(),
                ["source"] = sourceName,
                ["target"] = targetName,
                ["comparison"] = comparisonLabel
            });

        return new CloneAnalysisResult(
            Mode: mode,
            Status: CloneSuitabilityStatus.Blocked,
            CanProceed: false,
            Summary: summary,
            SourceName: sourceName,
            TargetName: targetName,
            RequiredCapacityLabel: "Belirlenmedi",
            ComparisonLabel: comparisonLabel,
            Checks: checks);
    }

    private static string NormalizeRootPath(string driveLetter)
    {
        var trimmed = (driveLetter ?? string.Empty).Trim();
        return trimmed.EndsWith('\\') ? trimmed : $"{trimmed}\\";
    }

    private static string GetModeLabel(CloneMode mode) => mode switch
    {
        CloneMode.Raw => "Ham kopya",
        CloneMode.Smart => "Akıllı kopya",
        _ => mode.ToString()
    };
}

using System.Runtime.Versioning;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Disks;

[SupportedOSPlatform("windows")]
public sealed class DiskSurfaceScanService(IOperationLogService operationLogService) : IDiskSurfaceScanService
{
    private const int ChunkSizeBytes = 8 * 1024 * 1024;
    private const int MaxReportedRanges = 64;

    public async Task<DiskSurfaceScanResult> RunAsync(
        DiskSummary disk,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!RawVolumeAccessScope.IsAdministrator())
        {
            return BuildFailure(disk, "Yüzey taraması için uygulamayı yönetici olarak açın.");
        }

        if (string.IsNullOrWhiteSpace(disk.DriveLetter))
        {
            return BuildFailure(disk, "Yüzey taraması için erişilebilir bir sürücü harfi gerekiyor.");
        }

        if (!disk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase))
        {
            return BuildFailure(disk, "Yüzey taraması şu anda yalnızca HDD ve USB HDD sürücüler için açıktır.");
        }

        if (disk.IsSystemVolume)
        {
            return BuildFailure(disk, "Çalışan sistem HDD'sinde derin yüzey taraması için WinPE ortamını kullanın.");
        }

        var totalBytes = Math.Max(disk.TotalBytes, 0);
        if (totalBytes <= 0)
        {
            return BuildFailure(disk, "Taranacak sürücü boyutu belirlenemedi.");
        }

        operationLogService.Record(
            OperationSeverity.Info,
            "Diskler",
            "HDD yüzey taraması başlatıldı.",
            "disk.surface.start",
            new Dictionary<string, string>
            {
                ["disk"] = disk.Name,
                ["drive"] = disk.DriveLetter,
                ["mediaType"] = disk.MediaType
            });

        RawVolumeAccessScope? scope = null;
        var buffer = new byte[ChunkSizeBytes];
        var findings = new List<string>();
        long scannedBytes = 0;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            scope = RawVolumeAccessScope.OpenRead(disk.DriveLetter);

            while (scannedBytes < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var offset = scannedBytes;
                var bytesToRead = (int)Math.Min(buffer.Length, totalBytes - scannedBytes);

                try
                {
                    scope.Stream.Position = offset;

                    var readTotal = 0;
                    while (readTotal < bytesToRead)
                    {
                        var read = await scope.Stream.ReadAsync(
                            buffer.AsMemory(readTotal, bytesToRead - readTotal),
                            cancellationToken);

                        if (read == 0)
                        {
                            break;
                        }

                        readTotal += read;
                    }

                    if (readTotal <= 0)
                    {
                        break;
                    }

                    scannedBytes += readTotal;
                    ReportProgress(progress, scannedBytes, totalBytes, findings.Count, startedAt);
                }
                catch (IOException ex)
                {
                    var rangeEnd = Math.Min(totalBytes - 1, offset + bytesToRead - 1);
                    if (findings.Count < MaxReportedRanges)
                    {
                        findings.Add($"{FormatBytes(offset)} - {FormatBytes(rangeEnd)} aralığında okuma hatası: {ex.Message}");
                    }

                    operationLogService.Record(
                        OperationSeverity.Warning,
                        "Diskler",
                        "Yüzey taramasında okunamayan aralık algılandı.",
                        "disk.surface.badrange",
                        new Dictionary<string, string>
                        {
                            ["disk"] = disk.Name,
                            ["offsetStart"] = offset.ToString(),
                            ["offsetEnd"] = rangeEnd.ToString(),
                            ["error"] = ex.Message
                        });

                    scannedBytes = offset + bytesToRead;
                    scope.Dispose();
                    scope = RawVolumeAccessScope.OpenRead(disk.DriveLetter);
                    ReportProgress(progress, scannedBytes, totalBytes, findings.Count, startedAt);
                }
            }

            var result = findings.Count == 0
                ? new DiskSurfaceScanResult(
                    disk.Name,
                    ExecutionState.Succeeded,
                    "Tamamlandı",
                    "Derin okuma taramasında bozuk sektör belirtisi görülmedi.",
                    FormatBytes(totalBytes),
                    "0 aralık",
                    ["Tarama yalnızca okunamayan fiziksel aralıkları arar; dosya sistemi tutarlılığı için ayrıca CHKDSK önerilir."])
                : new DiskSurfaceScanResult(
                    disk.Name,
                    ExecutionState.CompletedWithWarnings,
                    "Uyarı var",
                    $"{findings.Count} okunamayan aralık bulundu. Sürücüyü yedekleyip değiştirme planı yapın.",
                    FormatBytes(totalBytes),
                    $"{findings.Count} aralık",
                    findings);

            operationLogService.Record(
                result.State == ExecutionState.Succeeded ? OperationSeverity.Info : OperationSeverity.Warning,
                "Diskler",
                "HDD yüzey taraması tamamlandı.",
                "disk.surface.complete",
                new Dictionary<string, string>
                {
                    ["disk"] = disk.Name,
                    ["badRangeCount"] = findings.Count.ToString(),
                    ["scannedBytes"] = totalBytes.ToString()
                });

            return result;
        }
        catch (OperationCanceledException)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "Diskler",
                "HDD yüzey taraması iptal edildi.",
                "disk.surface.canceled",
                new Dictionary<string, string>
                {
                    ["disk"] = disk.Name,
                    ["scannedBytes"] = scannedBytes.ToString()
                });

            return new DiskSurfaceScanResult(
                disk.Name,
                ExecutionState.Canceled,
                "İptal edildi",
                "Yüzey taraması kullanıcı tarafından durduruldu.",
                FormatBytes(scannedBytes),
                $"{findings.Count} aralık",
                findings.Count == 0 ? ["İptal edilmeden önce okunamayan aralık saptanmadı."] : findings);
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Error,
                "Diskler",
                $"HDD yüzey taraması başarısız oldu: {ex.Message}",
                "disk.surface.failure",
                new Dictionary<string, string>
                {
                    ["disk"] = disk.Name,
                    ["error"] = ex.Message
                });

            return BuildFailure(disk, $"Yüzey taraması tamamlanamadı: {ex.Message}");
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private static DiskSurfaceScanResult BuildFailure(DiskSummary disk, string message)
    {
        return new DiskSurfaceScanResult(
            disk.Name,
            ExecutionState.Failed,
            "Başarısız",
            message,
            "-",
            "-",
            [message]);
    }

    private static void ReportProgress(
        IProgress<OperationProgressSnapshot>? progress,
        long scannedBytes,
        long totalBytes,
        int badRangeCount,
        DateTimeOffset startedAt)
    {
        if (progress is null)
        {
            return;
        }

        var percent = totalBytes <= 0 ? 0 : Math.Clamp(scannedBytes * 100d / totalBytes, 0d, 100d);
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var bytesPerSecond = elapsed.TotalSeconds <= 0 ? 0d : scannedBytes / elapsed.TotalSeconds;
        var remaining = bytesPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, totalBytes - scannedBytes) / bytesPerSecond);

        progress.Report(new OperationProgressSnapshot(
            "HDD yüzey taraması",
            badRangeCount == 0
                ? "Disk yüzeyi okunuyor."
                : $"{badRangeCount} sorunlu aralık algılandı, tarama devam ediyor.",
            percent,
            false,
            scannedBytes,
            totalBytes,
            FormatBytes(scannedBytes),
            FormatBytes(totalBytes),
            bytesPerSecond <= 0 ? "-" : $"{(bytesPerSecond / 1024d / 1024d):0.0} MB/sn",
            bytesPerSecond <= 0 ? "Hesaplanıyor" : ByteSizeFormatter.FormatDuration(remaining),
            null));
    }

    private static string FormatBytes(long value) => ByteSizeFormatter.Format(Math.Max(0, value));
}

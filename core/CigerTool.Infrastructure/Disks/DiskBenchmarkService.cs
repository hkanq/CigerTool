using System.Diagnostics;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Disks;

public sealed class DiskBenchmarkService(
    IOperationLogService operationLogService) : IDiskBenchmarkService
{
    private const int BufferSize = 1024 * 1024;
    private const int RandomBlockSize = 4 * 1024;

    public async Task<DiskBenchmarkResult> RunAsync(
        DiskSummary disk,
        DiskBenchmarkProfileOption profile,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(disk.DriveLetter))
        {
            return BuildFailureResult(disk, profile, "Performans testi için erişilebilir sürücü harfi bulunamadı.");
        }

        var benchmarkRoot = Path.Combine(disk.DriveLetter + Path.DirectorySeparatorChar, "CigerTool.Benchmark");
        var benchmarkFile = Path.Combine(benchmarkRoot, "benchmark.tmp");
        var notes = new List<string>();

        operationLogService.Record(
            OperationSeverity.Info,
            "Diskler",
            "Performans testi başlatıldı.",
            "disk.benchmark.start",
            new Dictionary<string, string>
            {
                ["disk"] = disk.Name,
                ["profile"] = profile.Title,
                ["size"] = profile.TestFileSizeBytes.ToString()
            });

        try
        {
            Directory.CreateDirectory(benchmarkRoot);

            var sequentialWrite = await MeasureSequentialWriteAsync(
                benchmarkFile,
                profile.TestFileSizeBytes,
                progress,
                cancellationToken);

            var sequentialRead = await MeasureSequentialReadAsync(
                benchmarkFile,
                profile.TestFileSizeBytes,
                progress,
                cancellationToken);

            var (randomWriteMbps, randomWriteIops) = await MeasureRandomWriteAsync(
                benchmarkFile,
                profile.RandomOperations,
                progress,
                cancellationToken);

            var (randomReadMbps, randomReadIops) = await MeasureRandomReadAsync(
                benchmarkFile,
                profile.RandomOperations,
                progress,
                cancellationToken);

            TryDeleteFile(benchmarkFile);
            TryDeleteDirectory(benchmarkRoot);

            notes.Add("Sıralı ve 4K rastgele testler seçilen sürücü üzerinde geçici test dosyasıyla ölçüldü.");
            if (disk.IsSystemVolume)
            {
                notes.Add("Bu test sistem sürücüsü üzerinde çalıştı; arka plandaki işletim sistemi etkinliği sonuçları etkileyebilir.");
            }

            notes.Add(BuildPerformanceComment(disk, sequentialRead, sequentialWrite, randomReadIops, randomWriteIops));

            operationLogService.Record(
                OperationSeverity.Info,
                "Diskler",
                "Performans testi tamamlandı.",
                "disk.benchmark.complete",
                new Dictionary<string, string>
                {
                    ["disk"] = disk.Name,
                    ["seqRead"] = $"{sequentialRead:0.0}",
                    ["seqWrite"] = $"{sequentialWrite:0.0}",
                    ["rndRead"] = $"{randomReadMbps:0.0}",
                    ["rndWrite"] = $"{randomWriteMbps:0.0}"
                });

            return new DiskBenchmarkResult(
                disk.Name,
                profile.Title,
                ExecutionState.Succeeded,
                "Tamamlandı",
                "Performans testi başarıyla tamamlandı.",
                ByteSizeFormatter.Format(profile.TestFileSizeBytes),
                FormatSpeed(sequentialRead),
                FormatSpeed(sequentialWrite),
                FormatSpeed(randomReadMbps),
                FormatSpeed(randomWriteMbps),
                $"{randomReadIops:N0} IOPS",
                $"{randomWriteIops:N0} IOPS",
                notes);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(benchmarkFile);
            TryDeleteDirectory(benchmarkRoot);

            operationLogService.Record(
                OperationSeverity.Warning,
                "Diskler",
                "Performans testi iptal edildi.",
                "disk.benchmark.canceled",
                new Dictionary<string, string>
                {
                    ["disk"] = disk.Name
                });

            return new DiskBenchmarkResult(
                disk.Name,
                profile.Title,
                ExecutionState.Canceled,
                "İptal edildi",
                "Performans testi durduruldu.",
                ByteSizeFormatter.Format(profile.TestFileSizeBytes),
                "-",
                "-",
                "-",
                "-",
                "-",
                "-",
                ["Geçici test dosyası temizlenmeye çalışıldı."]);
        }
        catch (Exception ex)
        {
            TryDeleteFile(benchmarkFile);
            TryDeleteDirectory(benchmarkRoot);

            operationLogService.Record(
                OperationSeverity.Error,
                "Diskler",
                $"Performans testi başarısız oldu: {ex.Message}",
                "disk.benchmark.failure",
                new Dictionary<string, string>
                {
                    ["disk"] = disk.Name,
                    ["error"] = ex.Message
                });

            return BuildFailureResult(disk, profile, ex.Message);
        }
    }

    private static async Task<double> MeasureSequentialWriteAsync(
        string benchmarkFile,
        long totalBytes,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        Random.Shared.NextBytes(buffer);
        var written = 0L;
        var watch = Stopwatch.StartNew();

        await using var stream = new FileStream(
            benchmarkFile,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (written < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = (int)Math.Min(buffer.Length, totalBytes - written);
            await stream.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken);
            written += chunk;

            ReportProgress(progress, "Sıralı yazma", "Test dosyası yazılıyor.", written, totalBytes, 0, 35, null, watch);
        }

        await stream.FlushAsync(cancellationToken);
        watch.Stop();
        return ToMbps(written, watch.Elapsed);
    }

    private static async Task<double> MeasureSequentialReadAsync(
        string benchmarkFile,
        long totalBytes,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var readTotal = 0L;
        var watch = Stopwatch.StartNew();

        await using var stream = new FileStream(
            benchmarkFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (readTotal < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            readTotal += read;
            ReportProgress(progress, "Sıralı okuma", "Yazılan test dosyası okunuyor.", readTotal, totalBytes, 35, 65, null, watch);
        }

        watch.Stop();
        return ToMbps(readTotal, watch.Elapsed);
    }

    private static async Task<(double Mbps, long Iops)> MeasureRandomWriteAsync(
        string benchmarkFile,
        int operations,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[RandomBlockSize];
        Random.Shared.NextBytes(buffer);

        await using var stream = new FileStream(
            benchmarkFile,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            RandomBlockSize,
            FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.RandomAccess);

        var maxBlockIndex = Math.Max(1, (int)(stream.Length / RandomBlockSize) - 1);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < operations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = Random.Shared.NextInt64(0, maxBlockIndex) * RandomBlockSize;
            stream.Position = offset;
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

            var processed = (index + 1L) * RandomBlockSize;
            ReportProgress(progress, "4K rastgele yazma", "Küçük blok yazma performansı ölçülüyor.", processed, operations * 1L * RandomBlockSize, 65, 82, null, watch);
        }

        await stream.FlushAsync(cancellationToken);
        watch.Stop();
        return (ToMbps(operations * 1L * RandomBlockSize, watch.Elapsed), ToIops(operations, watch.Elapsed));
    }

    private static async Task<(double Mbps, long Iops)> MeasureRandomReadAsync(
        string benchmarkFile,
        int operations,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[RandomBlockSize];
        await using var stream = new FileStream(
            benchmarkFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            RandomBlockSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var maxBlockIndex = Math.Max(1, (int)(stream.Length / RandomBlockSize) - 1);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < operations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = Random.Shared.NextInt64(0, maxBlockIndex) * RandomBlockSize;
            stream.Position = offset;
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var processed = (index + 1L) * RandomBlockSize;
            ReportProgress(progress, "4K rastgele okuma", "Küçük blok okuma performansı ölçülüyor.", processed, operations * 1L * RandomBlockSize, 82, 100, null, watch);
        }

        watch.Stop();
        return (ToMbps(operations * 1L * RandomBlockSize, watch.Elapsed), ToIops(operations, watch.Elapsed));
    }

    private static void ReportProgress(
        IProgress<OperationProgressSnapshot>? progress,
        string phaseLabel,
        string summary,
        long processedBytes,
        long totalBytes,
        double startPercent,
        double endPercent,
        string? currentItem,
        Stopwatch watch)
    {
        if (progress is null)
        {
            return;
        }

        var ratio = totalBytes <= 0 ? 0d : Math.Clamp((double)processedBytes / totalBytes, 0d, 1d);
        var percent = startPercent + ((endPercent - startPercent) * ratio);
        var speed = watch.Elapsed.TotalSeconds <= 0
            ? 0d
            : processedBytes / watch.Elapsed.TotalSeconds;
        var remainingSeconds = speed <= 0 || totalBytes <= processedBytes
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((totalBytes - processedBytes) / speed);

        progress.Report(new OperationProgressSnapshot(
            phaseLabel,
            summary,
            percent,
            false,
            processedBytes,
            totalBytes,
            ByteSizeFormatter.Format(processedBytes),
            ByteSizeFormatter.Format(totalBytes),
            $"{ToMbps(processedBytes, watch.Elapsed):0.0} MB/sn",
            ByteSizeFormatter.FormatDuration(remainingSeconds),
            currentItem));
    }

    private static double ToMbps(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0 || bytes <= 0)
        {
            return 0d;
        }

        return bytes / 1024d / 1024d / elapsed.TotalSeconds;
    }

    private static long ToIops(int operations, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0 || operations <= 0)
        {
            return 0;
        }

        return (long)Math.Round(operations / elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
    }

    private static string FormatSpeed(double value)
    {
        return value <= 0 ? "-" : $"{value:0.0} MB/sn";
    }

    private static string BuildPerformanceComment(
        DiskSummary disk,
        double sequentialRead,
        double sequentialWrite,
        long randomReadIops,
        long randomWriteIops)
    {
        var averageSequential = (sequentialRead + sequentialWrite) / 2d;
        var averageRandomIops = (randomReadIops + randomWriteIops) / 2d;

        if (disk.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || averageSequential >= 1800d)
        {
            return "Sonuçlar üst seviye NVMe sınıfına yakın görünüyor. Yine de gerçek iş yüklerinde sıcaklık ve kontrolcü davranışı sonuçları değiştirebilir.";
        }

        if (disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) || averageSequential >= 350d)
        {
            return averageRandomIops >= 20_000
                ? "Sonuçlar tipik SSD düzeyinde ve günlük kullanım için güçlü görünüyor."
                : "Sıralı hızlar SSD düzeyinde, ancak küçük blok erişimlerinde arka plan yükü veya kontrolcü etkisi görülebilir.";
        }

        if (disk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase) || averageSequential < 250d)
        {
            return "Sonuçlar mekanik disk veya USB kutusu sınıfına yakın görünüyor. Özellikle küçük blok erişimlerinde bu davranış normal kabul edilir.";
        }

        return "Sonuçlar karışık depolama sınıfı gösteriyor; bağlantı türü, USB köprüsü veya denetleyici etkisi altında olabilir.";
    }

    private static DiskBenchmarkResult BuildFailureResult(DiskSummary disk, DiskBenchmarkProfileOption profile, string message)
    {
        return new DiskBenchmarkResult(
            disk.Name,
            profile.Title,
            ExecutionState.Failed,
            "Başarısız",
            $"Performans testi tamamlanamadı: {message}",
            ByteSizeFormatter.Format(profile.TestFileSizeBytes),
            "-",
            "-",
            "-",
            "-",
            "-",
            "-",
            [message]);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
        }
    }
}

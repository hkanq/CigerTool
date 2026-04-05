using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Disks;

[SupportedOSPlatform("windows")]
public sealed class DiskBenchmarkService(IOperationLogService operationLogService) : IDiskBenchmarkService
{
    private const int BufferSize = 1024 * 1024;
    private const int RandomBlockSize = 4 * 1024;
    private static readonly Regex SpeedRegex = new(@"(?<value>\d+(?:[.,]\d+)?)\s+MB/s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        operationLogService.Record(
            OperationSeverity.Info,
            "Diskler",
            "Performans testi başlatıldı.",
            "disk.benchmark.start",
            new Dictionary<string, string>
            {
                ["disk"] = disk.Name,
                ["profile"] = profile.Title
            });

        try
        {
            if (CanUseWinSat())
            {
                var result = await RunWinSatBenchmarkAsync(disk, profile, progress, cancellationToken);
                if (result is not null)
                {
                    return result;
                }
            }

            return await RunFallbackBenchmarkAsync(disk, profile, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            operationLogService.Record(OperationSeverity.Warning, "Diskler", "Performans testi iptal edildi.", "disk.benchmark.canceled");
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
            operationLogService.Record(
                OperationSeverity.Error,
                "Diskler",
                $"Performans testi başarısız oldu: {ex.Message}",
                "disk.benchmark.failure",
                new Dictionary<string, string> { ["disk"] = disk.Name, ["error"] = ex.Message });
            return BuildFailureResult(disk, profile, ex.Message);
        }
    }

    private static bool CanUseWinSat()
    {
        var winSatPath = Path.Combine(global::System.Environment.SystemDirectory, "winsat.exe");
        return File.Exists(winSatPath);
    }

    private async Task<DiskBenchmarkResult?> RunWinSatBenchmarkAsync(
        DiskSummary disk,
        DiskBenchmarkProfileOption profile,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var driveLetter = disk.DriveLetter.TrimEnd(':');
        var results = new List<double>();
        var notes = new List<string> { "Ölçüm yöntemi: yerel Windows disk değerlendirmesi (WinSAT)." };
        var phases = new[]
        {
            new BenchmarkPhase("SEQ1M Q1T1 Okuma", "Sıralı okuma ölçülüyor.", $"disk -drive {driveLetter} -seq -read"),
            new BenchmarkPhase("SEQ1M Q1T1 Yazma", "Sıralı yazma ölçülüyor.", $"disk -drive {driveLetter} -seq -write"),
            new BenchmarkPhase("RND4K Q1T1 Okuma", "4K rastgele okuma ölçülüyor.", $"disk -drive {driveLetter} -ran -read -ransize 4096"),
            new BenchmarkPhase("RND4K Q1T1 Yazma", "4K rastgele yazma ölçülüyor.", $"disk -drive {driveLetter} -ran -write -ransize 4096")
        };

        for (var index = 0; index < phases.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phase = phases[index];
            ReportIndeterminate(progress, phase.Label, phase.Summary, index * 25);
            var output = await RunProcessAsync(Path.Combine(global::System.Environment.SystemDirectory, "winsat.exe"), phase.Arguments, cancellationToken);
            var speed = TryParseLastSpeed(output);
            if (speed is null)
            {
                notes.Add($"{phase.Label}: WinSAT çıktısı doğrudan çözülemedi, yerel yedek ölçüme dönülecek.");
                return null;
            }

            results.Add(speed.Value);
            progress?.Report(new OperationProgressSnapshot(
                phase.Label,
                phase.Summary,
                (index + 1) * 25,
                false,
                index + 1,
                phases.Length,
                (index + 1).ToString(),
                phases.Length.ToString(),
                $"{speed.Value:0.0} MB/sn",
                "Tamamlandı",
                null));
        }

        var sequentialRead = results[0];
        var sequentialWrite = results[1];
        var randomRead = results[2];
        var randomWrite = results[3];
        var randomReadIops = ToIops(randomRead, RandomBlockSize);
        var randomWriteIops = ToIops(randomWrite, RandomBlockSize);
        notes.Add(BuildPerformanceComment(disk, sequentialRead, sequentialWrite, randomReadIops, randomWriteIops));

        operationLogService.Record(
            OperationSeverity.Info,
            "Diskler",
            "Performans testi tamamlandı.",
            "disk.benchmark.complete",
            new Dictionary<string, string>
            {
                ["disk"] = disk.Name,
                ["method"] = "winsat",
                ["seqRead"] = $"{sequentialRead:0.0}",
                ["seqWrite"] = $"{sequentialWrite:0.0}"
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
            FormatSpeed(randomRead),
            FormatSpeed(randomWrite),
            $"{randomReadIops:N0} IOPS",
            $"{randomWriteIops:N0} IOPS",
            notes);
    }

    private async Task<DiskBenchmarkResult> RunFallbackBenchmarkAsync(
        DiskSummary disk,
        DiskBenchmarkProfileOption profile,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var benchmarkRoot = Path.Combine(disk.DriveLetter + Path.DirectorySeparatorChar, "CigerTool.Benchmark");
        var benchmarkFile = Path.Combine(benchmarkRoot, "benchmark.tmp");
        Directory.CreateDirectory(benchmarkRoot);

        try
        {
            var sequentialWrite = await MeasureSequentialWriteAsync(benchmarkFile, profile.TestFileSizeBytes, progress, cancellationToken);
            var sequentialRead = await MeasureSequentialReadAsync(benchmarkFile, profile.TestFileSizeBytes, progress, cancellationToken);
            var (randomWriteMbps, randomWriteIops) = await MeasureRandomWriteAsync(benchmarkFile, profile.RandomOperations, progress, cancellationToken);
            var (randomReadMbps, randomReadIops) = await MeasureRandomReadAsync(benchmarkFile, profile.RandomOperations, progress, cancellationToken);

            TryDeleteFile(benchmarkFile);
            TryDeleteDirectory(benchmarkRoot);

            var notes = new List<string>
            {
                "Ölçüm yöntemi: yerel yedek test. Sonuçlar sistem önbelleği ve arka plan yükünden daha fazla etkilenebilir.",
                BuildPerformanceComment(disk, sequentialRead, sequentialWrite, randomReadIops, randomWriteIops)
            };

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
        finally
        {
            TryDeleteFile(benchmarkFile);
            TryDeleteDirectory(benchmarkRoot);
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }

        return $"{stdout}{System.Environment.NewLine}{stderr}";
    }

    private static double? TryParseLastSpeed(string output)
    {
        var matches = SpeedRegex.Matches(output ?? string.Empty);
        if (matches.Count == 0)
        {
            return null;
        }

        var raw = matches[^1].Groups["value"].Value.Replace(',', '.');
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long ToIops(double mbps, int blockSizeBytes)
    {
        if (mbps <= 0 || blockSizeBytes <= 0)
        {
            return 0;
        }

        return (long)Math.Round((mbps * 1024d * 1024d) / blockSizeBytes, MidpointRounding.AwayFromZero);
    }

    private static void ReportIndeterminate(IProgress<OperationProgressSnapshot>? progress, string phaseLabel, string summary, double percent)
    {
        progress?.Report(new OperationProgressSnapshot(
            phaseLabel,
            summary,
            percent,
            true,
            0,
            0,
            "Hazırlanıyor",
            "Hazırlanıyor",
            "Ölçülüyor",
            "Hesaplanıyor",
            null));
    }

    private static async Task<double> MeasureSequentialWriteAsync(string benchmarkFile, long totalBytes, IProgress<OperationProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        Random.Shared.NextBytes(buffer);
        var written = 0L;
        var watch = Stopwatch.StartNew();
        await using var stream = new FileStream(benchmarkFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (written < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(buffer.Length, totalBytes - written);
            await stream.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken);
            written += chunk;
            ReportProgress(progress, "SEQ1M Q1T1 Yazma", "Sıralı yazma ölçülüyor.", written, totalBytes, 0, 30, watch);
        }

        await stream.FlushAsync(cancellationToken);
        watch.Stop();
        return ToMbps(written, watch.Elapsed);
    }

    private static async Task<double> MeasureSequentialReadAsync(string benchmarkFile, long totalBytes, IProgress<OperationProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var readTotal = 0L;
        var watch = Stopwatch.StartNew();
        await using var stream = new FileStream(benchmarkFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (readTotal < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            readTotal += read;
            ReportProgress(progress, "SEQ1M Q1T1 Okuma", "Sıralı okuma ölçülüyor.", readTotal, totalBytes, 30, 60, watch);
        }

        watch.Stop();
        return ToMbps(readTotal, watch.Elapsed);
    }

    private static async Task<(double Mbps, long Iops)> MeasureRandomWriteAsync(string benchmarkFile, int operations, IProgress<OperationProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[RandomBlockSize];
        Random.Shared.NextBytes(buffer);
        await using var stream = new FileStream(benchmarkFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, RandomBlockSize, FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.RandomAccess);
        var maxBlockIndex = Math.Max(1, (int)(stream.Length / RandomBlockSize) - 1);
        var watch = Stopwatch.StartNew();

        for (var index = 0; index < operations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = Random.Shared.NextInt64(0, maxBlockIndex) * RandomBlockSize;
            stream.Position = offset;
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            ReportProgress(progress, "RND4K Q1T1 Yazma", "4K rastgele yazma ölçülüyor.", (index + 1L) * RandomBlockSize, operations * 1L * RandomBlockSize, 60, 80, watch);
        }

        await stream.FlushAsync(cancellationToken);
        watch.Stop();
        return (ToMbps(operations * 1L * RandomBlockSize, watch.Elapsed), (long)Math.Round(operations / watch.Elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
    }

    private static async Task<(double Mbps, long Iops)> MeasureRandomReadAsync(string benchmarkFile, int operations, IProgress<OperationProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[RandomBlockSize];
        await using var stream = new FileStream(benchmarkFile, FileMode.Open, FileAccess.Read, FileShare.Read, RandomBlockSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var maxBlockIndex = Math.Max(1, (int)(stream.Length / RandomBlockSize) - 1);
        var watch = Stopwatch.StartNew();

        for (var index = 0; index < operations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = Random.Shared.NextInt64(0, maxBlockIndex) * RandomBlockSize;
            stream.Position = offset;
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            ReportProgress(progress, "RND4K Q1T1 Okuma", "4K rastgele okuma ölçülüyor.", (index + 1L) * RandomBlockSize, operations * 1L * RandomBlockSize, 80, 100, watch);
        }

        watch.Stop();
        return (ToMbps(operations * 1L * RandomBlockSize, watch.Elapsed), (long)Math.Round(operations / watch.Elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
    }

    private static void ReportProgress(IProgress<OperationProgressSnapshot>? progress, string phaseLabel, string summary, long processedBytes, long totalBytes, double startPercent, double endPercent, Stopwatch watch)
    {
        if (progress is null) return;
        var ratio = totalBytes <= 0 ? 0d : Math.Clamp((double)processedBytes / totalBytes, 0d, 1d);
        var percent = startPercent + ((endPercent - startPercent) * ratio);
        var speed = watch.Elapsed.TotalSeconds <= 0 ? 0d : processedBytes / watch.Elapsed.TotalSeconds;
        var remainingSeconds = speed <= 0 || totalBytes <= processedBytes ? TimeSpan.Zero : TimeSpan.FromSeconds((totalBytes - processedBytes) / speed);
        progress.Report(new OperationProgressSnapshot(phaseLabel, summary, percent, false, processedBytes, totalBytes, ByteSizeFormatter.Format(processedBytes), ByteSizeFormatter.Format(totalBytes), $"{ToMbps(processedBytes, watch.Elapsed):0.0} MB/sn", ByteSizeFormatter.FormatDuration(remainingSeconds), null));
    }

    private static double ToMbps(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 || bytes <= 0 ? 0d : bytes / 1024d / 1024d / elapsed.TotalSeconds;
    private static string FormatSpeed(double value) => value <= 0 ? "-" : $"{value:0.0} MB/sn";

    private static string BuildPerformanceComment(DiskSummary disk, double sequentialRead, double sequentialWrite, long randomReadIops, long randomWriteIops)
    {
        var averageSequential = (sequentialRead + sequentialWrite) / 2d;
        return disk.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || averageSequential >= 1800d
            ? "Sonuçlar NVMe sınıfına yakın görünüyor."
            : disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) || averageSequential >= 350d
                ? randomReadIops >= 20_000 ? "Sonuçlar tipik SSD sınıfına yakın görünüyor." : "Sıralı hızlar SSD düzeyinde, küçük blok erişimlerinde dalgalanma görülebilir."
                : "Sonuçlar HDD veya USB kutusu sınıfına yakın görünüyor.";
    }

    private static DiskBenchmarkResult BuildFailureResult(DiskSummary disk, DiskBenchmarkProfileOption profile, string message) =>
        new(
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

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false); } catch { }
    }

    private sealed record BenchmarkPhase(string Label, string Summary, string Arguments);
}

using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Backup;

[SupportedOSPlatform("windows")]
internal sealed class ImageContainerService
{
    private readonly SmartCloneFileSystemExecutor _smartExecutor = new();

    public ImageContainerFormat DetectFormat(string path)
    {
        var extension = Path.GetExtension(path)?.ToLowerInvariant();
        return extension switch
        {
            ".ctimg" => ImageContainerFormat.CigerPackage,
            ".img" => ImageContainerFormat.RawImage,
            _ => throw new IOException("Desteklenmeyen imaj biçimi. Şu an yalnızca .img ve .ctimg desteklenir.")
        };
    }

    public ImageCaptureMode DetectCaptureMode(string imagePath)
    {
        return DetectFormat(imagePath) switch
        {
            ImageContainerFormat.RawImage => ImageCaptureMode.Raw,
            ImageContainerFormat.CigerPackage => ReadPackageDescriptor(imagePath).CaptureMode,
            _ => ImageCaptureMode.Raw
        };
    }

    public string GetFormatLabel(ImageContainerFormat format, ImageCaptureMode captureMode = ImageCaptureMode.Raw)
    {
        return (format, captureMode) switch
        {
            (ImageContainerFormat.RawImage, _) => "Ham imaj (.img)",
            (ImageContainerFormat.CigerPackage, ImageCaptureMode.Smart) => "Akıllı CigerTool paketi (.ctimg)",
            (ImageContainerFormat.CigerPackage, _) => "CigerTool paketi (.ctimg)",
            _ => format.ToString()
        };
    }

    public long GetPayloadLength(string imagePath)
    {
        return DetectFormat(imagePath) switch
        {
            ImageContainerFormat.RawImage => new FileInfo(imagePath).Length,
            ImageContainerFormat.CigerPackage => ReadPackageDescriptor(imagePath).PayloadBytes,
            _ => throw new IOException("İmaj boyutu belirlenemedi.")
        };
    }

    public async Task<ImageExecutionOutcome> CaptureVolumeAsync(
        DiskSummary source,
        string destinationPath,
        ImageContainerFormat format,
        ImageCaptureMode captureMode,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        EnsureDestinationDirectory(destinationPath);

        if (format == ImageContainerFormat.RawImage)
        {
            if (captureMode != ImageCaptureMode.Raw)
            {
                throw new IOException("Akıllı imaj yalnızca CigerTool paketi (.ctimg) biçiminde kaydedilebilir.");
            }

            var processedBytes = await CaptureRawImageAsync(source, destinationPath, progress, cancellationToken);
            return new ImageExecutionOutcome(processedBytes, processedBytes, GetFormatLabel(format, captureMode), []);
        }

        return captureMode switch
        {
            ImageCaptureMode.Smart => await CaptureSmartPackageAsync(source, destinationPath, progress, cancellationToken),
            _ => await CaptureRawPackageAsync(source, destinationPath, progress, cancellationToken)
        };
    }

    public async Task<ImageExecutionOutcome> RestoreImageAsync(
        string imagePath,
        DiskSummary target,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var format = DetectFormat(imagePath);
        if (format == ImageContainerFormat.RawImage)
        {
            return await RestoreRawImageAsync(imagePath, target, progress, cancellationToken);
        }

        var descriptor = ReadPackageDescriptor(imagePath);
        return descriptor.CaptureMode switch
        {
            ImageCaptureMode.Smart => await RestoreSmartPackageAsync(imagePath, target, descriptor, progress, cancellationToken),
            _ => await RestoreRawPackageAsync(imagePath, target, progress, cancellationToken)
        };
    }

    public async Task<ImageExecutionOutcome> ConvertAsync(
        string sourcePath,
        string destinationPath,
        ImageContainerFormat destinationFormat,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var sourceFormat = DetectFormat(sourcePath);
        if (sourceFormat == destinationFormat)
        {
            throw new IOException("Kaynak ve hedef imaj biçimi aynı olamaz.");
        }

        EnsureDestinationDirectory(destinationPath);

        if (sourceFormat == ImageContainerFormat.CigerPackage && DetectCaptureMode(sourcePath) == ImageCaptureMode.Smart)
        {
            throw new IOException("Akıllı CigerTool paketi şu an ham imaja dönüştürülemez.");
        }

        return destinationFormat switch
        {
            ImageContainerFormat.RawImage => await ConvertToRawImageAsync(sourcePath, sourceFormat, destinationPath, progress, cancellationToken),
            ImageContainerFormat.CigerPackage => await ConvertToPackageAsync(sourcePath, sourceFormat, destinationPath, progress, cancellationToken),
            _ => throw new IOException("Desteklenmeyen hedef biçim.")
        };
    }

    private static async Task<long> CaptureRawImageAsync(
        DiskSummary source,
        string destinationPath,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        using var sourceScope = RawVolumeAccessScope.OpenRead(source.DriveLetter);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        sourceScope.Stream.Position = 0;

        return await StreamCopyExecutor.CopyAsync(
            sourceScope.Stream,
            destination,
            source.TotalBytes,
            "İmaj alma",
            "Sürücü içeriği imaj dosyasına yazılıyor.",
            progress,
            cancellationToken,
            Path.GetFileName(destinationPath));
    }

    private async Task<ImageExecutionOutcome> CaptureRawPackageAsync(
        DiskSummary source,
        string destinationPath,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        using var sourceScope = RawVolumeAccessScope.OpenRead(source.DriveLetter);
        await using var packageStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false);

        await WriteMetadataAsync(
            archive,
            new PackageMetadataModel
            {
                Version = 2,
                CreatedAt = DateTimeOffset.Now,
                SourceName = source.Name,
                SourceDriveLetter = source.DriveLetter,
                FileSystem = source.FileSystem,
                SizeBytes = source.TotalBytes,
                SourceTotalBytes = source.TotalBytes,
                CaptureMode = nameof(ImageCaptureMode.Raw),
                FileCount = 0
            },
            cancellationToken);

        var payloadEntry = archive.CreateEntry("payload.img", CompressionLevel.NoCompression);
        await using var payloadStream = payloadEntry.Open();
        sourceScope.Stream.Position = 0;

        var processedBytes = await StreamCopyExecutor.CopyAsync(
            sourceScope.Stream,
            payloadStream,
            source.TotalBytes,
            "İmaj alma",
            "Sürücü içeriği CigerTool paketine yazılıyor.",
            progress,
            cancellationToken,
            Path.GetFileName(destinationPath));

        return new ImageExecutionOutcome(processedBytes, processedBytes, GetFormatLabel(ImageContainerFormat.CigerPackage), []);
    }

    private async Task<ImageExecutionOutcome> CaptureSmartPackageAsync(
        DiskSummary source,
        string destinationPath,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sourceRoot = EnsureDriveRoot(source.DriveLetter);
        var files = SmartCloneFileSystemExecutor.ScanFilesForPackaging(sourceRoot, warnings, cancellationToken);
        var totalBytes = files.Sum(file => file.Length);

        await using var packageStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false);

        await WriteMetadataAsync(
            archive,
            new PackageMetadataModel
            {
                Version = 2,
                CreatedAt = DateTimeOffset.Now,
                SourceName = source.Name,
                SourceDriveLetter = source.DriveLetter,
                FileSystem = source.FileSystem,
                SizeBytes = totalBytes,
                SourceTotalBytes = source.TotalBytes,
                CaptureMode = nameof(ImageCaptureMode.Smart),
                FileCount = files.Count
            },
            cancellationToken);

        var processedBytes = await _smartExecutor.WriteFileSetToZipAsync(
            sourceRoot,
            archive,
            files,
            totalBytes,
            progress,
            cancellationToken);

        return new ImageExecutionOutcome(
            processedBytes,
            totalBytes,
            GetFormatLabel(ImageContainerFormat.CigerPackage, ImageCaptureMode.Smart),
            warnings);
    }

    private static async Task<ImageExecutionOutcome> RestoreRawImageAsync(
        string imagePath,
        DiskSummary target,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        await using var imageStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        using var targetScope = RawVolumeAccessScope.OpenWrite(target.DriveLetter);
        targetScope.Stream.Position = 0;

        var processedBytes = await StreamCopyExecutor.CopyAsync(
            imageStream,
            targetScope.Stream,
            imageStream.Length,
            "Geri yükleme",
            "İmaj içeriği hedef sürücüye yazılıyor.",
            progress,
            cancellationToken,
            Path.GetFileName(imagePath));

        return new ImageExecutionOutcome(processedBytes, imageStream.Length, "Ham imaj (.img)", []);
    }

    private static async Task<ImageExecutionOutcome> RestoreRawPackageAsync(
        string imagePath,
        DiskSummary target,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var payload = archive.GetEntry("payload.img") ?? throw new IOException("CigerTool paketi içinde payload.img bulunamadı.");
        await using var payloadStream = payload.Open();
        using var targetScope = RawVolumeAccessScope.OpenWrite(target.DriveLetter);
        targetScope.Stream.Position = 0;

        var processedBytes = await StreamCopyExecutor.CopyAsync(
            payloadStream,
            targetScope.Stream,
            payload.Length,
            "Geri yükleme",
            "Paket içeriği hedef sürücüye yazılıyor.",
            progress,
            cancellationToken,
            Path.GetFileName(imagePath));

        return new ImageExecutionOutcome(processedBytes, payload.Length, "CigerTool paketi (.ctimg)", []);
    }

    private async Task<ImageExecutionOutcome> RestoreSmartPackageAsync(
        string imagePath,
        DiskSummary target,
        PackageDescriptor descriptor,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var result = await _smartExecutor.RestoreFileSetFromZipAsync(
            archive,
            EnsureDriveRoot(target.DriveLetter),
            progress,
            cancellationToken);

        return new ImageExecutionOutcome(
            result.ProcessedBytes,
            descriptor.PayloadBytes,
            "Akıllı CigerTool paketi (.ctimg)",
            result.Warnings);
    }

    private async Task<ImageExecutionOutcome> ConvertToRawImageAsync(
        string sourcePath,
        ImageContainerFormat sourceFormat,
        string destinationPath,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        switch (sourceFormat)
        {
            case ImageContainerFormat.CigerPackage:
            {
                using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var descriptor = ReadPackageDescriptor(archive, sourcePath);
                if (descriptor.CaptureMode == ImageCaptureMode.Smart)
                {
                    throw new IOException("Akıllı CigerTool paketi şu an ham imaja dönüştürülemez.");
                }

                var payload = archive.GetEntry("payload.img") ?? throw new IOException("CigerTool paketi içinde payload.img bulunamadı.");
                await using var payloadStream = payload.Open();
                await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

                var processedBytes = await StreamCopyExecutor.CopyAsync(
                    payloadStream,
                    destination,
                    payload.Length,
                    "Dönüştürme",
                    "Paket içeriği ham imaj dosyasına aktarılıyor.",
                    progress,
                    cancellationToken,
                    Path.GetFileName(sourcePath));

                return new ImageExecutionOutcome(processedBytes, payload.Length, "Ham imaj (.img)", []);
            }

            default:
                throw new IOException("Bu kaynak biçiminden ham imaja dönüşüm desteklenmiyor.");
        }
    }

    private async Task<ImageExecutionOutcome> ConvertToPackageAsync(
        string sourcePath,
        ImageContainerFormat sourceFormat,
        string destinationPath,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false);

        var sourceLength = sourceFormat switch
        {
            ImageContainerFormat.RawImage => new FileInfo(sourcePath).Length,
            _ => throw new IOException("Bu kaynak biçiminden CigerTool paketine dönüşüm desteklenmiyor.")
        };

        await WriteMetadataAsync(
            archive,
            new PackageMetadataModel
            {
                Version = 2,
                CreatedAt = DateTimeOffset.Now,
                SourceName = Path.GetFileName(sourcePath),
                SourceDriveLetter = null,
                FileSystem = "Ham içerik",
                SizeBytes = sourceLength,
                SourceTotalBytes = sourceLength,
                CaptureMode = nameof(ImageCaptureMode.Raw),
                FileCount = 0
            },
            cancellationToken);

        var payloadEntry = archive.CreateEntry("payload.img", CompressionLevel.NoCompression);
        await using var payloadStream = payloadEntry.Open();
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

        var processedBytes = await StreamCopyExecutor.CopyAsync(
            sourceStream,
            payloadStream,
            sourceLength,
            "Dönüştürme",
            "Ham imaj CigerTool paketine dönüştürülüyor.",
            progress,
            cancellationToken,
            Path.GetFileName(sourcePath));

        return new ImageExecutionOutcome(processedBytes, sourceLength, "CigerTool paketi (.ctimg)", []);
    }

    private static PackageDescriptor ReadPackageDescriptor(string imagePath)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return ReadPackageDescriptor(archive, imagePath);
    }

    private static PackageDescriptor ReadPackageDescriptor(ZipArchive archive, string imagePath)
    {
        var metadataEntry = archive.GetEntry("metadata.json");
        PackageMetadataModel metadata;

        if (metadataEntry is null)
        {
            var fallbackPayload = archive.GetEntry("payload.img") ?? throw new IOException("CigerTool paketi okunamadı.");
            metadata = new PackageMetadataModel
            {
                Version = 1,
                CreatedAt = DateTimeOffset.MinValue,
                SourceName = Path.GetFileName(imagePath),
                FileSystem = "Ham içerik",
                SizeBytes = fallbackPayload.Length,
                SourceTotalBytes = fallbackPayload.Length,
                CaptureMode = nameof(ImageCaptureMode.Raw)
            };
        }
        else
        {
            using var metadataStream = metadataEntry.Open();
            metadata = JsonSerializer.Deserialize<PackageMetadataModel>(metadataStream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new PackageMetadataModel();
        }

        var captureMode = ParseCaptureMode(metadata.CaptureMode);
        var payloadBytes = captureMode switch
        {
            ImageCaptureMode.Smart => metadata.SizeBytes > 0
                ? metadata.SizeBytes
                : archive.Entries
                    .Where(entry => entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name))
                    .Sum(entry => entry.Length),
            _ => archive.GetEntry("payload.img")?.Length ?? metadata.SizeBytes
        };

        if (payloadBytes <= 0)
        {
            throw new IOException("CigerTool paketi içeriği okunamadı.");
        }

        return new PackageDescriptor(captureMode, payloadBytes, metadata);
    }

    private static ImageCaptureMode ParseCaptureMode(string? value)
    {
        return string.Equals(value, nameof(ImageCaptureMode.Smart), StringComparison.OrdinalIgnoreCase)
            ? ImageCaptureMode.Smart
            : ImageCaptureMode.Raw;
    }

    private static async Task WriteMetadataAsync(ZipArchive archive, PackageMetadataModel metadata, CancellationToken cancellationToken)
    {
        var metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.SmallestSize);
        await using var metadataStream = metadataEntry.Open();
        await JsonSerializer.SerializeAsync(metadataStream, metadata, cancellationToken: cancellationToken);
    }

    private static void EnsureDestinationDirectory(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string EnsureDriveRoot(string driveLetter)
    {
        var trimmed = (driveLetter ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new IOException("Sürücü kökü çözümlenemedi.");
        }

        return trimmed.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || trimmed.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? trimmed
            : trimmed + Path.DirectorySeparatorChar;
    }

    private sealed class PackageMetadataModel
    {
        public int Version { get; init; } = 1;

        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.MinValue;

        public string SourceName { get; init; } = string.Empty;

        public string? SourceDriveLetter { get; init; }

        public string FileSystem { get; init; } = "Bilinmiyor";

        public long SizeBytes { get; init; }

        public long SourceTotalBytes { get; init; }

        public string CaptureMode { get; init; } = nameof(ImageCaptureMode.Raw);

        public int FileCount { get; init; }
    }

    private sealed record PackageDescriptor(
        ImageCaptureMode CaptureMode,
        long PayloadBytes,
        PackageMetadataModel Metadata);
}

internal sealed record ImageExecutionOutcome(
    long ProcessedBytes,
    long TotalBytes,
    string FormatLabel,
    IReadOnlyList<string> Warnings);

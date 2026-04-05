using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using CigerTool.Application.Models;
using CigerTool.Application.Contracts;
using CigerTool.Domain.Enums;
using CigerTool.Usb.Models;

namespace CigerTool.Usb.Services;

[SupportedOSPlatform("windows")]
internal sealed class StandardIsoUsbWriter(IOperationLogService operationLogService)
{
    private const long Fat32FileLimitBytes = 4L * 1024 * 1024 * 1024 - 1;
    private const int CopyBufferSize = 1024 * 1024;

    public async Task<UsbCreatorOperationResult> PrepareAndWriteAsync(
        string isoPath,
        UsbPhysicalDeviceInfo device,
        string bootProfileLabel,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? mountedRoot = null;

        try
        {
            ReportStep(progress, "ISO bağlanıyor", "Kaynak ISO Windows tarafından bağlanıyor.", 3, true, currentItem: Path.GetFileName(isoPath));
            mountedRoot = await MountIsoAsync(isoPath, cancellationToken);

            var layout = AnalyzeIsoLayout(mountedRoot, bootProfileLabel);
            var strategy = ChooseStrategy(layout);
            var diskNumber = ParseDiskNumber(device.PhysicalPath);
            if (diskNumber < 0)
            {
                return new UsbCreatorOperationResult(false, OperationSeverity.Error, "USB aygıt numarası çözümlenemedi.");
            }

            operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "Standart ISO hazırlama akışı başlatıldı.",
                "usb.iso.prepare.start",
                new Dictionary<string, string>
                {
                    ["image"] = isoPath,
                    ["device"] = device.PhysicalPath,
                    ["scenario"] = layout.ScenarioLabel,
                    ["filesystem"] = strategy.FileSystem,
                    ["splitWim"] = strategy.SplitInstallWim.ToString()
                });

            ReportStep(progress, "USB hazırlanıyor", $"USB aygıtı {strategy.FileSystem} dosya sistemi ile hazırlanıyor.", 10, true);
            var targetRoot = await PrepareUsbTargetAsync(diskNumber, strategy.FileSystem, cancellationToken);

            ReportStep(progress, "Kaynak analizi", "ISO içeriği hazırlanıyor.", 18, true);
            await CopyIsoContentsAsync(layout, targetRoot, strategy, progress, cancellationToken);

            if (strategy.SplitInstallWim && layout.InstallWimPath is not null)
            {
                ReportStep(progress, "Windows imajı bölünüyor", "Büyük install.wim dosyası USB uyumluluğu için parçalara ayrılıyor.", 88, true);
                var targetSwm = Path.Combine(targetRoot, "sources", "install.swm");
                await SplitInstallWimAsync(layout.InstallWimPath, targetSwm, cancellationToken);
            }

            if (strategy.ApplyWindowsBootCode)
            {
                ReportStep(progress, "Önyükleme alanı hazırlanıyor", "USB için Windows önyükleme kodu uygulanıyor.", 94, true);
                await ApplyBootCodeAsync(targetRoot, cancellationToken);
            }

            var validation = ValidatePreparedUsb(layout, targetRoot, strategy);
            if (!validation.IsValid)
            {
                operationLogService.Record(
                    OperationSeverity.Error,
                    "USB Oluşturma",
                    "ISO hazırlama sonrası doğrulama başarısız oldu.",
                    "usb.iso.prepare.validation.failure",
                    new Dictionary<string, string>
                    {
                        ["device"] = device.PhysicalPath,
                        ["reason"] = validation.Message
                    });

                return new UsbCreatorOperationResult(false, OperationSeverity.Error, validation.Message);
            }

            ReportStep(progress, "Doğrulama tamamlandı", validation.Message, 100, false);

            operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "Standart ISO hazırlama akışı başarıyla tamamlandı.",
                "usb.iso.prepare.complete",
                new Dictionary<string, string>
                {
                    ["device"] = device.PhysicalPath,
                    ["targetRoot"] = targetRoot,
                    ["filesystem"] = strategy.FileSystem,
                    ["scenario"] = layout.ScenarioLabel
                });

            var finalMessage = strategy.CompatibilityNote is null
                ? "ISO hazırlanıp USB'ye başarıyla yazıldı."
                : $"ISO hazırlanıp USB'ye yazıldı. {strategy.CompatibilityNote}";

            return new UsbCreatorOperationResult(true, OperationSeverity.Info, finalMessage);
        }
        catch (OperationCanceledException)
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "ISO hazırlama işlemi iptal edildi.");
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Error,
                "USB Oluşturma",
                $"Standart ISO hazırlama akışı başarısız oldu: {ex.Message}",
                "usb.iso.prepare.failure",
                new Dictionary<string, string>
                {
                    ["image"] = isoPath,
                    ["device"] = device.PhysicalPath,
                    ["error"] = ex.Message
                });

            return new UsbCreatorOperationResult(false, OperationSeverity.Error, $"ISO hazırlanamadı: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(mountedRoot))
            {
                try
                {
                    await DismountIsoAsync(isoPath, CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static IsoWriteStrategy ChooseStrategy(IsoLayout layout)
    {
        var windowsLikeScenario =
            layout.ScenarioLabel.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            layout.ScenarioLabel.Contains("WinPE", StringComparison.OrdinalIgnoreCase) ||
            layout.ScenarioLabel.Contains("CigerTool OS", StringComparison.OrdinalIgnoreCase);

        if (layout.InstallWimPath is not null && layout.InstallWimSizeBytes > Fat32FileLimitBytes)
        {
            return new IsoWriteStrategy(
                FileSystem: "FAT32",
                SplitInstallWim: true,
                ApplyWindowsBootCode: windowsLikeScenario,
                CompatibilityNote: null);
        }

        if (layout.MaxFileSizeBytes > Fat32FileLimitBytes)
        {
            return new IsoWriteStrategy(
                FileSystem: "NTFS",
                SplitInstallWim: false,
                ApplyWindowsBootCode: windowsLikeScenario,
                CompatibilityNote: "Bu kaynak büyük dosyalar içerdiği için USB NTFS olarak hazırlandı. Bazı UEFI sistemlerde uyumluluk cihaz yazılımına göre değişebilir.");
        }

        return new IsoWriteStrategy(
            FileSystem: "FAT32",
            SplitInstallWim: false,
            ApplyWindowsBootCode: windowsLikeScenario,
            CompatibilityNote: null);
    }

    private static IsoLayout AnalyzeIsoLayout(string mountedRoot, string bootProfileLabel)
    {
        var files = Directory.EnumerateFiles(mountedRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();

        var installWim = files.FirstOrDefault(file =>
            string.Equals(file.DirectoryName, Path.Combine(mountedRoot.TrimEnd(Path.DirectorySeparatorChar), "sources"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.Name, "install.wim", StringComparison.OrdinalIgnoreCase));

        return new IsoLayout(
            RootPath: mountedRoot,
            ScenarioLabel: bootProfileLabel,
            Files: files,
            TotalBytes: files.Sum(file => file.Length),
            MaxFileSizeBytes: files.Length == 0 ? 0 : files.Max(file => file.Length),
            HasEfiBoot: Directory.Exists(Path.Combine(mountedRoot, "EFI")),
            HasBootMgr: File.Exists(Path.Combine(mountedRoot, "bootmgr")) || File.Exists(Path.Combine(mountedRoot, "BOOTMGR")),
            InstallWimPath: installWim?.FullName,
            InstallWimSizeBytes: installWim?.Length ?? 0);
    }

    private async Task CopyIsoContentsAsync(
        IsoLayout layout,
        string targetRoot,
        IsoWriteStrategy strategy,
        IProgress<OperationProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var filesToCopy = layout.Files
            .Where(file => !(strategy.SplitInstallWim && file.FullName.Equals(layout.InstallWimPath, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var totalBytes = filesToCopy.Sum(file => file.Length);
        long copiedBytes = 0;

        foreach (var sourceFile in filesToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(layout.RootPath, sourceFile.FullName);
            var targetFile = Path.Combine(targetRoot, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await CopyFileAsync(
                sourceFile.FullName,
                targetFile,
                processed =>
                {
                    var absoluteBytes = copiedBytes + processed;
                    var percent = totalBytes <= 0 ? 20 : 20 + Math.Clamp((absoluteBytes * 60d) / totalBytes, 0d, 60d);
                    progress?.Report(new OperationProgressSnapshot(
                        "Dosyalar kopyalanıyor",
                        "ISO içeriği USB aygıtına kopyalanıyor.",
                        percent,
                        false,
                        absoluteBytes,
                        totalBytes,
                        FormatBytes(absoluteBytes),
                        FormatBytes(totalBytes),
                        "Kopyalanıyor",
                        "Hesaplanıyor",
                        relativePath));
                },
                cancellationToken);

            copiedBytes += sourceFile.Length;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string targetPath,
        Action<long> progressCallback,
        CancellationToken cancellationToken)
    {
        const int bufferSize = CopyBufferSize;
        var copied = 0L;

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        var buffer = new byte[bufferSize];

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progressCallback(copied);
        }

        await target.FlushAsync(cancellationToken);
        File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));
        File.SetAttributes(targetPath, File.GetAttributes(sourcePath));
    }

    private async Task<string> MountIsoAsync(string isoPath, CancellationToken cancellationToken)
    {
        var escapedIsoPath = EscapePowerShellLiteral(isoPath);
        var script = $@"
$ErrorActionPreference = 'Stop'
Mount-DiskImage -ImagePath '{escapedIsoPath}' | Out-Null
Start-Sleep -Milliseconds 800
$volume = Get-DiskImage -ImagePath '{escapedIsoPath}' | Get-Volume | Where-Object {{ $_.DriveLetter }} | Select-Object -First 1
if ($null -eq $volume) {{ throw 'ISO bağlandı ancak sürücü harfi alınamadı.' }}
""$($volume.DriveLetter):\""
";

        var result = await RunPowerShellAsync(script, cancellationToken);
        return result.Trim();
    }

    private async Task DismountIsoAsync(string isoPath, CancellationToken cancellationToken)
    {
        var escapedIsoPath = EscapePowerShellLiteral(isoPath);
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Dismount-DiskImage -ImagePath '{escapedIsoPath}' | Out-Null
";

        await RunPowerShellAsync(script, cancellationToken);
    }

    private async Task<string> PrepareUsbTargetAsync(int diskNumber, string fileSystem, CancellationToken cancellationToken)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
$diskNumber = {diskNumber}
try {{ Set-Disk -Number $diskNumber -IsReadOnly $false -ErrorAction SilentlyContinue | Out-Null }} catch {{}}
Clear-Disk -Number $diskNumber -RemoveData -RemoveOEM -Confirm:$false | Out-Null
Initialize-Disk -Number $diskNumber -PartitionStyle MBR | Out-Null
$partition = New-Partition -DiskNumber $diskNumber -UseMaximumSize -AssignDriveLetter -IsActive
Format-Volume -DriveLetter $partition.DriveLetter -FileSystem {fileSystem} -NewFileSystemLabel 'CIGERTOOL' -Confirm:$false -Force | Out-Null
Start-Sleep -Milliseconds 500
""$($partition.DriveLetter):\""
";

        var result = await RunPowerShellAsync(script, cancellationToken);
        return result.Trim();
    }

    private async Task SplitInstallWimAsync(string sourceWimPath, string targetSwmPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetSwmPath)!);
        var args = $"/Split-Image /ImageFile:\"{sourceWimPath}\" /SWMFile:\"{targetSwmPath}\" /FileSize:3800";
        await RunProcessAsync("dism.exe", args, cancellationToken);
    }

    private async Task ApplyBootCodeAsync(string targetRoot, CancellationToken cancellationToken)
    {
        var driveLetter = targetRoot[..2];
        await RunProcessAsync("bootsect.exe", $"/nt60 {driveLetter} /mbr /force", cancellationToken);
    }

    private static ValidationResult ValidatePreparedUsb(IsoLayout layout, string targetRoot, IsoWriteStrategy strategy)
    {
        if (layout.HasEfiBoot && !Directory.Exists(Path.Combine(targetRoot, "EFI")))
        {
            return new ValidationResult(false, "Hazırlanan USB üzerinde EFI önyükleme klasörü bulunamadı.");
        }

        if (layout.HasBootMgr && !File.Exists(Path.Combine(targetRoot, "bootmgr")))
        {
            return new ValidationResult(false, "Hazırlanan USB üzerinde bootmgr dosyası bulunamadı.");
        }

        if (strategy.SplitInstallWim && !File.Exists(Path.Combine(targetRoot, "sources", "install.swm")))
        {
            return new ValidationResult(false, "install.wim bölünmesi tamamlanamadı.");
        }

        return new ValidationResult(true, "USB hazırlama ve temel bütünlük denetimi tamamlandı.");
    }

    private static int ParseDiskNumber(string physicalPath)
    {
        var match = Regex.Match(physicalPath ?? string.Empty, @"PhysicalDrive(?<number>\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var value)
            ? value
            : -1;
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            cancellationToken);
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                outputBuilder.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                errorBuilder.AppendLine(args.Data);
            }
        };
        process.Exited += (_, _) => exitTcs.TrySetResult(process.ExitCode);

        if (!process.Start())
        {
            throw new InvalidOperationException($"{fileName} başlatılamadı.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        var exitCode = await exitTcs.Task;
        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (exitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(errorBuilder.ToString())
                ? outputBuilder.ToString()
                : errorBuilder.ToString();
            throw new InvalidOperationException(error.Trim());
        }

        return outputBuilder.ToString();
    }

    private static void ReportStep(
        IProgress<OperationProgressSnapshot>? progress,
        string phaseLabel,
        string summary,
        double percent,
        bool indeterminate,
        string? currentItem = null)
    {
        progress?.Report(new OperationProgressSnapshot(
            phaseLabel,
            summary,
            percent,
            indeterminate,
            0,
            0,
            "0 B",
            "Bilinmiyor",
            indeterminate ? "Hazırlanıyor" : "-",
            indeterminate ? "Hesaplanıyor" : "-",
            currentItem));
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    private sealed record IsoLayout(
        string RootPath,
        string ScenarioLabel,
        FileInfo[] Files,
        long TotalBytes,
        long MaxFileSizeBytes,
        bool HasEfiBoot,
        bool HasBootMgr,
        string? InstallWimPath,
        long InstallWimSizeBytes);

    private sealed record IsoWriteStrategy(
        string FileSystem,
        bool SplitInstallWim,
        bool ApplyWindowsBootCode,
        string? CompatibilityNote);

    private sealed record ValidationResult(
        bool IsValid,
        string Message);
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;

namespace CigerTool.Usb.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsDeploymentService(IOperationLogService operationLogService) : IWindowsDeploymentService
{
    public async Task<IReadOnlyList<WindowsImageEditionOption>> GetAvailableEditionsAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var imagePath = await ResolveInstallImagePathAsync(sourcePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return [];
        }

        var output = await RunProcessAsync("dism.exe", $"/English /Get-WimInfo /WimFile:\"{imagePath}\"", cancellationToken);
        return ParseEditions(output);
    }

    public async Task<UsbCreatorOperationResult> DeployToDiskAsync(
        string sourcePath,
        int targetDiskNumber,
        int imageIndex,
        bool portableMode,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? mountedRoot = null;

        try
        {
            var imagePath = sourcePath;
            if (string.Equals(Path.GetExtension(sourcePath), ".iso", StringComparison.OrdinalIgnoreCase))
            {
                ReportStep(progress, "ISO bağlanıyor", "Windows kurulum ISO'su bağlanıyor.", 5, true, Path.GetFileName(sourcePath));
                mountedRoot = await MountIsoAsync(sourcePath, cancellationToken);
                imagePath = FindWindowsImagePath(mountedRoot) ?? throw new InvalidOperationException("ISO içinde install.wim veya install.esd bulunamadı.");
            }

            operationLogService.Record(
                OperationSeverity.Info,
                "Kurulum Medyası",
                "Doğrudan Windows kurulum akışı başlatıldı.",
                "install.direct.start",
                new Dictionary<string, string>
                {
                    ["source"] = sourcePath,
                    ["image"] = imagePath,
                    ["diskNumber"] = targetDiskNumber.ToString(),
                    ["imageIndex"] = imageIndex.ToString(),
                    ["portableMode"] = portableMode.ToString()
                });

            ReportStep(progress, "Hedef disk hazırlanıyor", "Seçilen disk bölümlendiriliyor ve kurulum için hazırlanıyor.", 15, true);
            await PrepareTargetDiskAsync(targetDiskNumber, cancellationToken);

            ReportStep(progress, "Windows imajı uygulanıyor", "Seçilen Windows sürümü hedef diske açılıyor.", 35, true);
            await RunProcessAsync("dism.exe", $"/Apply-Image /ImageFile:\"{imagePath}\" /Index:{imageIndex} /ApplyDir:W:\\", cancellationToken);

            ReportStep(progress, "Önyükleme dosyaları kuruluyor", "EFI ve önyükleme dosyaları hazırlanıyor.", 82, true);
            await RunProcessAsync("bcdboot.exe", @"W:\Windows /s S: /f ALL", cancellationToken);

            if (portableMode)
            {
                ReportStep(progress, "Taşınabilir ayarlar uygulanıyor", "İlk açılış için temel taşınabilir kurulum ayarları hazırlanıyor.", 92, true);
                await WritePortableMarkerAsync(cancellationToken);
            }

            ReportStep(progress, "Temizlik", "Geçici atamalar kaldırılıyor.", 97, true);
            await ClearDriveLetterAssignmentsAsync(cancellationToken);

            progress?.Report(new OperationProgressSnapshot(
                "Tamamlandı",
                portableMode
                    ? "Windows taşınabilir kurulum akışı tamamlandı."
                    : "Windows doğrudan disk kurulumu tamamlandı.",
                100,
                false,
                1,
                1,
                "Tamamlandı",
                "Tamamlandı",
                "-",
                "-",
                null));

            return new UsbCreatorOperationResult(
                true,
                OperationSeverity.Info,
                portableMode
                    ? "Kaynak Windows imajı hedef diske taşınabilir düzenle uygulandı."
                    : "Kaynak Windows imajı hedef diske başarıyla kuruldu.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operationLogService.Record(
                OperationSeverity.Error,
                "Kurulum Medyası",
                $"Doğrudan kurulum başarısız oldu: {ex.Message}",
                "install.direct.failure",
                new Dictionary<string, string> { ["error"] = ex.Message });

            return new UsbCreatorOperationResult(false, OperationSeverity.Error, $"Doğrudan kurulum tamamlanamadı: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(mountedRoot))
            {
                try
                {
                    await DismountIsoAsync(sourcePath, CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static IReadOnlyList<WindowsImageEditionOption> ParseEditions(string output)
    {
        var result = new List<WindowsImageEditionOption>();
        var matches = Regex.Matches(output, @"Index : (?<index>\d+)\s+Name : (?<name>.+?)(?:\s+Description : (?<description>.+?))?(?=\s+Index : |\s*$)", RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["index"].Value, out var index))
            {
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            var description = match.Groups["description"].Success ? match.Groups["description"].Value.Trim() : name;
            result.Add(new WindowsImageEditionOption(index, name, description));
        }

        return result;
    }

    private static async Task<string?> ResolveInstallImagePathAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".wim", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(sourcePath), ".esd", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        if (!string.Equals(Path.GetExtension(sourcePath), ".iso", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mountedRoot = await MountIsoAsync(sourcePath, cancellationToken);
        try
        {
            return FindWindowsImagePath(mountedRoot);
        }
        finally
        {
            await DismountIsoAsync(sourcePath, CancellationToken.None);
        }
    }

    private static string? FindWindowsImagePath(string mountedRoot)
    {
        var sourcesRoot = Path.Combine(mountedRoot.TrimEnd(Path.DirectorySeparatorChar), "sources");
        var installWim = Path.Combine(sourcesRoot, "install.wim");
        if (File.Exists(installWim))
        {
            return installWim;
        }

        var installEsd = Path.Combine(sourcesRoot, "install.esd");
        return File.Exists(installEsd) ? installEsd : null;
    }

    private static async Task PrepareTargetDiskAsync(int diskNumber, CancellationToken cancellationToken)
    {
        var script = $@"
select disk {diskNumber}
clean
convert gpt
create partition efi size=100
format quick fs=fat32 label=""SYSTEM""
assign letter=S
create partition msr size=16
create partition primary
format quick fs=ntfs label=""Windows""
assign letter=W
exit
";

        await RunDiskPartAsync(script, cancellationToken);
    }

    private static async Task WritePortableMarkerAsync(CancellationToken cancellationToken)
    {
        const string script = @"
$path = 'W:\CigerToolPortable.flag'
Set-Content -Path $path -Value 'portable' -Encoding ASCII
";
        await RunPowerShellAsync(script, cancellationToken);
    }

    private static async Task ClearDriveLetterAssignmentsAsync(CancellationToken cancellationToken)
    {
        const string script = @"
select volume S
remove letter=S noerr
select volume W
remove letter=W noerr
exit
";
        await RunDiskPartAsync(script, cancellationToken);
    }

    private static async Task<string> MountIsoAsync(string isoPath, CancellationToken cancellationToken)
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

        return (await RunPowerShellAsync(script, cancellationToken)).Trim();
    }

    private static async Task DismountIsoAsync(string isoPath, CancellationToken cancellationToken)
    {
        var escapedIsoPath = EscapePowerShellLiteral(isoPath);
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Dismount-DiskImage -ImagePath '{escapedIsoPath}' | Out-Null
";
        await RunPowerShellAsync(script, cancellationToken);
    }

    private static async Task RunDiskPartAsync(string scriptContent, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"cigertool-diskpart-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempPath, scriptContent, cancellationToken);

        try
        {
            await RunProcessAsync("diskpart.exe", $"/s \"{tempPath}\"", cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
        }
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
}

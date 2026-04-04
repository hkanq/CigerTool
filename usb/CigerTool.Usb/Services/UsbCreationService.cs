using System.Net.Http;
using System.Security.Cryptography;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Usb.Contracts;
using CigerTool.Usb.Models;

namespace CigerTool.Usb.Services;

public sealed class UsbCreationService : IUsbCreationService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromHours(2)
    };

    private readonly ISettingsService _settingsService;
    private readonly IReleaseSourceResolver _releaseSourceResolver;
    private readonly IOperationLogService _operationLogService;
    private readonly IAppPathService _appPathService;
    private readonly UsbDeviceDiscoveryService _deviceDiscoveryService;
    private readonly RawDiskWriter _rawDiskWriter;
    private readonly object _sync = new();

    private ReleaseManifestSummary _releaseSummary;
    private IReadOnlyList<UsbPhysicalDeviceInfo> _devices;
    private string? _manualImagePath;
    private string? _preparedImagePath;
    private string? _expectedSha256;
    private string? _calculatedSha256;
    private ChecksumVerificationState _checksumState;
    private string _checksumStatus;

    public UsbCreationService(
        ISettingsService settingsService,
        IReleaseSourceResolver releaseSourceResolver,
        IOperationLogService operationLogService,
        IAppPathService appPathService)
    {
        _settingsService = settingsService;
        _releaseSourceResolver = releaseSourceResolver;
        _operationLogService = operationLogService;
        _appPathService = appPathService;
        _deviceDiscoveryService = new UsbDeviceDiscoveryService(operationLogService);
        _rawDiskWriter = new RawDiskWriter();

        var settings = settingsService.GetSettings();
        _releaseSummary = BuildInitialSummary(settings);
        _devices = TryGetInitialDevices();
        _checksumState = ChecksumVerificationState.NotStarted;
        _checksumStatus = "Bütünlük doğrulaması henüz yapılmadı.";
    }

    public UsbCreatorWorkspaceSnapshot GetSnapshot()
    {
        ReleaseManifestSummary releaseSummary;
        IReadOnlyList<UsbPhysicalDeviceInfo> devices;
        string? preparedImagePath;
        string? expectedSha256;
        string? calculatedSha256;
        ChecksumVerificationState checksumState;
        string checksumStatus;

        lock (_sync)
        {
            releaseSummary = _releaseSummary;
            devices = _devices;
            preparedImagePath = _preparedImagePath;
            expectedSha256 = _expectedSha256;
            calculatedSha256 = _calculatedSha256;
            checksumState = _checksumState;
            checksumStatus = _checksumStatus;
        }

        var settings = _settingsService.GetSettings();
        var imageSizeBytes = ResolveImageSizeBytes(releaseSummary, preparedImagePath);
        var isAdministrator = _rawDiskWriter.IsAdministrator();
        var deviceEntries = devices.Select(device => ToDeviceEntry(device, imageSizeBytes)).ToArray();
        var eligibleCount = deviceEntries.Count(device => device.CanWrite);
        var hasPreparedImage = !string.IsNullOrWhiteSpace(preparedImagePath) && File.Exists(preparedImagePath);
        var canWriteFromCurrentState = isAdministrator &&
                                      hasPreparedImage &&
                                      eligibleCount > 0 &&
                                      checksumState is not (ChecksumVerificationState.Mismatch or ChecksumVerificationState.Failed);

        return new UsbCreatorWorkspaceSnapshot(
            Heading: "USB Ortamı Oluştur",
            Summary: "Hazır CigerTool OS imajını indirip doğrulayabilir, ardından uygun USB belleğe güvenli şekilde yazabilirsiniz.",
            ReleaseSourceConfiguration: new ReleaseSourceConfiguration(
                DefaultChannel: settings.DefaultChannel,
                DefaultManifestUrl: settings.DefaultManifestUrl,
                AllowManualImageSelection: true,
                AllowLocalOverride: true),
            ReleaseSourceStatus: releaseSummary.Status,
            Metrics:
            [
                new CardMetric("Kurulum kaynağı", releaseSummary.ModeLabel, releaseSummary.SourceDescription),
                new CardMetric("Sürüm", releaseSummary.Version, releaseSummary.ImageName),
                new CardMetric("Hazır imaj", hasPreparedImage ? "Hazır" : "Hazır değil", preparedImagePath ?? "Henüz indirilen veya seçilen bir imaj yok."),
                new CardMetric("Bütünlük", FormatChecksumState(checksumState), checksumStatus),
                new CardMetric("USB aygıtı", devices.Count.ToString(), $"{eligibleCount} aygıt yazma için uygun görünüyor."),
                new CardMetric("Yönetici yetkisi", isAdministrator ? "Açık" : "Kapalı", isAdministrator ? "Raw yazma izinleri kullanılabilir." : "USB yazmak için uygulamayı yönetici olarak açın.")
            ],
            Requirements:
            [
                "Bu bölüm işletim sistemi üretmez; yalnızca hazır CigerTool OS imajını indirir veya kullanır.",
                "Kaynak sırası: elle seçilen dosya, yerel geçersiz kılma, ardından çevrimiçi yayın bilgisi.",
                "Bütünlük doğrulaması başarısızsa yazma işlemi başlatılmaz.",
                "Sistem diski ve küçük kapasiteli aygıtlar güvenlik için engellenir."
            ],
            Release: new UsbReleaseInfo(
                ModeLabel: releaseSummary.ModeLabel,
                Status: releaseSummary.Status,
                SourceDescription: releaseSummary.SourceDescription,
                Channel: releaseSummary.Channel,
                Version: releaseSummary.Version,
                ImageName: releaseSummary.ImageName,
                ImageSizeLabel: imageSizeBytes > 0 ? FormatBytes(imageSizeBytes) : "Bilinmiyor",
                Notes: releaseSummary.Notes,
                ImageUrl: releaseSummary.ImageUrl,
                PreparedImagePath: preparedImagePath,
                ExpectedSha256: expectedSha256,
                CalculatedSha256: calculatedSha256,
                ChecksumState: checksumState,
                ChecksumStatus: checksumStatus),
            Devices: deviceEntries,
            IsAdministrator: isAdministrator,
            CanWriteFromCurrentState: canWriteFromCurrentState);
    }

    public async Task<UsbCreatorOperationResult> RefreshReleaseInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await _releaseSourceResolver.ResolveAsync(GetManualImagePath(), cancellationToken);
            var preparedImagePath = ResolvePreparedImagePath(summary);

            lock (_sync)
            {
                var previousExpectedSha = _expectedSha256;
                _releaseSummary = summary;
                _expectedSha256 = summary.Sha256;
                UpdatePreparedImagePath(preparedImagePath);

                if (!string.Equals(previousExpectedSha, summary.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    ResetChecksumState();
                }
            }

            return new UsbCreatorOperationResult(true, OperationSeverity.Info, summary.Status);
        }
        catch (Exception ex)
        {
            _operationLogService.Record(
                OperationSeverity.Error,
                "USB Oluşturma",
                "Kurulum kaynağı yenilemesi başarısız oldu.",
                "usb.release.refresh.failure",
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });

            return new UsbCreatorOperationResult(false, OperationSeverity.Error, $"Kurulum kaynağı alınamadı: {ex.Message}");
        }
    }

    public Task<UsbCreatorOperationResult> RefreshUsbDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var devices = _deviceDiscoveryService.GetUsbDevices();
            lock (_sync)
            {
                _devices = devices;
            }

            _operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "USB aygıt listesi yenilendi.",
                "usb.devices.refresh",
                new Dictionary<string, string>
                {
                    ["count"] = devices.Count.ToString()
                });

            if (devices.Count == 0)
            {
                return Task.FromResult(new UsbCreatorOperationResult(
                    true,
                    OperationSeverity.Warning,
                    "Bağlı USB aygıtı bulunamadı. Aygıtın takılı olduğundan, Windows tarafından görüldüğünden ve uygulamanın yönetici olarak çalıştığından emin olun."));
            }

            var writableCount = devices.Count(device => !device.IsSystemDisk);
            return Task.FromResult(new UsbCreatorOperationResult(
                true,
                OperationSeverity.Info,
                $"{devices.Count} USB aygıtı bulundu. {writableCount} aygıt yazma için değerlendirilebilir."));
        }
        catch (Exception ex)
        {
            _operationLogService.Record(
                OperationSeverity.Error,
                "USB Oluşturma",
                "USB aygıt listesi yenilenemedi.",
                "usb.devices.refresh.failure",
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });

            return Task.FromResult(new UsbCreatorOperationResult(false, OperationSeverity.Error, $"USB aygıtları taranamadı: {ex.Message}"));
        }
    }

    public UsbCreatorOperationResult SetManualImagePath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "Elle seçilen imaj yolu boş.");
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(imagePath));

        lock (_sync)
        {
            _manualImagePath = fullPath;
        }

        _operationLogService.Record(
            File.Exists(fullPath) ? OperationSeverity.Info : OperationSeverity.Warning,
            "USB Oluşturma",
            "Bu oturum için elle seçilen imaj yolu güncellendi.",
            "usb.manual.path.set",
            new Dictionary<string, string>
            {
                ["path"] = fullPath
            });

        return new UsbCreatorOperationResult(
            true,
            File.Exists(fullPath) ? OperationSeverity.Info : OperationSeverity.Warning,
            File.Exists(fullPath)
                ? "Elle seçilen imaj hazır. İsterseniz kaynağı yenileyebilir veya bütünlüğü doğrulayabilirsiniz."
                : "Seçilen imaj henüz bulunamıyor. Dosya yolunu kontrol edin.");
    }

    public UsbCreatorOperationResult ClearManualImageSelection()
    {
        lock (_sync)
        {
            _manualImagePath = null;
            _releaseSummary = BuildInitialSummary(_settingsService.GetSettings());
            UpdatePreparedImagePath(null);
        }

        _operationLogService.Record(
            OperationSeverity.Info,
            "USB Oluşturma",
            "Elle seçilen imaj temizlendi.",
            "usb.manual.path.clear");

        return new UsbCreatorOperationResult(true, OperationSeverity.Info, "Elle seçilen imaj temizlendi. Kaynağı yeniden yenileyin.");
    }

    public async Task<UsbCreatorOperationResult> DownloadImageAsync(CancellationToken cancellationToken = default)
    {
        ReleaseManifestSummary releaseSummary;
        lock (_sync)
        {
            releaseSummary = _releaseSummary;
        }

        if (!string.IsNullOrWhiteSpace(releaseSummary.LocalImagePath))
        {
            return new UsbCreatorOperationResult(
                true,
                OperationSeverity.Info,
                "Etkin kaynak zaten yerel bir imaj sağlıyor. Ek indirme gerekmiyor.");
        }

        if (string.IsNullOrWhiteSpace(releaseSummary.ImageUrl))
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "İndirilecek imaj adresi yok. Kaynak durumunu kontrol edin.");
        }

        var targetPath = GetDownloadPath(releaseSummary);
        var tempPath = targetPath + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        try
        {
            _operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "İmaj indirme başladı.",
                "usb.download.start",
                new Dictionary<string, string>
                {
                    ["url"] = releaseSummary.ImageUrl,
                    ["target"] = targetPath
                });

            using var response = await HttpClient.GetAsync(releaseSummary.ImageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
            using var sha256 = SHA256.Create();
            var buffer = new byte[1024 * 1024];

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var calculatedSha = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

            await output.FlushAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(releaseSummary.Sha256) &&
                !string.Equals(releaseSummary.Sha256, calculatedSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);

                lock (_sync)
                {
                    _calculatedSha256 = calculatedSha;
                    _checksumState = ChecksumVerificationState.Mismatch;
                    _checksumStatus = "İndirilen imajın bütünlük değeri beklenen SHA-256 ile eşleşmiyor.";
                }

                _operationLogService.Record(
                    OperationSeverity.Error,
                    "USB Oluşturma",
                    "İndirilen imajın bütünlük doğrulaması başarısız oldu.",
                    "usb.download.checksum.mismatch",
                    new Dictionary<string, string>
                    {
                        ["expected"] = releaseSummary.Sha256 ?? string.Empty,
                        ["calculated"] = calculatedSha
                    });

                return new UsbCreatorOperationResult(false, OperationSeverity.Error, "İndirilen imaj bütünlük doğrulamasından geçmedi.");
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(tempPath, targetPath);

            lock (_sync)
            {
                UpdatePreparedImagePath(targetPath);
                _calculatedSha256 = calculatedSha;
                _checksumState = string.IsNullOrWhiteSpace(releaseSummary.Sha256)
                    ? ChecksumVerificationState.CalculatedOnly
                    : ChecksumVerificationState.Verified;
                _checksumStatus = string.IsNullOrWhiteSpace(releaseSummary.Sha256)
                    ? "İmaj indirildi ve SHA-256 hesaplandı. Beklenen değer olmadığı için dikkatle ilerleyin."
                    : "İmaj indirildi ve beklenen SHA-256 ile doğrulandı.";
            }

            _operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "İmaj indirme tamamlandı.",
                "usb.download.complete",
                new Dictionary<string, string>
                {
                    ["target"] = targetPath,
                    ["sha256"] = calculatedSha
                });

            return new UsbCreatorOperationResult(true, OperationSeverity.Info, "İmaj indirildi ve hazırlandı.");
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);

            _operationLogService.Record(
                OperationSeverity.Error,
                "USB Oluşturma",
                "İmaj indirme başarısız oldu.",
                "usb.download.failure",
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message,
                    ["target"] = targetPath
                });

            return new UsbCreatorOperationResult(false, OperationSeverity.Error, $"İmaj indirilemedi: {ex.Message}");
        }
    }

    public async Task<UsbCreatorOperationResult> VerifyPreparedImageAsync(CancellationToken cancellationToken = default)
    {
        var imagePath = GetPreparedImagePath();
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "Doğrulanacak imaj dosyası hazır değil.");
        }

        try
        {
            var calculatedSha = await _rawDiskWriter.ComputeFileSha256Async(imagePath, cancellationToken);
            var expectedSha = GetExpectedSha256() ?? TryReadSidecarSha256(imagePath);

            UsbCreatorOperationResult result;
            lock (_sync)
            {
                _expectedSha256 = expectedSha;
                _calculatedSha256 = calculatedSha;

                if (string.IsNullOrWhiteSpace(expectedSha))
                {
                    _checksumState = ChecksumVerificationState.CalculatedOnly;
                    _checksumStatus = "SHA-256 hesaplandı ancak karşılaştırılacak beklenen değer bulunamadı.";
                    result = new UsbCreatorOperationResult(true, OperationSeverity.Warning, _checksumStatus);
                }
                else if (string.Equals(expectedSha, calculatedSha, StringComparison.OrdinalIgnoreCase))
                {
                    _checksumState = ChecksumVerificationState.Verified;
                    _checksumStatus = "İmajın SHA-256 değeri beklenen değer ile eşleşiyor.";
                    result = new UsbCreatorOperationResult(true, OperationSeverity.Info, _checksumStatus);
                }
                else
                {
                    _checksumState = ChecksumVerificationState.Mismatch;
                    _checksumStatus = "İmajın SHA-256 değeri beklenen değer ile eşleşmiyor.";
                    result = new UsbCreatorOperationResult(false, OperationSeverity.Error, _checksumStatus);
                }
            }

            _operationLogService.Record(
                result.Severity,
                "USB Oluşturma",
                result.Message,
                "usb.image.verify",
                new Dictionary<string, string>
                {
                    ["path"] = imagePath,
                    ["expected"] = expectedSha ?? string.Empty,
                    ["calculated"] = calculatedSha
                });

            return result;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _checksumState = ChecksumVerificationState.Failed;
                _checksumStatus = $"Bütünlük doğrulaması tamamlanamadı: {ex.Message}";
            }

            _operationLogService.Record(
                OperationSeverity.Error,
                "USB Oluşturma",
                "İmaj bütünlük doğrulaması başarısız oldu.",
                "usb.image.verify.failure",
                new Dictionary<string, string>
                {
                    ["path"] = imagePath,
                    ["error"] = ex.Message
                });

            return new UsbCreatorOperationResult(false, OperationSeverity.Error, $"Bütünlük doğrulaması tamamlanamadı: {ex.Message}");
        }
    }

    public async Task<UsbCreatorOperationResult> WriteImageAsync(string? usbDeviceId, bool confirmedByUser, CancellationToken cancellationToken = default)
    {
        if (!confirmedByUser)
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "Yazma işlemi için açık onay gerekiyor.");
        }

        if (!_rawDiskWriter.IsAdministrator())
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Error, "USB yazmak için uygulamayı yönetici olarak çalıştırın.");
        }

        var imagePath = GetPreparedImagePath();
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "USB belleğe yazılacak imaj hazır değil.");
        }

        var device = GetDeviceById(usbDeviceId);
        if (device is null)
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Warning, "Yazma işlemi için geçerli bir USB aygıt seçilmedi.");
        }

        var imageLength = new FileInfo(imagePath).Length;
        if (device.IsSystemDisk)
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Error, "Sistem diski USB hedefi olarak kullanılamaz.");
        }

        if (device.SizeBytes <= 0 || device.SizeBytes < imageLength)
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Error, "Seçilen USB aygıtı imaj boyutu için yeterli değil.");
        }

        var verification = await VerifyPreparedImageAsync(cancellationToken);
        var checksumState = GetChecksumState();
        if (checksumState is ChecksumVerificationState.Mismatch or ChecksumVerificationState.Failed)
        {
            return new UsbCreatorOperationResult(false, OperationSeverity.Error, "Bütünlük doğrulamasında sorun var. USB yazma işlemi engellendi.");
        }

        try
        {
            _operationLogService.Record(
                OperationSeverity.Warning,
                "USB Oluşturma",
                "USB yazma işlemi başlıyor.",
                "usb.write.start",
                new Dictionary<string, string>
                {
                    ["device"] = device.PhysicalPath,
                    ["image"] = imagePath,
                    ["verification"] = verification.Message
                });

            await _rawDiskWriter.WriteImageAsync(imagePath, device, cancellationToken);

            var calculatedSha = GetCalculatedSha256();
            if (string.IsNullOrWhiteSpace(calculatedSha))
            {
                calculatedSha = await _rawDiskWriter.ComputeFileSha256Async(imagePath, cancellationToken);
                lock (_sync)
                {
                    _calculatedSha256 = calculatedSha;
                }
            }

            var deviceSha = await _rawDiskWriter.ComputeDeviceSha256Async(device.PhysicalPath, imageLength, cancellationToken);
            if (!string.Equals(deviceSha, calculatedSha, StringComparison.OrdinalIgnoreCase))
            {
                _operationLogService.Record(
                    OperationSeverity.Error,
                    "USB Oluşturma",
                    "Yazma sonrası doğrulama başarısız oldu.",
                    "usb.write.validation.failure",
                    new Dictionary<string, string>
                    {
                        ["device"] = device.PhysicalPath,
                        ["expected"] = calculatedSha ?? string.Empty,
                        ["calculated"] = deviceSha
                    });

                return new UsbCreatorOperationResult(false, OperationSeverity.Error, "USB yazma tamamlandı ancak son doğrulama eşleşmedi.");
            }

            lock (_sync)
            {
                _checksumStatus = $"İmaj yazıldı ve geri okuma doğrulaması tamamlandı: {deviceSha}";
            }

            _operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "USB yazma ve doğrulama tamamlandı.",
                "usb.write.complete",
                new Dictionary<string, string>
                {
                    ["device"] = device.PhysicalPath,
                    ["sha256"] = deviceSha
                });

            await RefreshUsbDevicesAsync(CancellationToken.None);
            return new UsbCreatorOperationResult(true, OperationSeverity.Info, "USB yazma ve son doğrulama başarıyla tamamlandı.");
        }
        catch (Exception ex)
        {
            _operationLogService.Record(
                OperationSeverity.Error,
                "USB Oluşturma",
                "USB yazma işlemi başarısız oldu.",
                "usb.write.failure",
                new Dictionary<string, string>
                {
                    ["device"] = device.PhysicalPath,
                    ["error"] = ex.Message
                });

            return new UsbCreatorOperationResult(false, OperationSeverity.Error, $"USB yazma işlemi başarısız: {ex.Message}");
        }
    }

    private UsbDeviceEntry ToDeviceEntry(UsbPhysicalDeviceInfo device, long imageSizeBytes)
    {
        var mountedVolumesLabel = device.MountedVolumes.Count == 0
            ? "Bağlı sürücü harfi yok"
            : string.Join(", ", device.MountedVolumes);

        var canWrite = true;
        string safetyStatus;

        if (device.IsSystemDisk)
        {
            canWrite = false;
            safetyStatus = "Engelli: sistem diski";
        }
        else if (device.SizeBytes <= 0)
        {
            canWrite = false;
            safetyStatus = "Engelli: kapasite okunamadı";
        }
        else if (imageSizeBytes > 0 && device.SizeBytes < imageSizeBytes)
        {
            canWrite = false;
            safetyStatus = "Engelli: aygıt imaj için küçük";
        }
        else
        {
            safetyStatus = "Yazmaya uygun";
        }

        return new UsbDeviceEntry(
            Id: device.Id,
            DisplayName: $"{device.Model} ({FormatBytes(device.SizeBytes)})",
            Model: device.Model,
            PhysicalPath: device.PhysicalPath,
            SizeLabel: FormatBytes(device.SizeBytes),
            SizeBytes: device.SizeBytes,
            MountedVolumesLabel: mountedVolumesLabel,
            IsSystemDisk: device.IsSystemDisk,
            CanWrite: canWrite,
            SafetyStatus: safetyStatus);
    }

    private void UpdatePreparedImagePath(string? preparedImagePath)
    {
        var expectedPath = string.IsNullOrWhiteSpace(preparedImagePath) ? null : Path.GetFullPath(preparedImagePath);
        if (string.Equals(_preparedImagePath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _preparedImagePath = expectedPath;
        _calculatedSha256 = null;
        _checksumState = ChecksumVerificationState.NotStarted;
        _checksumStatus = "Bütünlük doğrulaması henüz yapılmadı.";
    }

    private string? ResolvePreparedImagePath(ReleaseManifestSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.LocalImagePath) && File.Exists(summary.LocalImagePath))
        {
            return summary.LocalImagePath;
        }

        if (string.IsNullOrWhiteSpace(summary.ImageUrl))
        {
            return null;
        }

        var cachedDownloadPath = GetDownloadPath(summary);
        return File.Exists(cachedDownloadPath) ? cachedDownloadPath : null;
    }

    private string GetDownloadPath(ReleaseManifestSummary summary)
    {
        var safeChannel = string.IsNullOrWhiteSpace(summary.Channel) ? "unknown" : summary.Channel;
        var safeVersion = string.IsNullOrWhiteSpace(summary.Version) ? "unknown" : summary.Version;
        var baseDirectory = Path.Combine(
            _appPathService.GetPaths().DownloadsDirectory,
            safeChannel,
            safeVersion);

        var imageName = string.IsNullOrWhiteSpace(summary.ImageName) ? "cigertool-os.img" : summary.ImageName;
        return Path.Combine(baseDirectory, imageName);
    }

    private static ReleaseManifestSummary BuildInitialSummary(ApplicationSettings settings)
    {
        return new ReleaseManifestSummary(
            Channel: settings.DefaultChannel,
            Version: "Henüz yenilenmedi",
            ImageName: "İmaj seçilmedi",
            ImageUrl: null,
            Sha256: null,
            Notes: "Kaynağı yenileyerek çevrimiçi yayın bilgisi, yerel geçersiz kılma veya elle seçilen dosya ile devam edebilirsiniz.",
            SizeBytes: null,
            SourceDescription: settings.DefaultManifestUrl ?? "Varsayılan yayın adresi tanımlı değil.",
            Status: "Yayın bilgisi henüz yenilenmedi.",
            ModeLabel: "Bekleniyor",
            LocalImagePath: null,
            IsCachedFallback: false);
    }

    private long ResolveImageSizeBytes(ReleaseManifestSummary releaseSummary, string? preparedImagePath)
    {
        if (!string.IsNullOrWhiteSpace(preparedImagePath) && File.Exists(preparedImagePath))
        {
            return new FileInfo(preparedImagePath).Length;
        }

        return releaseSummary.SizeBytes ?? 0L;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "Bilinmiyor";
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

    private static string? TryReadSidecarSha256(string imagePath)
    {
        var candidates = new[]
        {
            imagePath + ".sha256",
            Path.ChangeExtension(imagePath, ".sha256")
        }
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(candidate).Trim();
                var token = content
                    .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token.Trim().ToLowerInvariant();
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private string? GetManualImagePath()
    {
        lock (_sync)
        {
            return _manualImagePath;
        }
    }

    private string? GetPreparedImagePath()
    {
        lock (_sync)
        {
            return _preparedImagePath;
        }
    }

    private string? GetExpectedSha256()
    {
        lock (_sync)
        {
            return _expectedSha256;
        }
    }

    private string? GetCalculatedSha256()
    {
        lock (_sync)
        {
            return _calculatedSha256;
        }
    }

    private ChecksumVerificationState GetChecksumState()
    {
        lock (_sync)
        {
            return _checksumState;
        }
    }

    private UsbPhysicalDeviceInfo? GetDeviceById(string? usbDeviceId)
    {
        lock (_sync)
        {
            return _devices.FirstOrDefault(device => string.Equals(device.Id, usbDeviceId, StringComparison.OrdinalIgnoreCase));
        }
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

    private void ResetChecksumState()
    {
        _calculatedSha256 = null;
        _checksumState = ChecksumVerificationState.NotStarted;
        _checksumStatus = "Bütünlük doğrulaması henüz yapılmadı.";
    }

    private static string FormatChecksumState(ChecksumVerificationState state) => state switch
    {
        ChecksumVerificationState.NotStarted => "Bekliyor",
        ChecksumVerificationState.Verified => "Doğrulandı",
        ChecksumVerificationState.CalculatedOnly => "Hesaplandı",
        ChecksumVerificationState.Mismatch => "Eşleşmiyor",
        ChecksumVerificationState.Failed => "Başarısız",
        _ => state.ToString()
    };

    private IReadOnlyList<UsbPhysicalDeviceInfo> TryGetInitialDevices()
    {
        try
        {
            return _deviceDiscoveryService.GetUsbDevices();
        }
        catch
        {
            return Array.Empty<UsbPhysicalDeviceInfo>();
        }
    }
}

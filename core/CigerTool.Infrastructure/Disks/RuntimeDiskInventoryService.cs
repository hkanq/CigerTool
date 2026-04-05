using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Disks;

[SupportedOSPlatform("windows")]
public sealed class RuntimeDiskInventoryService(IOperationLogService operationLogService) : IDiskInventoryService
{
    private const string StorageNamespace = @"ROOT\Microsoft\Windows\Storage";
    private const string SmartNamespace = @"ROOT\WMI";

    public DiskWorkspaceSnapshot GetSnapshot()
    {
        var disks = GetCurrentDisks();
        var systemDrive = global::System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var systemDisk = disks.FirstOrDefault(disk => disk.IsSystemVolume);
        var removableCount = disks.Count(disk => disk.IsRemovable);
        var attentionCount = disks.Count(disk => !string.Equals(disk.HealthLabel, "Sağlıklı", StringComparison.OrdinalIgnoreCase));
        var ssdCount = disks.Count(disk => disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                                          disk.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var hddCount = disks.Count(disk => disk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase));
        var riskCount = disks.Count(disk => string.Equals(disk.HealthLabel, "Risk var", StringComparison.OrdinalIgnoreCase));

        return new DiskWorkspaceSnapshot(
            Heading: "Diskler ve Sağlık",
            Summary: "Bağlı sürücülerin marka, bağlantı tipi, medya sınıfı, sağlık özeti ve kapasite durumunu buradan izleyebilirsiniz.",
            System: new SystemSummary(
                MachineName: global::System.Environment.MachineName,
                OperatingSystem: RuntimeInformation.OSDescription,
                Architecture: RuntimeInformation.OSArchitecture.ToString(),
                Framework: RuntimeInformation.FrameworkDescription,
                CurrentUser: global::System.Environment.UserName,
                SystemDrive: systemDrive,
                UptimeLabel: ByteSizeFormatter.FormatUptime(TimeSpan.FromMilliseconds(global::System.Environment.TickCount64))),
            Metrics:
            [
                new CardMetric("Toplam sürücü", disks.Count.ToString(), "Bağlı ve erişilebilir sürücüler."),
                new CardMetric("SSD / NVMe", ssdCount.ToString(), "Katı hal depolama sınıfındaki sürücüler."),
                new CardMetric("HDD", hddCount.ToString(), "Mekanik disk sınıfındaki sürücüler."),
                new CardMetric("USB / harici", removableCount.ToString(), "USB bellek ve harici diskler."),
                new CardMetric("Risk / izleme", $"{riskCount} / {attentionCount}", "Sağlık veya boş alan nedeniyle dikkat isteyen sürücüler."),
                new CardMetric(
                    "Sistem sürücüsü",
                    systemDisk?.DriveLetter ?? systemDrive,
                    systemDisk is null
                        ? "Sistem sürücüsü ayrıntısı okunamadı."
                        : $"{systemDisk.MediaType} · {systemDisk.FreeLabel} boş")
            ],
            Disks: disks,
            Notes:
            [
                "Sağlık özeti Windows depolama durumu, operasyon durumu, arıza öngörüsü sinyali ve boş alan bilgisiyle oluşturulur.",
                "SSD / HDD / USB bellek sınıfı model, bağlantı tipi ve depolama telemetrisi birlikte değerlendirilerek gösterilir.",
                "Performans testi seçilen sürücü üzerinde geçici test dosyasıyla yapılır; özellikle sistem sürücüsünde arka plan yükü sonucu etkileyebilir."
            ]);
    }

    public IReadOnlyList<DiskSummary> GetCurrentDisks()
    {
        var systemDrive = (global::System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var storageRecords = QueryStorageDiskRecords();
        var physicalRecords = QueryPhysicalDiskRecords();
        var smartRecords = QuerySmartStatusRecords();
        var mappings = new List<DriveMapping>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                var mapping = ResolveDriveMapping(drive, systemDrive, storageRecords, physicalRecords, smartRecords);
                if (mapping is not null)
                {
                    mappings.Add(mapping);
                }
            }
            catch (Exception ex)
            {
                operationLogService.Record(
                    OperationSeverity.Warning,
                    "Diskler",
                    $"Sürücü bilgisi okunamadı: {drive.Name}",
                    "disks.read.failure",
                    new Dictionary<string, string>
                    {
                        ["drive"] = drive.Name,
                        ["error"] = ex.Message
                    });
            }
        }

        return mappings
            .Select(mapping => ToDiskSummary(mapping, systemDrive))
            .OrderByDescending(disk => disk.IsSystemVolume)
            .ThenBy(disk => disk.IsRemovable)
            .ThenBy(disk => disk.DriveLetter)
            .ToArray();
    }

    public DiskSummary? FindById(string id)
    {
        return GetCurrentDisks().FirstOrDefault(disk => string.Equals(disk.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private DriveMapping? ResolveDriveMapping(
        DriveInfo drive,
        string systemDrive,
        IReadOnlyDictionary<int, StorageDiskRecord> storageRecords,
        IReadOnlyList<PhysicalDiskRecord> physicalRecords,
        IReadOnlyList<SmartStatusRecord> smartRecords)
    {
        var driveLetter = drive.Name.TrimEnd('\\');
        var diskNumber = TryResolveDiskNumberFromDriveLetter(driveLetter);
        if (diskNumber < 0)
        {
            return null;
        }

        var win32Record = QueryWin32DiskRecord(diskNumber);
        if (win32Record is null)
        {
            return null;
        }

        storageRecords.TryGetValue(diskNumber, out var storageRecord);
        var physicalRecord = MatchPhysicalRecord(win32Record, drive.TotalSize, physicalRecords);
        var smartRecord = MatchSmartRecord(win32Record, physicalRecord, smartRecords);
        var modelSource = FirstNonEmpty(storageRecord?.FriendlyName, win32Record.Model, physicalRecord?.FriendlyName, "Bilinmeyen aygıt");

        return new DriveMapping(
            DriveLetter: driveLetter,
            VolumeLabel: SafeRead(() => drive.VolumeLabel, string.Empty),
            FileSystem: SafeRead(() => drive.DriveFormat, "Bilinmiyor"),
            TotalBytes: drive.TotalSize,
            FreeBytes: drive.AvailableFreeSpace,
            Model: modelSource,
            Brand: ExtractBrand(modelSource),
            InterfaceType: win32Record.InterfaceType,
            BusTypeLabel: ResolveBusTypeLabel(storageRecord?.BusTypeCode, win32Record.InterfaceType),
            MediaType: FirstNonEmpty(physicalRecord?.MediaTypeLabel, win32Record.MediaType, "Bilinmiyor"),
            Status: FirstNonEmpty(storageRecord?.HealthLabel, win32Record.Status, physicalRecord?.HealthLabel, "Bilinmiyor"),
            OperationalStatus: FirstNonEmpty(storageRecord?.OperationalStatusLabel, physicalRecord?.OperationalStatusLabel, "Bilinmiyor"),
            IsRemovable: drive.DriveType == DriveType.Removable ||
                         ResolveBusTypeLabel(storageRecord?.BusTypeCode, win32Record.InterfaceType).Contains("USB", StringComparison.OrdinalIgnoreCase),
            Index: win32Record.Index,
            IsBootOrSystem: storageRecord?.IsSystem == true ||
                            storageRecord?.IsBoot == true ||
                            string.Equals(driveLetter, systemDrive, StringComparison.OrdinalIgnoreCase),
            PredictFailure: smartRecord?.PredictFailure == true,
            SmartReason: smartRecord?.ReasonLabel);
    }

    private DiskSummary ToDiskSummary(DriveMapping mapping, string systemDrive)
    {
        var totalBytes = mapping.TotalBytes;
        var freeBytes = mapping.FreeBytes;
        var usedBytes = Math.Max(0, totalBytes - freeBytes);
        var usagePercent = totalBytes <= 0 ? 0 : (int)Math.Round((double)usedBytes / totalBytes * 100d, MidpointRounding.AwayFromZero);
        var isSystemVolume = string.Equals(mapping.DriveLetter, systemDrive, StringComparison.OrdinalIgnoreCase);
        var connectionType = ResolveConnectionType(mapping.BusTypeLabel, mapping.InterfaceType, mapping.IsRemovable);
        var mediaClass = ResolveMediaClass(mapping);
        var warningSummary = BuildWarningSummary(mapping, isSystemVolume, freeBytes, totalBytes, mediaClass);
        var healthLabel = BuildHealthLabel(mapping, warningSummary);
        var displayName = string.IsNullOrWhiteSpace(mapping.VolumeLabel)
            ? mapping.DriveLetter
            : $"{mapping.DriveLetter} - {mapping.VolumeLabel}";

        return new DiskSummary(
            Id: mapping.DriveLetter,
            Name: displayName,
            DriveLetter: mapping.DriveLetter,
            FileSystem: mapping.FileSystem,
            ConnectionType: connectionType,
            CapacityLabel: ByteSizeFormatter.Format(totalBytes),
            UsedLabel: ByteSizeFormatter.Format(usedBytes),
            FreeLabel: ByteSizeFormatter.Format(freeBytes),
            LayoutLabel: isSystemVolume ? "Sistem sürücüsü" : "Veri sürücüsü",
            HealthLabel: healthLabel,
            TotalBytes: totalBytes,
            UsedBytes: usedBytes,
            FreeBytes: freeBytes,
            IsSystemVolume: isSystemVolume,
            IsReady: true,
            DeviceModel: BuildDeviceModel(mapping.Brand, mapping.Model),
            BusType: mapping.BusTypeLabel,
            MediaType: mediaClass,
            IdentityLabel: BuildIdentityLabel(mapping, mediaClass, connectionType),
            WarningSummary: warningSummary,
            UsagePercent: usagePercent,
            IsRemovable: mapping.IsRemovable,
            SupportsRawAccess: RawVolumeAccessScope.IsAdministrator());
    }

    private static int TryResolveDiskNumberFromDriveLetter(string driveLetter)
    {
        try
        {
            var normalized = driveLetter.Trim().TrimEnd('\\').TrimEnd(':');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return -1;
            }

            using var searcher = new ManagementObjectSearcher(
                StorageNamespace,
                $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{normalized}'");

            foreach (ManagementObject partition in searcher.Get())
            {
                using (partition)
                {
                    return Convert.ToInt32(partition["DiskNumber"] ?? -1);
                }
            }
        }
        catch
        {
        }

        return -1;
    }

    private static Win32DiskRecord? QueryWin32DiskRecord(int diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Model, InterfaceType, MediaType, Index, Status, PNPDeviceID FROM Win32_DiskDrive WHERE Index = {diskNumber}");

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    return new Win32DiskRecord(
                        Index: Convert.ToInt32(disk["Index"] ?? -1),
                        Model: disk["Model"]?.ToString() ?? "Bilinmeyen aygıt",
                        InterfaceType: disk["InterfaceType"]?.ToString() ?? "Bilinmiyor",
                        MediaType: disk["MediaType"]?.ToString() ?? "Bilinmiyor",
                        Status: disk["Status"]?.ToString() ?? "Bilinmiyor",
                        PnpDeviceId: disk["PNPDeviceID"]?.ToString() ?? string.Empty);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static IReadOnlyDictionary<int, StorageDiskRecord> QueryStorageDiskRecords()
    {
        try
        {
            var result = new Dictionary<int, StorageDiskRecord>();
            using var searcher = new ManagementObjectSearcher(
                StorageNamespace,
                "SELECT Number, FriendlyName, Model, BusType, HealthStatus, OperationalStatus, IsBoot, IsSystem FROM MSFT_Disk");

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    var number = Convert.ToInt32(disk["Number"] ?? -1);
                    if (number < 0)
                    {
                        continue;
                    }

                    result[number] = new StorageDiskRecord(
                        Number: number,
                        FriendlyName: FirstNonEmpty(disk["FriendlyName"]?.ToString(), disk["Model"]?.ToString()),
                        BusTypeCode: Convert.ToInt32(disk["BusType"] ?? 0),
                        HealthLabel: ResolveStorageHealthLabel(Convert.ToInt32(disk["HealthStatus"] ?? 0)),
                        OperationalStatusLabel: ResolveOperationalStatusLabel(disk["OperationalStatus"]),
                        IsBoot: Convert.ToBoolean(disk["IsBoot"] ?? false),
                        IsSystem: Convert.ToBoolean(disk["IsSystem"] ?? false));
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<int, StorageDiskRecord>();
        }
    }

    private static IReadOnlyList<PhysicalDiskRecord> QueryPhysicalDiskRecords()
    {
        try
        {
            var result = new List<PhysicalDiskRecord>();
            using var searcher = new ManagementObjectSearcher(
                StorageNamespace,
                "SELECT FriendlyName, MediaType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk");

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    result.Add(new PhysicalDiskRecord(
                        FriendlyName: disk["FriendlyName"]?.ToString() ?? string.Empty,
                        MediaTypeLabel: ResolvePhysicalMediaTypeLabel(Convert.ToInt32(disk["MediaType"] ?? 0)),
                        HealthLabel: ResolveStorageHealthLabel(Convert.ToInt32(disk["HealthStatus"] ?? 0)),
                        OperationalStatusLabel: ResolveOperationalStatusLabel(disk["OperationalStatus"]),
                        SizeBytes: Convert.ToInt64(disk["Size"] ?? 0L)));
                }
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<SmartStatusRecord> QuerySmartStatusRecords()
    {
        try
        {
            var result = new List<SmartStatusRecord>();
            using var searcher = new ManagementObjectSearcher(
                SmartNamespace,
                "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    result.Add(new SmartStatusRecord(
                        InstanceName: item["InstanceName"]?.ToString() ?? string.Empty,
                        PredictFailure: Convert.ToBoolean(item["PredictFailure"] ?? false),
                        ReasonCode: Convert.ToInt32(item["Reason"] ?? 0)));
                }
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static PhysicalDiskRecord? MatchPhysicalRecord(
        Win32DiskRecord win32Record,
        long targetSizeBytes,
        IReadOnlyList<PhysicalDiskRecord> physicalRecords)
    {
        var modelMatch = physicalRecords.FirstOrDefault(record =>
            !string.IsNullOrWhiteSpace(record.FriendlyName) &&
            (record.FriendlyName.Contains(win32Record.Model, StringComparison.OrdinalIgnoreCase) ||
             win32Record.Model.Contains(record.FriendlyName, StringComparison.OrdinalIgnoreCase)));

        if (modelMatch is not null)
        {
            return modelMatch;
        }

        return physicalRecords.FirstOrDefault(record =>
            record.SizeBytes > 0 &&
            targetSizeBytes > 0 &&
            Math.Abs(record.SizeBytes - targetSizeBytes) <= 1024L * 1024 * 1024);
    }

    private static SmartStatusRecord? MatchSmartRecord(
        Win32DiskRecord win32Record,
        PhysicalDiskRecord? physicalRecord,
        IReadOnlyList<SmartStatusRecord> smartRecords)
    {
        var candidates = new[]
        {
            NormalizeForSearch(win32Record.Model),
            NormalizeForSearch(win32Record.PnpDeviceId),
            NormalizeForSearch(physicalRecord?.FriendlyName)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

        foreach (var smartRecord in smartRecords)
        {
            var instanceName = NormalizeForSearch(smartRecord.InstanceName);
            if (candidates.Any(candidate => instanceName.Contains(candidate, StringComparison.Ordinal)))
            {
                return smartRecord;
            }
        }

        return null;
    }

    private static string BuildWarningSummary(DriveMapping mapping, bool isSystemVolume, long freeBytes, long totalBytes, string mediaClass)
    {
        var messages = new List<string>();

        if (mapping.PredictFailure)
        {
            messages.Add(string.IsNullOrWhiteSpace(mapping.SmartReason)
                ? "Disk arıza öngörüsü sinyali veriyor."
                : $"Disk arıza öngörüsü sinyali veriyor: {mapping.SmartReason}");
        }

        if (!string.Equals(mapping.Status, "Sağlıklı", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mapping.Status, "Bilinmiyor", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add($"Depolama durumu: {mapping.Status}");
        }

        if (!string.Equals(mapping.OperationalStatus, "Çalışıyor", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mapping.OperationalStatus, "Bilinmiyor", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add($"Operasyon durumu: {mapping.OperationalStatus}");
        }

        if (totalBytes > 0 && (freeBytes * 100L / totalBytes) < 10)
        {
            messages.Add("Boş alan yüzde 10'un altına düştü.");
        }

        if (isSystemVolume)
        {
            messages.Add("Çalışan sistem sürücüsü.");
        }

        if (mediaClass.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add("Harici bağlantı nedeniyle hız ve güç koşulları değişebilir.");
        }

        return messages.Count == 0 ? "Genel durum sağlıklı görünüyor." : string.Join(" ", messages);
    }

    private static string BuildHealthLabel(DriveMapping mapping, string warningSummary)
    {
        if (mapping.PredictFailure ||
            string.Equals(mapping.Status, "Risk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mapping.Status, "Kritik", StringComparison.OrdinalIgnoreCase))
        {
            return "Risk var";
        }

        if (warningSummary.Contains("Boş alan", StringComparison.OrdinalIgnoreCase) ||
            warningSummary.Contains("Harici bağlantı", StringComparison.OrdinalIgnoreCase) ||
            warningSummary.Contains("Çalışan sistem", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(mapping.Status, "Sağlıklı", StringComparison.OrdinalIgnoreCase))
        {
            return "İzlenmeli";
        }

        return "Sağlıklı";
    }

    private static string BuildIdentityLabel(DriveMapping mapping, string mediaClass, string connectionType)
    {
        return string.Join(" · ", new[]
        {
            $"Disk {mapping.Index}",
            mapping.Brand,
            mediaClass,
            connectionType
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildDeviceModel(string brand, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return brand;
        }

        if (model.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
        {
            return model;
        }

        return string.IsNullOrWhiteSpace(brand) ? model : $"{brand} {model}";
    }

    private static string ResolveConnectionType(string busTypeLabel, string interfaceType, bool isRemovable)
    {
        if (!string.IsNullOrWhiteSpace(busTypeLabel) && !string.Equals(busTypeLabel, "Bilinmiyor", StringComparison.OrdinalIgnoreCase))
        {
            return busTypeLabel;
        }

        if (isRemovable)
        {
            return "USB";
        }

        return interfaceType;
    }

    private static string ResolveMediaClass(DriveMapping mapping)
    {
        var source = string.Join(' ', new[] { mapping.MediaType, mapping.Model, mapping.BusTypeLabel, mapping.InterfaceType });

        if (source.Contains("NVME", StringComparison.OrdinalIgnoreCase))
        {
            return "NVMe SSD";
        }

        if (source.Contains("SSD", StringComparison.OrdinalIgnoreCase) || string.Equals(mapping.MediaType, "SSD", StringComparison.OrdinalIgnoreCase))
        {
            return mapping.IsRemovable ? "USB SSD" : "SSD";
        }

        if (source.Contains("USB", StringComparison.OrdinalIgnoreCase) && mapping.IsRemovable && mapping.TotalBytes <= 512L * 1024 * 1024 * 1024)
        {
            return "USB Bellek";
        }

        if (source.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("HARD DISK", StringComparison.OrdinalIgnoreCase))
        {
            return mapping.IsRemovable ? "USB HDD" : "HDD";
        }

        if (mapping.IsRemovable)
        {
            return "Harici Disk";
        }

        return "Depolama Aygıtı";
    }

    private static string ResolveBusTypeLabel(int? busTypeCode, string interfaceType)
    {
        if (busTypeCode is null)
        {
            return interfaceType;
        }

        return busTypeCode.Value switch
        {
            7 => "USB",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            17 => "NVMe",
            8 => "RAID",
            9 => "iSCSI",
            3 => "ATA",
            _ => string.IsNullOrWhiteSpace(interfaceType) ? "Bilinmiyor" : interfaceType
        };
    }

    private static string ResolveStorageHealthLabel(int healthStatus)
    {
        return healthStatus switch
        {
            0 => "Sağlıklı",
            1 => "Sağlıklı",
            2 => "İzlenmeli",
            3 => "Risk",
            5 => "Risk",
            _ => "Bilinmiyor"
        };
    }

    private static string ResolveOperationalStatusLabel(object? rawValue)
    {
        if (rawValue is ushort[] values && values.Length > 0)
        {
            return ResolveOperationalStatusLabel(values[0]);
        }

        if (rawValue is Array array && array.Length > 0)
        {
            var first = array.GetValue(0);
            if (first is not null)
            {
                return ResolveOperationalStatusLabel(first);
            }
        }

        if (rawValue is null)
        {
            return "Bilinmiyor";
        }

        var code = Convert.ToInt32(rawValue);
        return code switch
        {
            2 => "Çalışıyor",
            3 => "Kısıtlı",
            5 => "Bakım gerekiyor",
            6 => "Stres altında",
            7 => "Tahmini arıza",
            8 => "Başlatılıyor",
            _ => "Bilinmiyor"
        };
    }

    private static string ResolvePhysicalMediaTypeLabel(int mediaTypeCode)
    {
        return mediaTypeCode switch
        {
            3 => "HDD",
            4 => "SSD",
            5 => "SCM",
            _ => "Bilinmiyor"
        };
    }

    private static string ExtractBrand(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "Bilinmeyen Marka";
        }

        var normalized = model.Trim();
        var knownBrands = new[]
        {
            "Samsung", "Kingston", "Crucial", "Western Digital", "WD", "Seagate", "Toshiba",
            "SanDisk", "Intel", "Micron", "ADATA", "Transcend", "Kioxia", "SK hynix", "Corsair", "Lexar"
        };

        var match = knownBrands.FirstOrDefault(brand => normalized.StartsWith(brand, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match))
        {
            return match;
        }

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Bilinmeyen Marka";
    }

    private static string SafeRead(Func<string> action, string fallback)
    {
        try
        {
            var value = action();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string ResolveSmartReason(int reasonCode)
    {
        return reasonCode switch
        {
            0 => "Windows uyarı nedeni bildirmedi",
            1 => "Eşik aşımı bildirildi",
            2 => "Sürücü kendi tanılama uyarısı verdi",
            _ => $"Neden kodu {reasonCode}"
        };
    }

    private sealed record Win32DiskRecord(
        int Index,
        string Model,
        string InterfaceType,
        string MediaType,
        string Status,
        string PnpDeviceId);

    private sealed record StorageDiskRecord(
        int Number,
        string FriendlyName,
        int BusTypeCode,
        string HealthLabel,
        string OperationalStatusLabel,
        bool IsBoot,
        bool IsSystem);

    private sealed record PhysicalDiskRecord(
        string FriendlyName,
        string MediaTypeLabel,
        string HealthLabel,
        string OperationalStatusLabel,
        long SizeBytes);

    private sealed record SmartStatusRecord(
        string InstanceName,
        bool PredictFailure,
        int ReasonCode)
    {
        public string ReasonLabel => ResolveSmartReason(ReasonCode);
    }

    private sealed record DriveMapping(
        string DriveLetter,
        string VolumeLabel,
        string FileSystem,
        long TotalBytes,
        long FreeBytes,
        string Model,
        string Brand,
        string InterfaceType,
        string BusTypeLabel,
        string MediaType,
        string Status,
        string OperationalStatus,
        bool IsRemovable,
        int Index,
        bool IsBootOrSystem,
        bool PredictFailure,
        string? SmartReason);
}

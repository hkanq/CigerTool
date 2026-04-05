using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
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
        var healthyCount = disks.Count(disk => string.Equals(disk.HealthLabel, "Sağlıklı", StringComparison.OrdinalIgnoreCase));
        var attentionCount = disks.Count(disk => string.Equals(disk.HealthLabel, "Dikkat gerekiyor", StringComparison.OrdinalIgnoreCase));
        var riskCount = disks.Count(disk => string.Equals(disk.HealthLabel, "Riskli", StringComparison.OrdinalIgnoreCase));
        var solidStateCount = disks.Count(disk => disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase));
        var mechanicalCount = disks.Count(disk => disk.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase));

        return new DiskWorkspaceSnapshot(
            Heading: "Diskler ve Sağlık",
            Summary: "Bağlı disklerin marka, model, bağlantı tipi, gerçek kapasite durumu, sağlık özeti ve ayrıntılı teknik verilerini tek ekranda görebilirsiniz.",
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
                new CardMetric("Toplam disk", disks.Count.ToString(), "Bağlı ve okunabilen diskler."),
                new CardMetric("Sağlıklı", healthyCount.ToString(), "Şu an sorun sinyali vermeyen diskler."),
                new CardMetric("Dikkat", attentionCount.ToString(), "Takip edilmesi gereken diskler."),
                new CardMetric("Risk", riskCount.ToString(), "Arıza veya veri riski taşıyan diskler."),
                new CardMetric("SSD / NVMe", solidStateCount.ToString(), "Katı hal depolama sınıfındaki diskler."),
                new CardMetric("HDD / harici", $"{mechanicalCount} / {removableCount}", "Mekanik ve USB/harici depolama sayısı."),
                new CardMetric(
                    "Sistem diski",
                    systemDisk?.DriveLetter ?? systemDrive,
                    systemDisk is null
                        ? "Sistem diski ayrıntısı okunamadı."
                        : $"{systemDisk.DeviceModel} · {systemDisk.HealthLabel}")
            ],
            Disks: disks,
            Notes:
            [
                "Sağlık özeti Windows depolama durumu, SMART arıza öngörüsü, önemli SMART sayaçları ve boş alan durumu birlikte değerlendirilerek oluşturulur.",
                "Benchmark sonuçları artık önbellek etkisini azaltan yerel Windows disk değerlendirmesi ile hesaplanır; yine de USB köprüleri ve arka plan yükü sonucu etkileyebilir.",
                "SMART ayrıntıları sürücü ve denetleyici desteğine bağlıdır. Desteklenmeyen alanlar dürüstçe boş bırakılır."
            ]);
    }

    public IReadOnlyList<DiskSummary> GetCurrentDisks()
    {
        var systemDrive = (global::System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var storageRecords = QueryStorageDiskRecords();
        var physicalRecords = QueryPhysicalDiskRecords();
        var smartStatusRecords = QuerySmartStatusRecords();
        var smartDataRecords = QuerySmartDataRecords();
        var smartThresholdRecords = QuerySmartThresholdRecords();
        var mappings = new List<DriveMapping>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                var mapping = ResolveDriveMapping(
                    drive,
                    systemDrive,
                    storageRecords,
                    physicalRecords,
                    smartStatusRecords,
                    smartDataRecords,
                    smartThresholdRecords);

                if (mapping is not null)
                {
                    mappings.Add(mapping);
                }
            }
            catch (Exception ex)
            {
                operationLogService.Record(
                    Domain.Enums.OperationSeverity.Warning,
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
        IReadOnlyList<SmartStatusRecord> smartStatusRecords,
        IReadOnlyList<SmartVendorRecord> smartDataRecords,
        IReadOnlyList<SmartVendorRecord> smartThresholdRecords)
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
        var smartStatus = MatchSmartStatusRecord(win32Record, physicalRecord, smartStatusRecords);
        var smartData = MatchSmartVendorRecord(win32Record, physicalRecord, smartDataRecords);
        var smartThresholds = MatchSmartVendorRecord(win32Record, physicalRecord, smartThresholdRecords);
        var smartSnapshot = ParseSmartSnapshot(smartData?.VendorSpecific, smartThresholds?.VendorSpecific);
        var modelSource = FirstNonEmpty(storageRecord?.FriendlyName, win32Record.Model, physicalRecord?.FriendlyName, "Bilinmeyen aygıt");
        var brand = ExtractBrand(modelSource, physicalRecord?.Manufacturer, win32Record.Manufacturer);
        var status = FirstNonEmpty(storageRecord?.HealthLabel, win32Record.Status, physicalRecord?.HealthLabel, "Bilinmiyor");
        var operationalStatus = FirstNonEmpty(storageRecord?.OperationalStatusLabel, physicalRecord?.OperationalStatusLabel, "Bilinmiyor");

        return new DriveMapping(
            DriveLetter: driveLetter,
            VolumeLabel: SafeRead(() => drive.VolumeLabel, string.Empty),
            FileSystem: SafeRead(() => drive.DriveFormat, "Bilinmiyor"),
            TotalBytes: drive.TotalSize,
            FreeBytes: drive.AvailableFreeSpace,
            Model: modelSource,
            Brand: brand,
            InterfaceType: win32Record.InterfaceType,
            BusTypeLabel: ResolveBusTypeLabel(storageRecord?.BusTypeCode, win32Record.InterfaceType),
            MediaType: FirstNonEmpty(physicalRecord?.MediaTypeLabel, win32Record.MediaType, "Bilinmiyor"),
            Status: status,
            OperationalStatus: operationalStatus,
            IsRemovable: drive.DriveType == DriveType.Removable ||
                         ResolveBusTypeLabel(storageRecord?.BusTypeCode, win32Record.InterfaceType).Contains("USB", StringComparison.OrdinalIgnoreCase),
            Index: win32Record.Index,
            IsBootOrSystem: storageRecord?.IsSystem == true ||
                            storageRecord?.IsBoot == true ||
                            string.Equals(driveLetter, systemDrive, StringComparison.OrdinalIgnoreCase),
            PredictFailure: smartStatus?.PredictFailure == true,
            SmartReason: smartStatus?.ReasonLabel,
            SerialNumber: FirstNonEmpty(physicalRecord?.SerialNumber, win32Record.SerialNumber),
            FirmwareVersion: FirstNonEmpty(physicalRecord?.FirmwareVersion, win32Record.FirmwareRevision),
            LogicalSectorSize: physicalRecord?.LogicalSectorSize ?? win32Record.BytesPerSector,
            PhysicalSectorSize: physicalRecord?.PhysicalSectorSize ?? 0,
            SpindleSpeed: physicalRecord?.SpindleSpeed ?? 0,
            SmartSnapshot: smartSnapshot);
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
        var healthDetails = BuildHealthDetails(mapping, mediaClass, connectionType);
        var healthHighlights = BuildHealthHighlights(mapping, totalBytes, freeBytes);
        var warningSummary = BuildWarningSummary(mapping, isSystemVolume, freeBytes, totalBytes, mediaClass);
        var healthLabel = BuildHealthLabel(mapping, freeBytes, totalBytes);
        var healthScoreLabel = BuildHealthScoreLabel(mapping);
        var temperatureLabel = BuildTemperatureLabel(mapping);
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
            LayoutLabel: isSystemVolume ? "Sistem diski" : "Veri diski",
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
            SupportsRawAccess: RawVolumeAccessScope.IsAdministrator(),
            Brand: mapping.Brand,
            SerialNumber: mapping.SerialNumber,
            FirmwareVersion: mapping.FirmwareVersion,
            HealthScoreLabel: healthScoreLabel,
            TemperatureLabel: temperatureLabel,
            HealthHighlights: healthHighlights,
            HealthDetails: healthDetails,
            SmartAttributes: mapping.SmartSnapshot.Attributes);
    }

    private static IReadOnlyList<string> BuildHealthHighlights(DriveMapping mapping, long totalBytes, long freeBytes)
    {
        var items = new List<string>();

        if (!string.IsNullOrWhiteSpace(mapping.SmartReason))
        {
            items.Add($"SMART öngörüsü: {mapping.SmartReason}");
        }

        if (mapping.SmartSnapshot.HealthPercent is int healthPercent)
        {
            items.Add($"Tahmini kalan sağlık: %{healthPercent}");
        }

        if (mapping.SmartSnapshot.TemperatureCelsius is int temperature)
        {
            items.Add($"Sıcaklık: {temperature} °C");
        }

        if (mapping.SmartSnapshot.PowerOnHours is long powerOnHours)
        {
            items.Add($"Çalışma süresi: {powerOnHours:N0} saat");
        }

        if (mapping.SmartSnapshot.PowerCycleCount is long powerCycleCount)
        {
            items.Add($"Güç döngüsü: {powerCycleCount:N0}");
        }

        if (mapping.SmartSnapshot.HasReallocatedSectors)
        {
            items.Add("Yeniden eşlenen sektör kaydı var.");
        }

        if (mapping.SmartSnapshot.HasPendingSectors)
        {
            items.Add("Bekleyen veya düzeltilemeyen sektör sinyali var.");
        }

        if (mapping.SmartSnapshot.HasInterfaceErrors)
        {
            items.Add("Arabirim / CRC hata sayacı artmış görünüyor.");
        }

        if (totalBytes > 0 && (freeBytes * 100L / totalBytes) < 10)
        {
            items.Add("Boş alan yüzde 10'un altına düştü.");
        }

        if (items.Count == 0)
        {
            items.Add("Belirgin SMART veya depolama riski görülmedi.");
        }

        return items;
    }

    private static IReadOnlyList<DiskPropertyItem> BuildHealthDetails(DriveMapping mapping, string mediaClass, string connectionType)
    {
        return
        [
            new DiskPropertyItem("Marka", mapping.Brand),
            new DiskPropertyItem("Model", mapping.Model),
            new DiskPropertyItem("Seri no", Fallback(mapping.SerialNumber)),
            new DiskPropertyItem("Firmware", Fallback(mapping.FirmwareVersion)),
            new DiskPropertyItem("Bağlantı", connectionType),
            new DiskPropertyItem("Disk türü", mediaClass),
            new DiskPropertyItem("SMART durumu", mapping.SmartSnapshot.Attributes.Count > 0 ? "Okunabildi" : "Ayrıntılı veri yok"),
            new DiskPropertyItem("Depolama durumu", mapping.Status),
            new DiskPropertyItem("Operasyon durumu", mapping.OperationalStatus),
            new DiskPropertyItem("Mantıksal sektör", mapping.LogicalSectorSize > 0 ? $"{mapping.LogicalSectorSize} B" : "Bilinmiyor"),
            new DiskPropertyItem("Fiziksel sektör", mapping.PhysicalSectorSize > 0 ? $"{mapping.PhysicalSectorSize} B" : "Bilinmiyor"),
            new DiskPropertyItem("Dönüş hızı", mapping.SpindleSpeed > 0 ? $"{mapping.SpindleSpeed} RPM" : mediaClass.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD / NVMe" : "Bilinmiyor")
        ];
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

        if (mapping.SmartSnapshot.HasReallocatedSectors)
        {
            messages.Add("Yeniden eşlenen sektör sayacı sıfır değil.");
        }

        if (mapping.SmartSnapshot.HasPendingSectors)
        {
            messages.Add("Bekleyen veya düzeltilemeyen sektör sinyali var.");
        }

        if (mapping.SmartSnapshot.TemperatureCelsius is int temperature && temperature >= 55)
        {
            messages.Add($"Sıcaklık yüksek görünüyor ({temperature} °C).");
        }

        if (mapping.SmartSnapshot.HealthPercent is int healthPercent && healthPercent <= 20)
        {
            messages.Add($"Kalan sağlık düşük görünüyor (%{healthPercent}).");
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
            messages.Add("Çalışan sistem diski.");
        }

        if (mediaClass.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add("Harici bağlantı nedeniyle hız ve güç koşulları değişebilir.");
        }

        return messages.Count == 0 ? "Diskte belirgin bir risk sinyali görünmüyor." : string.Join(" ", messages);
    }

    private static string BuildHealthLabel(DriveMapping mapping, long freeBytes, long totalBytes)
    {
        if (mapping.PredictFailure ||
            mapping.SmartSnapshot.HasPendingSectors ||
            mapping.SmartSnapshot.HasReallocatedSectors ||
            string.Equals(mapping.Status, "Risk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mapping.Status, "Kritik", StringComparison.OrdinalIgnoreCase))
        {
            return "Riskli";
        }

        if ((mapping.SmartSnapshot.HealthPercent is int healthPercent && healthPercent <= 20) ||
            (mapping.SmartSnapshot.TemperatureCelsius is int temperature && temperature >= 55) ||
            (totalBytes > 0 && (freeBytes * 100L / totalBytes) < 10) ||
            mapping.SmartSnapshot.HasInterfaceErrors ||
            !string.Equals(mapping.Status, "Sağlıklı", StringComparison.OrdinalIgnoreCase) && !string.Equals(mapping.Status, "Bilinmiyor", StringComparison.OrdinalIgnoreCase))
        {
            return "Dikkat gerekiyor";
        }

        return "Sağlıklı";
    }

    private static string BuildHealthScoreLabel(DriveMapping mapping)
    {
        if (mapping.SmartSnapshot.HealthPercent is int healthPercent)
        {
            return $"%{healthPercent}";
        }

        return mapping.PredictFailure ? "Kritik" : "Bilinmiyor";
    }

    private static string BuildTemperatureLabel(DriveMapping mapping)
    {
        return mapping.SmartSnapshot.TemperatureCelsius is int temperature
            ? $"{temperature} °C"
            : "Bilinmiyor";
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

        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
            ? model
            : string.IsNullOrWhiteSpace(brand)
                ? model
                : $"{brand} {model}";
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
                $"SELECT Model, InterfaceType, MediaType, Index, Status, PNPDeviceID, SerialNumber, FirmwareRevision, BytesPerSector, Manufacturer FROM Win32_DiskDrive WHERE Index = {diskNumber}");

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
                        PnpDeviceId: disk["PNPDeviceID"]?.ToString() ?? string.Empty,
                        SerialNumber: disk["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
                        FirmwareRevision: disk["FirmwareRevision"]?.ToString()?.Trim() ?? string.Empty,
                        BytesPerSector: Convert.ToInt32(disk["BytesPerSector"] ?? 0),
                        Manufacturer: disk["Manufacturer"]?.ToString()?.Trim() ?? string.Empty);
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
                "SELECT DeviceId, FriendlyName, MediaType, HealthStatus, OperationalStatus, Size, SerialNumber, FirmwareVersion, Manufacturer, Model, PhysicalSectorSize, LogicalSectorSize, SpindleSpeed FROM MSFT_PhysicalDisk");

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    result.Add(new PhysicalDiskRecord(
                        DeviceId: Convert.ToInt32(disk["DeviceId"] ?? -1),
                        FriendlyName: disk["FriendlyName"]?.ToString() ?? string.Empty,
                        MediaTypeLabel: ResolvePhysicalMediaTypeLabel(Convert.ToInt32(disk["MediaType"] ?? 0)),
                        HealthLabel: ResolveStorageHealthLabel(Convert.ToInt32(disk["HealthStatus"] ?? 0)),
                        OperationalStatusLabel: ResolveOperationalStatusLabel(disk["OperationalStatus"]),
                        SizeBytes: Convert.ToInt64(disk["Size"] ?? 0L),
                        SerialNumber: disk["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
                        FirmwareVersion: disk["FirmwareVersion"]?.ToString()?.Trim() ?? string.Empty,
                        Manufacturer: disk["Manufacturer"]?.ToString()?.Trim() ?? string.Empty,
                        Model: disk["Model"]?.ToString()?.Trim() ?? string.Empty,
                        PhysicalSectorSize: Convert.ToInt32(disk["PhysicalSectorSize"] ?? 0),
                        LogicalSectorSize: Convert.ToInt32(disk["LogicalSectorSize"] ?? 0),
                        SpindleSpeed: Convert.ToInt32(disk["SpindleSpeed"] ?? 0)));
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

    private static IReadOnlyList<SmartVendorRecord> QuerySmartDataRecords()
    {
        return QuerySmartVendorRecords("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData");
    }

    private static IReadOnlyList<SmartVendorRecord> QuerySmartThresholdRecords()
    {
        return QuerySmartVendorRecords("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds");
    }

    private static IReadOnlyList<SmartVendorRecord> QuerySmartVendorRecords(string query)
    {
        try
        {
            var result = new List<SmartVendorRecord>();
            using var searcher = new ManagementObjectSearcher(SmartNamespace, query);

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    result.Add(new SmartVendorRecord(
                        InstanceName: item["InstanceName"]?.ToString() ?? string.Empty,
                        VendorSpecific: item["VendorSpecific"] as byte[] ?? Array.Empty<byte>()));
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
        var deviceIdMatch = physicalRecords.FirstOrDefault(record => record.DeviceId == win32Record.Index);
        if (deviceIdMatch is not null)
        {
            return deviceIdMatch;
        }

        var modelMatch = physicalRecords.FirstOrDefault(record =>
            !string.IsNullOrWhiteSpace(record.FriendlyName) &&
            (record.FriendlyName.Contains(win32Record.Model, StringComparison.OrdinalIgnoreCase) ||
             win32Record.Model.Contains(record.FriendlyName, StringComparison.OrdinalIgnoreCase) ||
             record.Model.Contains(win32Record.Model, StringComparison.OrdinalIgnoreCase)));

        if (modelMatch is not null)
        {
            return modelMatch;
        }

        return physicalRecords.FirstOrDefault(record =>
            record.SizeBytes > 0 &&
            targetSizeBytes > 0 &&
            Math.Abs(record.SizeBytes - targetSizeBytes) <= 1024L * 1024 * 1024);
    }

    private static SmartStatusRecord? MatchSmartStatusRecord(
        Win32DiskRecord win32Record,
        PhysicalDiskRecord? physicalRecord,
        IReadOnlyList<SmartStatusRecord> smartRecords)
    {
        return MatchSmartRecordCore(win32Record, physicalRecord, smartRecords, record => record.InstanceName);
    }

    private static SmartVendorRecord? MatchSmartVendorRecord(
        Win32DiskRecord win32Record,
        PhysicalDiskRecord? physicalRecord,
        IReadOnlyList<SmartVendorRecord> smartRecords)
    {
        return MatchSmartRecordCore(win32Record, physicalRecord, smartRecords, record => record.InstanceName);
    }

    private static TRecord? MatchSmartRecordCore<TRecord>(
        Win32DiskRecord win32Record,
        PhysicalDiskRecord? physicalRecord,
        IReadOnlyList<TRecord> records,
        Func<TRecord, string> instanceSelector)
    {
        var candidates = new[]
        {
            NormalizeForSearch(win32Record.Model),
            NormalizeForSearch(win32Record.PnpDeviceId),
            NormalizeForSearch(win32Record.SerialNumber),
            NormalizeForSearch(physicalRecord?.FriendlyName),
            NormalizeForSearch(physicalRecord?.SerialNumber),
            NormalizeForSearch(physicalRecord?.Model)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

        foreach (var record in records)
        {
            var instanceName = NormalizeForSearch(instanceSelector(record));
            if (candidates.Any(candidate => instanceName.Contains(candidate, StringComparison.Ordinal)))
            {
                return record;
            }
        }

        return default;
    }

    private static ParsedSmartSnapshot ParseSmartSnapshot(byte[]? vendorData, byte[]? thresholdData)
    {
        if (vendorData is null || vendorData.Length < 362)
        {
            return ParsedSmartSnapshot.Empty;
        }

        var thresholdMap = ParseSmartThresholds(thresholdData);
        var attributes = new List<DiskSmartAttribute>();
        var healthPercent = (int?)null;
        var temperature = (int?)null;
        var powerOnHours = (long?)null;
        var powerCycleCount = (long?)null;
        var hasReallocatedSectors = false;
        var hasPendingSectors = false;
        var hasInterfaceErrors = false;

        for (var offset = 2; offset + 11 < vendorData.Length; offset += 12)
        {
            var id = vendorData[offset];
            if (id == 0)
            {
                continue;
            }

            var current = vendorData[offset + 3];
            var worst = vendorData[offset + 4];
            var rawBytes = vendorData.AsSpan(offset + 5, 6).ToArray();
            var rawValue = DecodeRawValue(rawBytes);
            var threshold = thresholdMap.TryGetValue(id, out var thresholdValue) ? thresholdValue : (byte?)null;
            var name = ResolveSmartAttributeName(id);
            var statusLabel = BuildSmartAttributeStatus(id, current, rawValue, threshold);

            attributes.Add(new DiskSmartAttribute(
                Id: id,
                Name: name,
                CurrentValue: current.ToString(),
                WorstValue: worst.ToString(),
                ThresholdValue: threshold?.ToString() ?? "-",
                RawValue: FormatRawValue(id, rawValue),
                StatusLabel: statusLabel));

            switch (id)
            {
                case 5:
                    hasReallocatedSectors = rawValue > 0;
                    break;
                case 9:
                    powerOnHours = rawValue;
                    break;
                case 12:
                    powerCycleCount = rawValue;
                    break;
                case 190:
                case 194:
                    temperature ??= rawBytes[0];
                    break;
                case 197:
                case 198:
                    hasPendingSectors = hasPendingSectors || rawValue > 0;
                    break;
                case 199:
                    hasInterfaceErrors = rawValue > 0;
                    break;
                case 202:
                case 231:
                case 233:
                    if (current > 0)
                    {
                        healthPercent ??= Math.Clamp((int)current, 0, 100);
                    }
                    break;
            }
        }

        return new ParsedSmartSnapshot(
            Attributes: attributes.OrderBy(attribute => attribute.Id).ToArray(),
            HealthPercent: healthPercent,
            TemperatureCelsius: temperature,
            PowerOnHours: powerOnHours,
            PowerCycleCount: powerCycleCount,
            HasReallocatedSectors: hasReallocatedSectors,
            HasPendingSectors: hasPendingSectors,
            HasInterfaceErrors: hasInterfaceErrors);
    }

    private static Dictionary<byte, byte> ParseSmartThresholds(byte[]? thresholdData)
    {
        var result = new Dictionary<byte, byte>();
        if (thresholdData is null || thresholdData.Length < 362)
        {
            return result;
        }

        for (var offset = 2; offset + 1 < thresholdData.Length; offset += 12)
        {
            var id = thresholdData[offset];
            if (id == 0)
            {
                continue;
            }

            result[id] = thresholdData[offset + 1];
        }

        return result;
    }

    private static long DecodeRawValue(byte[] rawBytes)
    {
        long value = 0;
        for (var index = 0; index < rawBytes.Length; index++)
        {
            value |= (long)rawBytes[index] << (8 * index);
        }

        return value;
    }

    private static string FormatRawValue(int id, long rawValue)
    {
        return id switch
        {
            9 or 12 or 194 or 190 or 5 or 197 or 198 or 199 => rawValue.ToString("N0"),
            _ => rawValue > 0 ? rawValue.ToString("N0") : "0"
        };
    }

    private static string BuildSmartAttributeStatus(int id, byte current, long rawValue, byte? threshold)
    {
        if (threshold is not null && current <= threshold)
        {
            return "Eşik altında";
        }

        return id switch
        {
            5 or 197 or 198 when rawValue > 0 => "Risk sinyali",
            199 when rawValue > 0 => "Bağlantı hatası var",
            190 or 194 when rawValue >= 55 => "Sıcak",
            _ => "Normal"
        };
    }

    private static string ResolveSmartAttributeName(int id)
    {
        return id switch
        {
            1 => "Raw Read Error Rate",
            5 => "Reallocated Sector Count",
            9 => "Power-On Hours",
            12 => "Power Cycle Count",
            173 => "Wear Leveling Count",
            177 => "Wear Range Delta",
            179 => "Used Reserved Block Count",
            181 => "Program Fail Count",
            182 => "Erase Fail Count",
            183 => "Runtime Bad Block",
            184 => "End-to-End Error",
            187 => "Reported Uncorrectable Errors",
            188 => "Command Timeout",
            190 => "Temperature Airflow",
            194 => "Temperature",
            195 => "Hardware ECC Recovered",
            196 => "Reallocation Event Count",
            197 => "Current Pending Sector Count",
            198 => "Offline Uncorrectable Sector Count",
            199 => "UltraDMA CRC Error Count",
            202 => "Percent Lifetime Used",
            231 => "SSD Life Left",
            233 => "Media Wearout Indicator",
            241 => "Total LBAs Written",
            242 => "Total LBAs Read",
            _ => $"SMART {id}"
        };
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
            2 => "Dikkat gerekiyor",
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

    private static string ExtractBrand(string model, string? manufacturer, string? fallbackManufacturer)
    {
        var direct = FirstNonEmpty(manufacturer, fallbackManufacturer);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return "Bilinmeyen marka";
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

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Bilinmeyen marka";
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

    private static string Fallback(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Bilinmiyor" : value;
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
            0 => "Arıza öngörüsü sinyali yok",
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
        string PnpDeviceId,
        string SerialNumber,
        string FirmwareRevision,
        int BytesPerSector,
        string Manufacturer);

    private sealed record StorageDiskRecord(
        int Number,
        string FriendlyName,
        int BusTypeCode,
        string HealthLabel,
        string OperationalStatusLabel,
        bool IsBoot,
        bool IsSystem);

    private sealed record PhysicalDiskRecord(
        int DeviceId,
        string FriendlyName,
        string MediaTypeLabel,
        string HealthLabel,
        string OperationalStatusLabel,
        long SizeBytes,
        string SerialNumber,
        string FirmwareVersion,
        string Manufacturer,
        string Model,
        int PhysicalSectorSize,
        int LogicalSectorSize,
        int SpindleSpeed);

    private sealed record SmartStatusRecord(
        string InstanceName,
        bool PredictFailure,
        int ReasonCode)
    {
        public string ReasonLabel => ResolveSmartReason(ReasonCode);
    }

    private sealed record SmartVendorRecord(
        string InstanceName,
        byte[] VendorSpecific);

    private sealed record ParsedSmartSnapshot(
        IReadOnlyList<DiskSmartAttribute> Attributes,
        int? HealthPercent,
        int? TemperatureCelsius,
        long? PowerOnHours,
        long? PowerCycleCount,
        bool HasReallocatedSectors,
        bool HasPendingSectors,
        bool HasInterfaceErrors)
    {
        public static ParsedSmartSnapshot Empty { get; } = new([], null, null, null, null, false, false, false);
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
        string? SmartReason,
        string SerialNumber,
        string FirmwareVersion,
        int LogicalSectorSize,
        int PhysicalSectorSize,
        int SpindleSpeed,
        ParsedSmartSnapshot SmartSnapshot);
}

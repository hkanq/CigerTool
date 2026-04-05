using System.Management;
using System.Runtime.Versioning;
using CigerTool.Application.Contracts;
using CigerTool.Domain.Enums;
using CigerTool.Usb.Models;

namespace CigerTool.Usb.Services;

[SupportedOSPlatform("windows")]
internal sealed class UsbDeviceDiscoveryService(IOperationLogService operationLogService)
{
    private const string StorageNamespace = @"ROOT\Microsoft\Windows\Storage";

    public IReadOnlyList<UsbPhysicalDeviceInfo> GetUsbDevices()
    {
        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var legacyRecords = TryQueryLegacyDiskRecords(systemDrive);
        var devices = new List<UsbPhysicalDeviceInfo>();

        devices.AddRange(QueryStorageDevices(systemDrive, legacyRecords));

        foreach (var legacyRecord in legacyRecords.Values)
        {
            if (!legacyRecord.IsUsbCandidate)
            {
                continue;
            }

            if (devices.Any(device => string.Equals(device.PhysicalPath, legacyRecord.PhysicalPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            devices.Add(new UsbPhysicalDeviceInfo(
                Id: legacyRecord.PhysicalPath,
                PhysicalPath: legacyRecord.PhysicalPath,
                Model: legacyRecord.Model,
                SizeBytes: legacyRecord.SizeBytes,
                MountedVolumes: legacyRecord.MountedVolumes,
                IsSystemDisk: legacyRecord.IsSystemDisk));
        }

        return Order(devices);
    }

    private Dictionary<int, LegacyDiskRecord> TryQueryLegacyDiskRecords(string systemDrive)
    {
        try
        {
            return QueryLegacyDiskRecords(systemDrive);
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "USB Oluşturma",
                "Yedek USB aygıt sorgusu tamamlanamadı.",
                "usb.devices.legacy.failure",
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });

            return new Dictionary<int, LegacyDiskRecord>();
        }
    }

    private IReadOnlyList<UsbPhysicalDeviceInfo> QueryStorageDevices(
        string systemDrive,
        IReadOnlyDictionary<int, LegacyDiskRecord> legacyRecords)
    {
        var devices = new List<UsbPhysicalDeviceInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                StorageNamespace,
                "SELECT Number, FriendlyName, Model, Path, Size, BusType, IsBoot, IsSystem FROM MSFT_Disk");

            foreach (ManagementObject disk in searcher.Get())
            {
                using (disk)
                {
                    try
                    {
                        var diskNumber = TryGetInt(disk["Number"]);
                        if (diskNumber < 0)
                        {
                            continue;
                        }

                        legacyRecords.TryGetValue(diskNumber, out var legacyRecord);

                        var friendlyName = FirstNonEmpty(
                            disk["FriendlyName"]?.ToString(),
                            disk["Model"]?.ToString(),
                            legacyRecord?.Model,
                            $"USB Disk {diskNumber}");

                        var path = disk["Path"]?.ToString()?.Trim();
                        var busType = TryGetInt(disk["BusType"]);

                        if (!IsStorageUsbCandidate(busType, friendlyName, path, legacyRecord))
                        {
                            continue;
                        }

                        var sizeBytes = TryGetLong(disk["Size"]);
                        if (sizeBytes <= 0 && legacyRecord is not null)
                        {
                            sizeBytes = legacyRecord.SizeBytes;
                        }

                        var mountedVolumes = GetMountedVolumesByDiskNumber(diskNumber);
                        if (mountedVolumes.Count == 0 && legacyRecord is not null)
                        {
                            mountedVolumes = legacyRecord.MountedVolumes;
                        }

                        var physicalPath = $@"\\.\PhysicalDrive{diskNumber}";
                        var isSystemDisk = TryGetBool(disk["IsSystem"]) ||
                                           TryGetBool(disk["IsBoot"]) ||
                                           mountedVolumes.Any(volume => string.Equals(volume, systemDrive, StringComparison.OrdinalIgnoreCase)) ||
                                           legacyRecord?.IsSystemDisk == true;

                        devices.Add(new UsbPhysicalDeviceInfo(
                            Id: physicalPath,
                            PhysicalPath: physicalPath,
                            Model: friendlyName,
                            SizeBytes: sizeBytes,
                            MountedVolumes: mountedVolumes,
                            IsSystemDisk: isSystemDisk));
                    }
                    catch (Exception ex)
                    {
                        operationLogService.Record(
                            OperationSeverity.Warning,
                            "USB Oluşturma",
                            "Bir USB aygıt girdisi atlandı.",
                            "usb.devices.storage.item.failure",
                            new Dictionary<string, string>
                            {
                                ["error"] = ex.Message
                            });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "USB Oluşturma",
                "Gelişmiş USB aygıt taraması tamamlanamadı, yedek taramaya geçiliyor.",
                "usb.devices.storage.failure",
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
        }

        return devices;
    }

    private Dictionary<int, LegacyDiskRecord> QueryLegacyDiskRecords(string systemDrive)
    {
        var records = new Dictionary<int, LegacyDiskRecord>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, Caption, Size, InterfaceType, MediaType, PNPDeviceID, Index FROM Win32_DiskDrive");

        foreach (ManagementObject disk in searcher.Get())
        {
            using (disk)
            {
                try
                {
                    var index = TryGetInt(disk["Index"]);
                    if (index < 0)
                    {
                        continue;
                    }

                    var deviceId = disk["DeviceID"]?.ToString();
                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        continue;
                    }

                    var model = FirstNonEmpty(
                        disk["Model"]?.ToString(),
                        disk["Caption"]?.ToString(),
                        $"USB Disk {index}");

                    var mediaType = disk["MediaType"]?.ToString() ?? string.Empty;
                    var interfaceType = disk["InterfaceType"]?.ToString() ?? string.Empty;
                    var pnpDeviceId = disk["PNPDeviceID"]?.ToString() ?? string.Empty;
                    var sizeBytes = TryGetLong(disk["Size"]);
                    var physicalPath = $@"\\.\PhysicalDrive{index}";
                    var mountedVolumes = GetMountedVolumesByDiskNumber(index);
                    var isSystemDisk = mountedVolumes.Any(volume => string.Equals(volume, systemDrive, StringComparison.OrdinalIgnoreCase));

                    records[index] = new LegacyDiskRecord(
                        PhysicalPath: physicalPath,
                        Model: model,
                        SizeBytes: sizeBytes,
                        MountedVolumes: mountedVolumes,
                        IsSystemDisk: isSystemDisk,
                        IsUsbCandidate: IsLegacyUsbCandidate(interfaceType, mediaType, pnpDeviceId, model));
                }
                catch (Exception ex)
                {
                    operationLogService.Record(
                        OperationSeverity.Warning,
                        "USB Oluşturma",
                        "Eski yöntemle bir USB aygıt girdisi okunamadı.",
                        "usb.devices.legacy.item.failure",
                        new Dictionary<string, string>
                        {
                            ["error"] = ex.Message
                        });
                }
            }
        }

        return records;
    }

    private static IReadOnlyList<string> GetMountedVolumesByDiskNumber(int diskNumber)
    {
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var partitionSearcher = new ManagementObjectSearcher(
                StorageNamespace,
                $"SELECT DriveLetter, AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");

            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using (partition)
                {
                    var driveLetter = partition["DriveLetter"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(driveLetter))
                    {
                        volumes.Add(driveLetter.TrimEnd(':') + ":");
                    }

                    if (partition["AccessPaths"] is string[] accessPaths)
                    {
                        foreach (var accessPath in accessPaths)
                        {
                            var normalized = NormalizeVolumeLabel(accessPath);
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                volumes.Add(normalized);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return volumes.OrderBy(volume => volume).ToArray();
    }

    private static IReadOnlyList<UsbPhysicalDeviceInfo> Order(IEnumerable<UsbPhysicalDeviceInfo> devices)
    {
        return devices
            .Where(device => !string.IsNullOrWhiteSpace(device.PhysicalPath))
            .DistinctBy(device => device.PhysicalPath)
            .OrderBy(device => device.IsSystemDisk)
            .ThenBy(device => device.Model)
            .ThenBy(device => device.SizeBytes)
            .ToArray();
    }

    private static bool IsStorageUsbCandidate(
        int busType,
        string friendlyName,
        string? path,
        LegacyDiskRecord? legacyRecord)
    {
        return busType == 7 ||
               legacyRecord?.IsUsbCandidate == true ||
               ContainsIgnoreCase(friendlyName, "usb") ||
               ContainsIgnoreCase(friendlyName, "removable") ||
               ContainsIgnoreCase(friendlyName, "portable") ||
               ContainsIgnoreCase(friendlyName, "external") ||
               ContainsIgnoreCase(friendlyName, "flash") ||
               ContainsIgnoreCase(friendlyName, "sd") ||
               ContainsIgnoreCase(path, "usb") ||
               ContainsIgnoreCase(path, "removable");
    }

    private static bool IsLegacyUsbCandidate(
        string interfaceType,
        string mediaType,
        string pnpDeviceId,
        string model)
    {
        return interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("External", StringComparison.OrdinalIgnoreCase) ||
               pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
               pnpDeviceId.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
               ContainsIgnoreCase(model, "usb") ||
               ContainsIgnoreCase(model, "flash") ||
               ContainsIgnoreCase(model, "portable") ||
               ContainsIgnoreCase(model, "external") ||
               ContainsIgnoreCase(model, "removable") ||
               ContainsIgnoreCase(model, "card");
    }

    private static bool ContainsIgnoreCase(string? source, string value)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string? NormalizeVolumeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimEnd('\\');
        if (trimmed.Length == 1)
        {
            return trimmed + ":";
        }

        if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            return trimmed[..2];
        }

        return trimmed;
    }

    private static int TryGetInt(object? value)
    {
        try
        {
            return Convert.ToInt32(value ?? -1);
        }
        catch
        {
            return -1;
        }
    }

    private static long TryGetLong(object? value)
    {
        try
        {
            return Convert.ToInt64(value ?? 0L);
        }
        catch
        {
            return 0L;
        }
    }

    private static bool TryGetBool(object? value)
    {
        try
        {
            return Convert.ToBoolean(value ?? false);
        }
        catch
        {
            return false;
        }
    }

    private sealed record LegacyDiskRecord(
        string PhysicalPath,
        string Model,
        long SizeBytes,
        IReadOnlyList<string> MountedVolumes,
        bool IsSystemDisk,
        bool IsUsbCandidate);
}

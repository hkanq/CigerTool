using System.Runtime.Versioning;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Cloning;
using Xunit;

namespace CigerTool.Infrastructure.Tests.Basics;

[SupportedOSPlatform("windows")]
public sealed class CloneAnalysisWorkflowServiceTests
{
    [Fact]
    public void Analyze_RawClone_ShouldBlock_WhenTargetIsSmallerThanSource()
    {
        var service = new CloneWorkflowService(
            new FakeDiskInventoryService(
            [
                CreateDisk("SRC", "C:", 500, 300, true, "NTFS"),
                CreateDisk("DST", "E:", 300, 50, false, "NTFS")
            ]),
            new FakeEnvironmentProfileService(isWinPe: true),
            new FakeOperationLogService());

        var result = service.Analyze(new CloneWorkflowRequest(CloneMode.Raw, "SRC", "DST"));

        Assert.False(result.CanProceed);
        Assert.Equal(CloneSuitabilityStatus.Blocked, result.Status);
    }

    [Fact]
    public void Analyze_SmartClone_ShouldAllowSmallerTarget_WhenUsedSpaceFits()
    {
        var service = new CloneWorkflowService(
            new FakeDiskInventoryService(
            [
                CreateDisk("SRC", "C:", 500, 120, true, "NTFS"),
                CreateDisk("DST", "E:", 250, 20, false, "NTFS")
            ]),
            new FakeEnvironmentProfileService(isWinPe: false),
            new FakeOperationLogService());

        var result = service.Analyze(new CloneWorkflowRequest(CloneMode.Smart, "SRC", "DST"));

        Assert.True(result.CanProceed);
        Assert.Equal(CloneSuitabilityStatus.Caution, result.Status);
    }

    private static DiskSummary CreateDisk(string id, string driveLetter, long totalGb, long usedGb, bool isSystem, string fileSystem)
    {
        var totalBytes = totalGb * 1024L * 1024L * 1024L;
        var usedBytes = usedGb * 1024L * 1024L * 1024L;
        var freeBytes = totalBytes - usedBytes;

        return new DiskSummary(
            Id: id,
            Name: driveLetter,
            DriveLetter: driveLetter,
            FileSystem: fileSystem,
            ConnectionType: "Dahili / sabit",
            CapacityLabel: $"{totalGb} GB",
            UsedLabel: $"{usedGb} GB",
            FreeLabel: $"{Math.Max(0, totalGb - usedGb)} GB",
            LayoutLabel: isSystem ? "Sistem sürücüsü" : "Veri sürücüsü",
            HealthLabel: "Hazır",
            TotalBytes: totalBytes,
            UsedBytes: usedBytes,
            FreeBytes: freeBytes,
            IsSystemVolume: isSystem,
            IsReady: true,
            DeviceModel: "Test Disk",
            BusType: "SATA",
            MediaType: "Sabit",
            IdentityLabel: $"Disk {id}",
            WarningSummary: "Hazır",
            UsagePercent: (int)Math.Round((double)usedBytes / totalBytes * 100d),
            IsRemovable: false,
            SupportsRawAccess: true,
            Brand: "Test",
            SerialNumber: "TEST123",
            FirmwareVersion: "1.0",
            HealthScoreLabel: "%100",
            TemperatureLabel: "30 °C",
            HealthHighlights: ["Belirgin bir risk sinyali yok."],
            HealthDetails: [new DiskPropertyItem("Marka", "Test")],
            SmartAttributes: []);
    }

    private sealed class FakeDiskInventoryService(IReadOnlyList<DiskSummary> disks) : IDiskInventoryService
    {
        public DiskWorkspaceSnapshot GetSnapshot()
        {
            return new DiskWorkspaceSnapshot(
                Heading: "Test",
                Summary: "Test",
                System: new SystemSummary("Machine", "OS", "x64", ".NET", "User", "C:", "1h"),
                Metrics: [],
                Disks: disks,
                Notes: []);
        }

        public IReadOnlyList<DiskSummary> GetCurrentDisks() => disks;

        public DiskSummary? FindById(string id) => disks.FirstOrDefault(disk => disk.Id == id);
    }

    private sealed class FakeEnvironmentProfileService(bool isWinPe) : IEnvironmentProfileService
    {
        public AppEnvironmentProfile GetCurrentProfile()
        {
            return new AppEnvironmentProfile(
                ProfileName: isWinPe ? "Windows PE" : "Windows Desktop",
                IsWinPe: isWinPe,
                SupportsUsbCreation: true,
                SupportsLiveCloning: !isWinPe,
                Summary: "Test");
        }
    }

    private sealed class FakeOperationLogService : IOperationLogService
    {
        public LogsWorkspaceSnapshot GetSnapshot()
        {
            return new LogsWorkspaceSnapshot(
                Heading: "Logs",
                Summary: "Logs",
                Metrics: [],
                TextLogPath: "cigertool.log",
                StructuredLogPath: "cigertool.jsonl",
                Entries: []);
        }

        public void Record(
            OperationSeverity severity,
            string area,
            string message,
            string? eventId = null,
            IReadOnlyDictionary<string, string>? details = null)
        {
        }
    }
}

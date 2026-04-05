using CigerTool.Domain.Enums;

namespace CigerTool.Application.Models;

public sealed record DiskSurfaceScanResult(
    string DiskLabel,
    ExecutionState State,
    string StatusLabel,
    string Summary,
    string ScannedBytesLabel,
    string BadRangeCountLabel,
    IReadOnlyList<string> Findings);

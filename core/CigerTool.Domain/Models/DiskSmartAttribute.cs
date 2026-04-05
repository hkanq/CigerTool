namespace CigerTool.Domain.Models;

public sealed record DiskSmartAttribute(
    int Id,
    string Name,
    string CurrentValue,
    string WorstValue,
    string ThresholdValue,
    string RawValue,
    string StatusLabel);

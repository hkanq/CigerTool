namespace CigerTool.Application.Models;

public sealed record DiskBenchmarkProfileOption(
    string Id,
    string Title,
    string Description,
    long TestFileSizeBytes,
    int RandomOperations);

using CigerTool.Domain.Enums;

namespace CigerTool.Application.Models;

public sealed record DiskBenchmarkResult(
    string DiskLabel,
    string ProfileLabel,
    ExecutionState State,
    string StatusLabel,
    string Summary,
    string TestFileSizeLabel,
    string SequentialReadLabel,
    string SequentialWriteLabel,
    string RandomReadLabel,
    string RandomWriteLabel,
    string RandomReadIopsLabel,
    string RandomWriteIopsLabel,
    IReadOnlyList<string> Notes);

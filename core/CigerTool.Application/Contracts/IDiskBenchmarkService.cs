using CigerTool.Application.Models;
using CigerTool.Domain.Models;

namespace CigerTool.Application.Contracts;

public interface IDiskBenchmarkService
{
    Task<DiskBenchmarkResult> RunAsync(
        DiskSummary disk,
        DiskBenchmarkProfileOption profile,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}

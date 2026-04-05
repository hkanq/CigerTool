using CigerTool.Application.Models;
using CigerTool.Domain.Models;

namespace CigerTool.Application.Contracts;

public interface IDiskSurfaceScanService
{
    Task<DiskSurfaceScanResult> RunAsync(
        DiskSummary disk,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}

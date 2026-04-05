using CigerTool.Application.Models;

namespace CigerTool.Application.Contracts;

public interface IWindowsDeploymentService
{
    Task<IReadOnlyList<WindowsImageEditionOption>> GetAvailableEditionsAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<UsbCreatorOperationResult> DeployToDiskAsync(
        string sourcePath,
        int targetDiskNumber,
        int imageIndex,
        bool portableMode,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}

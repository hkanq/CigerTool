using CigerTool.Application.Models;

namespace CigerTool.Application.Contracts;

public interface IUsbCreationService
{
    UsbCreatorWorkspaceSnapshot GetSnapshot();

    Task<UsbCreatorOperationResult> RefreshReleaseInfoAsync(CancellationToken cancellationToken = default);

    Task<UsbCreatorOperationResult> RefreshUsbDevicesAsync(CancellationToken cancellationToken = default);

    UsbCreatorOperationResult SetManualImagePath(string imagePath);

    UsbCreatorOperationResult ClearManualImageSelection();

    Task<UsbCreatorOperationResult> DownloadImageAsync(
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);

    Task<UsbCreatorOperationResult> VerifyPreparedImageAsync(
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);

    Task<UsbCreatorOperationResult> WriteImageAsync(
        string? usbDeviceId,
        bool confirmedByUser,
        IProgress<OperationProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}

using CigerTool.App.ViewModels;
using CigerTool.App.ViewModels.Pages;
using CigerTool.Application.Contracts;
using CigerTool.Infrastructure.Backup;
using CigerTool.Infrastructure.Cloning;
using CigerTool.Infrastructure.Configuration;
using CigerTool.Infrastructure.Dashboard;
using CigerTool.Infrastructure.Diagnostics;
using CigerTool.Infrastructure.Disks;
using CigerTool.Infrastructure.Environment;
using CigerTool.Infrastructure.Logging;
using CigerTool.Infrastructure.Storage;
using CigerTool.Infrastructure.Tools;
using CigerTool.Usb.Contracts;
using CigerTool.Usb.Services;

namespace CigerTool.App.Composition;

public static class AppBootstrapper
{
    public static ShellViewModel CreateShellViewModel()
    {
        IEnvironmentProfileService environmentProfileService = new DesktopEnvironmentProfileService();
        IAppPathService appPathService = new RuntimeAppPathService(environmentProfileService);
        ISettingsService settingsService = new JsonSettingsService(appPathService);
        IOperationLogService operationLogService = new FileOperationLogService(appPathService);
        IDiskInventoryService diskInventoryService = new RuntimeDiskInventoryService(operationLogService);
        IDiskBenchmarkService diskBenchmarkService = new DiskBenchmarkService(operationLogService);
        IDiskSurfaceScanService diskSurfaceScanService = new DiskSurfaceScanService(operationLogService);
        IBackupWorkflowService backupWorkflowService = new BackupWorkflowService(
            diskInventoryService,
            environmentProfileService,
            operationLogService);
        ICloneWorkflowService cloneWorkflowService = new CloneWorkflowService(
            diskInventoryService,
            environmentProfileService,
            operationLogService);
        IToolCatalogService toolCatalogService = new JsonToolCatalogService(
            operationLogService,
            environmentProfileService,
            appPathService);
        IReleaseSourceResolver releaseSourceResolver = new ReleaseSourceResolver(settingsService, operationLogService, appPathService);
        IUsbCreationService usbCreationService = new UsbCreationService(settingsService, releaseSourceResolver, operationLogService, appPathService);
        IWindowsDeploymentService windowsDeploymentService = new WindowsDeploymentService(operationLogService);
        IStartupDiagnosticsService startupDiagnosticsService = new StartupDiagnosticsService(
            appPathService,
            settingsService,
            environmentProfileService,
            toolCatalogService,
            operationLogService);
        IDashboardService dashboardService = new OperationalDashboardService(
            environmentProfileService,
            startupDiagnosticsService,
            diskInventoryService,
            cloneWorkflowService,
            backupWorkflowService,
            usbCreationService,
            operationLogService);
        var settings = settingsService.GetSettings();
        var environmentProfile = environmentProfileService.GetCurrentProfile();
        var diagnostics = startupDiagnosticsService.Run();

        operationLogService.Record(
            CigerTool.Domain.Enums.OperationSeverity.Info,
            "Uygulama",
            "Ana uygulama kabuğu oluşturuldu.",
            "app.shell.ready");

        return new ShellViewModel(
            settings.ProductName,
            environmentProfile,
            diagnostics,
            () => new DashboardPageViewModel(dashboardService),
            () => new CloningPageViewModel(cloneWorkflowService, environmentProfileService, operationLogService),
            () => new BackupImagePageViewModel(backupWorkflowService, operationLogService),
            () => new DisksPageViewModel(diskInventoryService, diskBenchmarkService, diskSurfaceScanService),
            () => new UsbCreatorPageViewModel(usbCreationService),
            () => new ToolsPageViewModel(usbCreationService, diskInventoryService, windowsDeploymentService),
            () => new LogsPageViewModel(operationLogService),
            () => new SettingsPageViewModel(settingsService));
    }
}

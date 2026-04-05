using System.Windows.Input;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Models;

namespace CigerTool.App.ViewModels.Pages;

public sealed class ToolsPageViewModel : ViewModelBase
{
    private readonly IToolCatalogService _toolCatalogService;
    private readonly IToolLaunchService _toolLaunchService;
    private ToolsWorkspaceSnapshot _snapshot;
    private string _launchStatus;

    public ToolsPageViewModel(
        IToolCatalogService toolCatalogService,
        IToolLaunchService toolLaunchService)
    {
        _toolCatalogService = toolCatalogService;
        _toolLaunchService = toolLaunchService;
        _snapshot = toolCatalogService.GetSnapshot();
        _launchStatus = "İsteğe bağlı yardımcı açılışları burada raporlanır.";
        RefreshCommand = new RelayCommand(_ => Refresh());

        LaunchToolCommand = new RelayCommand(parameter =>
        {
            if (parameter is not ToolDefinition tool)
            {
                return;
            }

            var result = _toolLaunchService.Launch(tool);
            LaunchStatus = result.Message;
        });
    }

    public ToolsWorkspaceSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public ICommand LaunchToolCommand { get; }

    public ICommand RefreshCommand { get; }

    public string LaunchStatus
    {
        get => _launchStatus;
        private set => SetProperty(ref _launchStatus, value);
    }

    private void Refresh()
    {
        try
        {
            Snapshot = _toolCatalogService.GetSnapshot();
            LaunchStatus = "Ek özellikler listesi yenilendi.";
        }
        catch (Exception ex)
        {
            LaunchStatus = $"Ek özellikler listesi okunamadı: {ex.Message}";
        }
    }
}

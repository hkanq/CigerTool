using System.Collections.ObjectModel;
using System.Windows.Input;
using CigerTool.App.Models;
using CigerTool.App.ViewModels.Pages;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;

namespace CigerTool.App.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly Dictionary<NavigationTarget, Func<object>> _pageFactories;
    private readonly Dictionary<NavigationTarget, object> _pageCache = new();
    private object _currentPage;
    private string _currentPageTitle;
    private string _currentPageSubtitle;

    public ShellViewModel(
        string productName,
        AppEnvironmentProfile environmentProfile,
        StartupDiagnosticsSnapshot diagnostics,
        Func<DashboardPageViewModel> dashboardFactory,
        Func<CloningPageViewModel> cloningFactory,
        Func<BackupImagePageViewModel> backupImageFactory,
        Func<DisksPageViewModel> disksFactory,
        Func<UsbCreatorPageViewModel> usbCreatorFactory,
        Func<ToolsPageViewModel> toolsFactory,
        Func<LogsPageViewModel> logsFactory,
        Func<SettingsPageViewModel> settingsFactory)
    {
        EnvironmentProfile = environmentProfile;
        ProductName = productName;
        ReadinessLabel = diagnostics.Severity switch
        {
            OperationSeverity.Error => "Dikkat gerekiyor",
            OperationSeverity.Warning => "Gözden geçirilecek noktalar var",
            _ => "Kullanıma hazır"
        };
        EditionLabel = environmentProfile.IsWinPe ? "CigerTool OS" : "Windows Uygulaması";
        ProductTagline = "Klonlama, yedekleme, imaj alma ve USB hazırlığı tek yerde.";
        WindowSubtitle = environmentProfile.IsWinPe
            ? "Bakım ve kurtarma işlemleri için optimize edilmiş çalışma alanı"
            : "Günlük kullanım ve servis işlemleri için hazır çalışma alanı";

        _pageFactories = new Dictionary<NavigationTarget, Func<object>>
        {
            [NavigationTarget.Dashboard] = dashboardFactory,
            [NavigationTarget.Cloning] = cloningFactory,
            [NavigationTarget.BackupImage] = backupImageFactory,
            [NavigationTarget.Disks] = disksFactory,
            [NavigationTarget.UsbCreator] = usbCreatorFactory,
            [NavigationTarget.Extras] = toolsFactory,
            [NavigationTarget.Logs] = logsFactory,
            [NavigationTarget.Settings] = settingsFactory
        };

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new(NavigationTarget.Dashboard, "Ana Sayfa", "Durum, öneriler ve son işlemler", "\uE80F"),
            new(NavigationTarget.Cloning, "Klonlama", "Bir diski başka bir diske taşıyın", "\uE7C5"),
            new(NavigationTarget.BackupImage, "Yedekleme ve İmaj", "İmaj alın, geri yükleyin ve dönüştürün", "\uE823"),
            new(NavigationTarget.Disks, "Diskler ve Sağlık", "Bağlı diskleri ve sağlık durumlarını görün", "\uEDA2"),
            new(NavigationTarget.UsbCreator, "USB Ortamı Oluştur", "Kurulum ve canlı ortam USB'lerini hazırlayın", "\uE88E"),
            new(NavigationTarget.Extras, "Ek Özellikler", "Gelişmiş tanılama, benchmark ve yardımcılar", "\uE9CE"),
            new(NavigationTarget.Logs, "Günlükler", "İşlem kayıtlarını inceleyin", "\uE9D2"),
            new(NavigationTarget.Settings, "Ayarlar", "Uygulama tercihleri ve destek bilgileri", "\uE713")
        };

        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is NavigationItemViewModel item)
            {
                Navigate(item.Target);
            }
        });

        _currentPage = new object();
        _currentPageTitle = "Ana Sayfa";
        _currentPageSubtitle = "Genel durum ve önerilen sonraki adımlar";

        Navigate(NavigationTarget.Dashboard);
    }

    public string ProductName { get; }

    public string ReadinessLabel { get; }

    public string EditionLabel { get; }

    public string ProductTagline { get; }

    public string WindowSubtitle { get; }

    public AppEnvironmentProfile EnvironmentProfile { get; }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public ICommand NavigateCommand { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        private set => SetProperty(ref _currentPageTitle, value);
    }

    public string CurrentPageSubtitle
    {
        get => _currentPageSubtitle;
        private set => SetProperty(ref _currentPageSubtitle, value);
    }

    private void Navigate(NavigationTarget target)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = item.Target == target;
        }

        CurrentPage = GetOrCreatePage(target);

        var selected = NavigationItems.First(item => item.Target == target);
        CurrentPageTitle = selected.Title;
        CurrentPageSubtitle = selected.Subtitle;
    }

    private object GetOrCreatePage(NavigationTarget target)
    {
        if (_pageCache.TryGetValue(target, out var page))
        {
            return page;
        }

        page = _pageFactories[target]();
        _pageCache[target] = page;
        return page;
    }
}

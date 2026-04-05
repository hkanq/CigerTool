using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;

namespace CigerTool.Infrastructure.Diagnostics;

public sealed class StartupDiagnosticsService(
    IAppPathService appPathService,
    ISettingsService settingsService,
    IEnvironmentProfileService environmentProfileService,
    IToolCatalogService toolCatalogService,
    IOperationLogService operationLogService) : IStartupDiagnosticsService
{
    private StartupDiagnosticsSnapshot? _cached;

    public StartupDiagnosticsSnapshot Run()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var paths = appPathService.GetPaths();
        var settings = settingsService.GetSettings();
        var profile = environmentProfileService.GetCurrentProfile();
        var checks = new List<StartupCheckItem>
        {
            new(
                "Çalışma düzeni",
                profile.ProfileName,
                profile.Summary,
                OperationSeverity.Info),
            new(
                "Veri konumu",
                paths.StorageModeLabel,
                $"Uygulama verileri şurada tutulur: {paths.DataRoot}",
                OperationSeverity.Info),
            BuildDirectoryCheck("Günlük konumu", paths.LogDirectory),
            BuildDirectoryCheck("Önbellek konumu", paths.CacheDirectory),
            BuildDirectoryCheck("İndirme konumu", paths.DownloadsDirectory)
        };

        var overrideSettingsPath = Path.Combine(paths.ConfigDirectory, "appsettings.override.json");
        checks.Add(new StartupCheckItem(
            "Ek ayarlar",
            File.Exists(overrideSettingsPath) ? "Uygulanıyor" : "İsteğe bağlı",
            File.Exists(overrideSettingsPath)
                ? overrideSettingsPath
                : "İsteğe bağlı ayarlar için işletim sistemindeki yapılandırma klasörü kullanılabilir.",
            OperationSeverity.Info));

        checks.Add(new StartupCheckItem(
            "Yayın kaynağı",
            string.IsNullOrWhiteSpace(settings.DefaultManifestUrl) ? "Elle seçim de hazır" : "Hazır",
            string.IsNullOrWhiteSpace(settings.DefaultManifestUrl)
                ? "Varsayılan yayın adresi tanımlı değil. İsterseniz USB oluşturma bölümünde dosya seçerek devam edebilirsiniz."
                : settings.DefaultManifestUrl,
            OperationSeverity.Info));

        checks.Add(BuildExecutableCheck(
            "ISO hazırlama bileşenleri",
            [
                Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Windows), "System32", "dism.exe"),
                Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.Windows), "System32", "bootsect.exe")
            ],
            "Standart ISO hazırlama akışı için gereken Windows bileşenleri denetlenir."));

        var toolSnapshot = toolCatalogService.GetSnapshot();
        checks.Add(new StartupCheckItem(
            "Yardımcı araç desteği",
            toolSnapshot.Tools.Any(tool => tool.CanLaunch) ? "Hazır" : "İsteğe bağlı",
            "Yardımcı araçlar isteğe bağlıdır; ana disk işlemleri uygulama içinde çalışır.",
            OperationSeverity.Info));

        var highestSeverity = checks.Any(check => check.Severity == OperationSeverity.Error)
            ? OperationSeverity.Error
            : checks.Any(check => check.Severity == OperationSeverity.Warning)
                ? OperationSeverity.Warning
                : OperationSeverity.Info;

        var readinessLabel = highestSeverity switch
        {
            OperationSeverity.Error => "Müdahale gerekiyor",
            OperationSeverity.Warning => "Gözden geçirin",
            _ => "Hazır"
        };

        var summary = highestSeverity switch
        {
            OperationSeverity.Error => "Başlangıç denetimi kritik bir sorun buldu.",
            OperationSeverity.Warning => "Başlangıç denetimi tamamlandı; başlamadan önce birkaç noktayı gözden geçirin.",
            _ => "Başlangıç denetimi temiz tamamlandı."
        };

        operationLogService.Record(
            highestSeverity == OperationSeverity.Error ? OperationSeverity.Error : OperationSeverity.Info,
            "Başlangıç",
            summary,
            "app.selfcheck",
            new Dictionary<string, string>
            {
                ["storageMode"] = paths.StorageModeLabel,
                ["dataRoot"] = paths.DataRoot,
                ["readiness"] = readinessLabel
            });

        _cached = new StartupDiagnosticsSnapshot(
            Heading: "Başlangıç denetimi",
            Summary: summary,
            ReadinessLabel: readinessLabel,
            Severity: highestSeverity,
            Paths: paths,
            Checks: checks);

        return _cached;
    }

    private static StartupCheckItem BuildDirectoryCheck(string label, string path)
    {
        var exists = Directory.Exists(path);
        return new StartupCheckItem(
            label,
            exists ? "Hazır" : "Kullanılamıyor",
            path,
            exists ? OperationSeverity.Info : OperationSeverity.Error);
    }

    private static StartupCheckItem BuildExecutableCheck(string label, IReadOnlyList<string> paths, string successMessage)
    {
        var missing = paths.Where(path => !File.Exists(path)).ToArray();
        return new StartupCheckItem(
            label,
            missing.Length == 0 ? "Hazır" : "Kısmi",
            missing.Length == 0
                ? successMessage
                : $"Eksik bileşenler: {string.Join(", ", missing.Select(Path.GetFileName))}",
            missing.Length == 0 ? OperationSeverity.Info : OperationSeverity.Warning);
    }
}

using System.Text.Json;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;
using CigerTool.Domain.Enums;
using CigerTool.Domain.Models;

namespace CigerTool.Infrastructure.Tools;

public sealed class JsonToolCatalogService(
    IOperationLogService operationLogService,
    IEnvironmentProfileService environmentProfileService,
    IAppPathService appPathService) : IToolCatalogService
{
    private const string CatalogFileName = "tools.catalog.json";

    public ToolsWorkspaceSnapshot GetSnapshot()
    {
        var (tools, source) = LoadTools();
        var launchableCount = tools.Count(tool => tool.CanLaunch);
        var missingCount = tools.Count(tool => !tool.Exists);

        return new ToolsWorkspaceSnapshot(
            Heading: "Ek Özellikler",
            Summary: "Gelişmiş tanılama bağlantıları, destek araçları ve isteğe bağlı yardımcı bileşenler burada yer alır. Çekirdek disk işlemleri CigerTool içinde yerel olarak çalışır.",
            Metrics:
            [
                new CardMetric("İsteğe bağlı araç", tools.Count.ToString(), "Tanımlı tüm yardımcı araçlar."),
                new CardMetric("Hazır", launchableCount.ToString(), "Bu sistemde açılabilecek yardımcı bileşenler."),
                new CardMetric("Eksik", missingCount.ToString(), "Dosyası bulunmayan veya çözümlenemeyen öğeler."),
                new CardMetric("Katalog", Path.GetFileName(source), "Etkin araç listesi kaynağı.")
            ],
            Tools: tools,
            LaunchPolicyNote: "Bu bölümdeki yardımcılar isteğe bağlıdır; bulunmamaları CigerTool'un ana disk işlemlerini etkilemez.",
            CatalogSource: source);
    }

    public IReadOnlyList<ToolDefinition> GetTools()
    {
        return LoadTools().Tools;
    }

    private (IReadOnlyList<ToolDefinition> Tools, string Source) LoadTools()
    {
        var paths = appPathService.GetPaths();
        var catalogPath = ResolveCatalogPath(paths);

        try
        {
            var rawItems = catalogPath is not null
                ? ReadCatalogFile(catalogPath)
                : GetFallbackItems();
            var profile = environmentProfileService.GetCurrentProfile();
            var tools = rawItems.Select(item => ResolveTool(item, profile.IsWinPe)).ToArray();
            var source = catalogPath ?? "Dahili katalog";

            return (tools, source);
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "Yardımcı araçlar",
                "İsteğe bağlı araç listesi okunamadı. Dahili liste kullanılıyor.",
                "tools.catalog.error",
                new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });

            var profile = environmentProfileService.GetCurrentProfile();
            return (GetFallbackItems().Select(item => ResolveTool(item, profile.IsWinPe)).ToArray(), "Dahili katalog");
        }
    }

    private static string? ResolveCatalogPath(ApplicationPaths paths)
    {
        var candidates = new[]
        {
            Path.Combine(paths.ConfigDirectory, CatalogFileName),
            Path.Combine(AppContext.BaseDirectory, CatalogFileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private ToolDefinition ResolveTool(ToolCatalogItem item, bool isWinPe)
    {
        var paths = appPathService.GetPaths();
        var resolvedPath = ResolveExecutablePath(item.ExecutablePath, paths);
        var exists = !string.IsNullOrWhiteSpace(resolvedPath) &&
                     (Path.IsPathRooted(resolvedPath)
                        ? File.Exists(resolvedPath)
                        : FileExistsInPath(resolvedPath));
        var canLaunch = exists && (!isWinPe || item.AvailableInWinPe);
        var availabilityStatus = !exists
            ? "Dosya bulunamadı"
            : isWinPe && !item.AvailableInWinPe
                ? "Bu ortamda önerilmez"
                : item.IsBundled
                    ? "Hazır"
                    : "Erişilebilir";

        return new ToolDefinition(
            Id: item.Id,
            Name: item.Name,
            Category: item.Category,
            Description: item.Description,
            ExecutablePath: item.ExecutablePath,
            Arguments: item.Arguments,
            AvailableInWinPe: item.AvailableInWinPe,
            IsBundled: item.IsBundled,
            ResolvedExecutablePath: resolvedPath,
            Exists: exists,
            CanLaunch: canLaunch,
            AvailabilityStatus: availabilityStatus);
    }

    private static IReadOnlyList<ToolCatalogItem> ReadCatalogFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ToolCatalogItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];
    }

    private static IReadOnlyList<ToolCatalogItem> GetFallbackItems()
    {
        return
        [
            new ToolCatalogItem
            {
                Id = "system-info",
                Name = "Sistem bilgisi",
                Category = "Windows",
                Description = "Windows'un yerleşik sistem bilgi ekranını açar.",
                ExecutablePath = "msinfo32.exe",
                AvailableInWinPe = false,
                IsBundled = false
            },
            new ToolCatalogItem
            {
                Id = "log-folder",
                Name = "Günlük klasörü",
                Category = "Destek",
                Description = "CigerTool günlüklerinin bulunduğu klasörü açar.",
                ExecutablePath = "explorer.exe",
                Arguments = "{LogDirectory}",
                AvailableInWinPe = true,
                IsBundled = false
            },
            new ToolCatalogItem
            {
                Id = "disk-management",
                Name = "Disk yönetimi",
                Category = "Windows",
                Description = "Windows Disk Yönetimi konsolunu açar.",
                ExecutablePath = "diskmgmt.msc",
                AvailableInWinPe = false,
                IsBundled = false
            }
        ];
    }

    private static string ResolveExecutablePath(string configuredPath, ApplicationPaths paths)
    {
        var expanded = ExpandPathTokens(System.Environment.ExpandEnvironmentVariables(configuredPath), paths);

        if (Path.IsPathRooted(expanded))
        {
            return expanded;
        }

        if (expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains(Path.AltDirectorySeparatorChar))
        {
            var bundledCandidate = Path.GetFullPath(Path.Combine(paths.BaseDirectory, expanded));
            if (File.Exists(bundledCandidate))
            {
                return bundledCandidate;
            }

            if (expanded.StartsWith("Tools\\", StringComparison.OrdinalIgnoreCase) ||
                expanded.StartsWith("tools\\", StringComparison.OrdinalIgnoreCase))
            {
                return bundledCandidate;
            }

            return Path.GetFullPath(Path.Combine(paths.ToolsDirectory, expanded));
        }

        var matches = EnumeratePathCandidates(expanded).ToArray();
        return matches.FirstOrDefault(File.Exists) ?? expanded;
    }

    private static string ExpandPathTokens(string value, ApplicationPaths paths)
    {
        return value
            .Replace("{BaseDirectory}", paths.BaseDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{ToolsDirectory}", paths.ToolsDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{LogDirectory}", paths.LogDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{DataRoot}", paths.DataRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileExistsInPath(string executableOrPath)
    {
        if (Path.IsPathRooted(executableOrPath))
        {
            return File.Exists(executableOrPath);
        }

        return EnumeratePathCandidates(executableOrPath).Any(File.Exists);
    }

    private static IEnumerable<string> EnumeratePathCandidates(string executableName)
    {
        var searchDirectories = new List<string>();
        var path = System.Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrWhiteSpace(path))
        {
            searchDirectories.AddRange(path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        var windowsDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            searchDirectories.Add(Path.Combine(windowsDirectory, "System32"));
            searchDirectories.Add(windowsDirectory);
        }

        foreach (var directory in searchDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(directory, executableName);
        }
    }

    private sealed class ToolCatalogItem
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string ExecutablePath { get; init; } = string.Empty;

        public string? Arguments { get; init; }

        public bool AvailableInWinPe { get; init; } = true;

        public bool IsBundled { get; init; }
    }
}

using System.Net.Http;
using System.Text.Json;
using CigerTool.Application.Contracts;
using CigerTool.Domain.Enums;
using CigerTool.Usb.Contracts;
using CigerTool.Usb.Models;

namespace CigerTool.Usb.Services;

public sealed class ReleaseSourceResolver(
    ISettingsService settingsService,
    IOperationLogService operationLogService,
    IAppPathService appPathService) : IReleaseSourceResolver
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task<ReleaseManifestSummary> ResolveAsync(string? manualImagePath, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(manualImagePath))
        {
            var manualSummary = BuildManualSummary(manualImagePath, settingsService.GetSettings().DefaultChannel);
            operationLogService.Record(
                File.Exists(manualSummary.LocalImagePath) ? OperationSeverity.Info : OperationSeverity.Warning,
                "USB Oluşturma",
                "Elle seçilen imaj kaynağı çözüldü.",
                "usb.release.manual",
                new Dictionary<string, string>
                {
                    ["path"] = manualSummary.LocalImagePath ?? string.Empty,
                    ["status"] = manualSummary.Status
                });

            return manualSummary;
        }

        var settings = settingsService.GetSettings();
        var localOverride = TryLoadLocalOverride();

        if (localOverride.Override is not null)
        {
            if (!string.IsNullOrWhiteSpace(localOverride.Override.ImageFile))
            {
                var overrideImage = ResolveOverridePath(localOverride.Path!, localOverride.Override.ImageFile);
                var summary = BuildOverrideImageSummary(localOverride.Override, overrideImage, settings.DefaultChannel, localOverride.Path!);

                operationLogService.Record(
                    File.Exists(summary.LocalImagePath) ? OperationSeverity.Info : OperationSeverity.Warning,
                    "USB Oluşturma",
                    "Yerel geçersiz kılma ile imaj kaynağı çözüldü.",
                    "usb.release.override.image",
                    new Dictionary<string, string>
                    {
                        ["overridePath"] = localOverride.Path ?? string.Empty,
                        ["image"] = summary.LocalImagePath ?? string.Empty
                    });

                return summary;
            }

            if (!string.IsNullOrWhiteSpace(localOverride.Override.ManifestUrl))
            {
                try
                {
                    var summary = await FetchManifestAsync(
                        localOverride.Override.ManifestUrl,
                        localOverride.Override.Channel ?? settings.DefaultChannel,
                        $"Yerel geçersiz kılma manifesti: {localOverride.Override.ManifestUrl}",
                        "Yerel geçersiz kılma",
                        cancellationToken);

                    CacheSummary(summary);
                    operationLogService.Record(
                        OperationSeverity.Info,
                        "USB Oluşturma",
                        "Yerel geçersiz kılma manifesti çözüldü.",
                        "usb.release.override.manifest",
                        new Dictionary<string, string>
                        {
                            ["overridePath"] = localOverride.Path ?? string.Empty,
                            ["manifestUrl"] = localOverride.Override.ManifestUrl ?? string.Empty,
                            ["version"] = summary.Version
                        });

                    return summary;
                }
                catch (Exception ex)
                {
                    operationLogService.Record(
                        OperationSeverity.Warning,
                        "USB Oluşturma",
                        "Yerel geçersiz kılma manifesti okunamadı.",
                        "usb.release.override.failure",
                        new Dictionary<string, string>
                        {
                            ["overridePath"] = localOverride.Path ?? string.Empty,
                            ["error"] = ex.Message
                        });

                    var cached = TryReadCachedSummary();
                    if (cached is not null)
                    {
                        return cached with
                        {
                            Status = $"Yerel geçersiz kılma okunamadı. Önbellekteki yayın bilgisi gösteriliyor: {ex.Message}",
                            ModeLabel = "Önbellek",
                            IsCachedFallback = true
                        };
                    }

                    return BuildUnavailableSummary(
                        settings.DefaultChannel,
                        $"Yerel geçersiz kılma manifesti okunamadı: {ex.Message}");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultManifestUrl))
        {
            return BuildUnavailableSummary(
                settings.DefaultChannel,
                "Varsayılan yayın adresi tanımlı değil. Yerel geçersiz kılma veya elle dosya seçimi kullanın.");
        }

        try
        {
            var summary = await FetchManifestAsync(
                settings.DefaultManifestUrl,
                settings.DefaultChannel,
                settings.DefaultManifestUrl,
                "Çevrimiçi yayın bilgisi",
                cancellationToken);

            CacheSummary(summary);
            operationLogService.Record(
                OperationSeverity.Info,
                "USB Oluşturma",
                "Çevrimiçi yayın bilgisi çözüldü.",
                "usb.release.remote",
                new Dictionary<string, string>
                {
                    ["manifestUrl"] = settings.DefaultManifestUrl,
                    ["version"] = summary.Version
                });

            return summary;
        }
        catch (Exception ex)
        {
            operationLogService.Record(
                OperationSeverity.Warning,
                "USB Oluşturma",
                "Çevrimiçi yayın bilgisi okunamadı.",
                "usb.release.remote.failure",
                new Dictionary<string, string>
                {
                    ["manifestUrl"] = settings.DefaultManifestUrl ?? string.Empty,
                    ["error"] = ex.Message
                });

            var cached = TryReadCachedSummary();
            if (cached is not null)
            {
                operationLogService.Record(
                    OperationSeverity.Info,
                    "USB Oluşturma",
                    "Ağ hatası sonrası önbellekteki yayın bilgisi kullanılıyor.",
                    "usb.release.cache",
                    new Dictionary<string, string>
                    {
                        ["version"] = cached.Version
                    });

                return cached with
                {
                    Status = $"Ağ bağlantısında sorun var. Önbellekteki yayın bilgisi gösteriliyor: {ex.Message}",
                    ModeLabel = "Önbellek",
                    IsCachedFallback = true
                };
            }

            return BuildUnavailableSummary(
                settings.DefaultChannel,
                $"Çevrimiçi yayın bilgisi okunamadı: {ex.Message}");
        }
    }

    private async Task<ReleaseManifestSummary> FetchManifestAsync(
        string manifestUrl,
        string fallbackChannel,
        string sourceDescription,
        string modeLabel,
        CancellationToken cancellationToken)
    {
        manifestUrl = NormalizeManifestUrl(manifestUrl);

        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri))
        {
            throw new InvalidOperationException("Manifest adresi geçerli, tam bir URL değil.");
        }

        using var response = await HttpClient.GetAsync(manifestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        return ParseManifest(json, fallbackChannel, sourceDescription, modeLabel);
    }

    private static ReleaseManifestSummary ParseManifest(
        string json,
        string fallbackChannel,
        string sourceDescription,
        string modeLabel)
    {
        var document = JsonSerializer.Deserialize<ReleaseManifestDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Manifest içeriği boş.");

        var channel = FirstNonEmpty(document.Channel, fallbackChannel);
        var version = RequireValue(document.Version, "version");
        var imageName = RequireValue(document.ImageName, "image_name");
        var imageUrl = RequireValue(document.ImageUrl, "image_url");
        var sha256 = NormalizeSha256(RequireValue(document.Sha256, "sha256"));
        var notes = string.IsNullOrWhiteSpace(document.Notes) ? "Yayın notu sağlanmadı." : document.Notes.Trim();

        return new ReleaseManifestSummary(
            Channel: channel,
            Version: version,
            ImageName: imageName,
            ImageUrl: imageUrl,
            Sha256: sha256,
            Notes: notes,
            SizeBytes: document.SizeBytes,
            SourceDescription: sourceDescription,
            Status: "Yayın bilgisi başarıyla çözüldü.",
            ModeLabel: modeLabel,
            LocalImagePath: null,
            IsCachedFallback: false);
    }

    private static ReleaseManifestSummary BuildManualSummary(string manualImagePath, string defaultChannel)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(manualImagePath));
        var fileExists = File.Exists(fullPath);
        var fileInfo = fileExists ? new FileInfo(fullPath) : null;

        return new ReleaseManifestSummary(
            Channel: defaultChannel,
            Version: "Elle seçilen imaj",
            ImageName: Path.GetFileName(fullPath),
            ImageUrl: null,
            Sha256: TryReadSidecarSha256(fullPath),
            Notes: "Bu oturumda çevrimiçi yayın bilgisi yerine elle seçilen imaj kullanılıyor.",
            SizeBytes: fileInfo?.Length,
            SourceDescription: fullPath,
            Status: fileExists ? "Elle seçilen imaj hazır." : "Seçilen imaj dosyası bulunamadı.",
            ModeLabel: "Elle seçilen dosya",
            LocalImagePath: fullPath,
            IsCachedFallback: false);
    }

    private static ReleaseManifestSummary BuildOverrideImageSummary(
        LocalReleaseOverride localOverride,
        string imagePath,
        string defaultChannel,
        string overridePath)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(imagePath));
        var fileExists = File.Exists(fullPath);
        var fileInfo = fileExists ? new FileInfo(fullPath) : null;

        return new ReleaseManifestSummary(
            Channel: FirstNonEmpty(localOverride.Channel, defaultChannel),
            Version: FirstNonEmpty(localOverride.Version, "Yerel imaj"),
            ImageName: FirstNonEmpty(localOverride.ImageName, Path.GetFileName(fullPath)),
            ImageUrl: null,
            Sha256: NormalizeSha256(localOverride.Sha256) ?? TryReadSidecarSha256(fullPath),
            Notes: string.IsNullOrWhiteSpace(localOverride.Notes)
                ? $"Yerel geçersiz kılma dosyası aktif: {overridePath}"
                : localOverride.Notes.Trim(),
            SizeBytes: fileInfo?.Length,
            SourceDescription: fullPath,
            Status: fileExists ? "Yerel imaj hazır." : "Yerel imaj dosyası bulunamadı.",
            ModeLabel: "Yerel geçersiz kılma",
            LocalImagePath: fullPath,
            IsCachedFallback: false);
    }

    private static ReleaseManifestSummary BuildUnavailableSummary(string defaultChannel, string status)
    {
        return new ReleaseManifestSummary(
            Channel: defaultChannel,
            Version: "Hazır değil",
            ImageName: "İmaj bilgisi çözülemedi",
            ImageUrl: null,
            Sha256: null,
            Notes: "Şu anda kullanılabilir yayın bilgisi yok.",
            SizeBytes: null,
            SourceDescription: "Yayın kaynağı çözülemedi.",
            Status: status,
            ModeLabel: "Hazır değil",
            LocalImagePath: null,
            IsCachedFallback: false);
    }

    private (LocalReleaseOverride? Override, string? Path) TryLoadLocalOverride()
    {
        foreach (var path in GetOverrideProbePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var model = JsonSerializer.Deserialize<LocalReleaseOverride>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (model?.Enabled != false)
                {
                    return (model, path);
                }
            }
            catch (Exception ex)
            {
                operationLogService.Record(
                    OperationSeverity.Warning,
                    "USB Oluşturma",
                    "Yerel geçersiz kılma dosyası çözülemedi.",
                    "usb.release.override.parse",
                    new Dictionary<string, string>
                    {
                        ["path"] = path,
                        ["error"] = ex.Message
                    });
            }
        }

        return (null, null);
    }

    private IEnumerable<string> GetOverrideProbePaths()
    {
        yield return Path.Combine(appPathService.GetPaths().ConfigDirectory, "release-source.override.json");
        yield return Path.Combine(AppContext.BaseDirectory, "Config", "release-source.override.json");
        yield return Path.Combine(AppContext.BaseDirectory, "CigerTool", "Config", "release-source.override.json");

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            yield return Path.Combine(programData, "CigerTool", "release-source.override.json");
        }
    }

    private static string ResolveOverridePath(string overridePath, string configuredPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
        if (Path.IsPathRooted(expanded))
        {
            return expanded;
        }

        var baseDirectory = Path.GetDirectoryName(overridePath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }

    private static string? TryReadSidecarSha256(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var candidates = new[]
        {
            imagePath + ".sha256",
            Path.ChangeExtension(imagePath, ".sha256")
        }
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(candidate).Trim();
                var token = content
                    .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(token))
                {
                    return NormalizeSha256(token);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private void CacheSummary(ReleaseManifestSummary summary)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GetCachePath())!);
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetCachePath(), json);
        }
        catch
        {
        }
    }

    private ReleaseManifestSummary? TryReadCachedSummary()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ReleaseManifestSummary>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private string GetCachePath()
    {
        return Path.Combine(appPathService.GetPaths().CacheDirectory, "release-manifest-cache.json");
    }

    private static string RequireValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Manifest alanı eksik: {fieldName}");
        }

        return value.Trim();
    }

    private static string FirstNonEmpty(string? preferred, string fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
    }

    private static string? NormalizeSha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        var normalized = sha256.Trim().ToLowerInvariant();
        if (normalized.Contains("put_real", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : null;
    }

    private static string NormalizeManifestUrl(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri))
        {
            return manifestUrl;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return manifestUrl;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 4 && string.Equals(segments[2], "blob", StringComparison.OrdinalIgnoreCase))
        {
            var owner = segments[0];
            var repo = segments[1];
            var branch = segments[3];
            var relativePath = string.Join('/', segments.Skip(4));
            return $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{relativePath}";
        }

        return manifestUrl;
    }
}

using System.Text.Json;
using CigerTool.Application.Contracts;
using CigerTool.Application.Models;

namespace CigerTool.Infrastructure.Configuration;

public sealed class JsonSettingsService(IAppPathService appPathService) : ISettingsService
{
    private const string OverrideFileName = "appsettings.override.json";

    public ApplicationSettings GetSettings()
    {
        var model = LoadModel();

        return new ApplicationSettings(
            ProductName: model.Product.DisplayName,
            Language: model.Product.Language,
            DefaultChannel: model.ReleaseSource.DefaultChannel,
            DefaultManifestUrl: model.ReleaseSource.DefaultManifestUrl,
            UseTurkishDefaults: model.Product.UseTurkishDefaults,
            PreferSingleFilePublishing: model.Product.PreferSingleFilePublishing);
    }

    public SettingsWorkspaceSnapshot GetSnapshot()
    {
        var settings = GetSettings();
        var paths = appPathService.GetPaths();

        return new SettingsWorkspaceSnapshot(
            Heading: "Ayarlar",
            Summary: "Uygulama dili, güncelleme kanalı ve kayıt konumları burada toplanır.",
            Language: settings.Language,
            UpdateChannel: settings.DefaultChannel,
            ManifestUrl: settings.DefaultManifestUrl,
            UseTurkishDefaults: settings.UseTurkishDefaults,
            PreferSingleFilePublishing: settings.PreferSingleFilePublishing,
            StorageMode: paths.StorageModeLabel,
            DataRoot: paths.DataRoot,
            LogRoot: paths.LogDirectory,
            DownloadsRoot: paths.DownloadsDirectory,
            ToolsRoot: paths.ToolsDirectory);
    }

    private SettingsFileModel LoadModel()
    {
        var model = SettingsFileModel.CreateDefault();

        foreach (var overridePath in GetOverrideProbePaths())
        {
            var overrideModel = ReadOverrideModel(overridePath);
            if (overrideModel is null)
            {
                continue;
            }

            model = model.Merge(overrideModel);
        }

        return model;
    }

    private IEnumerable<string> GetOverrideProbePaths()
    {
        var paths = appPathService.GetPaths();
        yield return Path.Combine(paths.ConfigDirectory, OverrideFileName);
    }

    private static SettingsOverrideFileModel? ReadOverrideModel(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SettingsOverrideFileModel>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private sealed class SettingsFileModel
    {
        public ProductSection Product { get; init; } = new();

        public ReleaseSourceSection ReleaseSource { get; init; } = new();

        public static SettingsFileModel CreateDefault() => new();

        public SettingsFileModel Merge(SettingsOverrideFileModel overrideModel)
        {
            return new SettingsFileModel
            {
                Product = Product.Merge(overrideModel.Product),
                ReleaseSource = ReleaseSource.Merge(overrideModel.ReleaseSource)
            };
        }
    }

    private sealed class ProductSection
    {
        public string DisplayName { get; init; } = "CigerTool";

        public string Language { get; init; } = "tr-TR";

        public bool UseTurkishDefaults { get; init; } = true;

        public bool PreferSingleFilePublishing { get; init; } = true;

        public ProductSection Merge(OverrideProductSection? overrideSection)
        {
            if (overrideSection is null)
            {
                return this;
            }

            return new ProductSection
            {
                DisplayName = string.IsNullOrWhiteSpace(overrideSection.DisplayName) ? DisplayName : overrideSection.DisplayName,
                Language = string.IsNullOrWhiteSpace(overrideSection.Language) ? Language : overrideSection.Language,
                UseTurkishDefaults = overrideSection.UseTurkishDefaults ?? UseTurkishDefaults,
                PreferSingleFilePublishing = overrideSection.PreferSingleFilePublishing ?? PreferSingleFilePublishing
            };
        }
    }

    private sealed class ReleaseSourceSection
    {
        public string DefaultChannel { get; init; } = "stable";

        public string? DefaultManifestUrl { get; init; } = "https://raw.githubusercontent.com/hkanq/CigerTool_Disk_Klonlama_hkannq/main/release.json";

        public ReleaseSourceSection Merge(OverrideReleaseSourceSection? overrideSection)
        {
            if (overrideSection is null)
            {
                return this;
            }

            return new ReleaseSourceSection
            {
                DefaultChannel = string.IsNullOrWhiteSpace(overrideSection.DefaultChannel)
                    ? DefaultChannel
                    : overrideSection.DefaultChannel,
                DefaultManifestUrl = string.IsNullOrWhiteSpace(overrideSection.DefaultManifestUrl)
                    ? DefaultManifestUrl
                    : overrideSection.DefaultManifestUrl
            };
        }
    }

    private sealed class SettingsOverrideFileModel
    {
        public OverrideProductSection? Product { get; init; }

        public OverrideReleaseSourceSection? ReleaseSource { get; init; }
    }

    private sealed class OverrideProductSection
    {
        public string? DisplayName { get; init; }

        public string? Language { get; init; }

        public bool? UseTurkishDefaults { get; init; }

        public bool? PreferSingleFilePublishing { get; init; }
    }

    private sealed class OverrideReleaseSourceSection
    {
        public string? DefaultChannel { get; init; }

        public string? DefaultManifestUrl { get; init; }
    }
}

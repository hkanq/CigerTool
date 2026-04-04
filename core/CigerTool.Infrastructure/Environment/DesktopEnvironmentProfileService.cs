using Microsoft.Win32;
using System.Runtime.Versioning;
using CigerTool.Application.Contracts;
using CigerTool.Domain.Models;
using CigerTool.Infrastructure.Common;

namespace CigerTool.Infrastructure.Environment;

[SupportedOSPlatform("windows")]
public sealed class DesktopEnvironmentProfileService : IEnvironmentProfileService
{
    public AppEnvironmentProfile GetCurrentProfile()
    {
        var isWinPe = DetectWinPe();

        return isWinPe
            ? new AppEnvironmentProfile(
                ProfileName: "Servis ortamı",
                IsWinPe: true,
                SupportsUsbCreation: true,
                SupportsLiveCloning: false,
                Summary: "Çevrimdışı bakım, kurtarma ve güvenli disk işlemleri için hazır.")
            : new AppEnvironmentProfile(
                ProfileName: "Masaüstü",
                IsWinPe: false,
                SupportsUsbCreation: true,
                SupportsLiveCloning: true,
                Summary: "Günlük kullanım için uygun; çalışan sistem diski işlemlerinde ek güvenlik denetimleri uygulanır.");
    }

    private static bool DetectWinPe()
    {
        if (BuildFlavorDetector.IsWinPeFlavor())
        {
            return true;
        }

        if (System.Environment.SystemDirectory.StartsWith(@"X:\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        return key is not null;
    }
}

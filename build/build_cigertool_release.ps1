param(
    [string]$WorkspaceWimPath = "inputs\workspace\install.wim",
    [string]$OutputRoot = "build-output\workspace",
    [string]$ArtifactRoot = "artifacts",
    [string]$AppBuildRoot = "build-output\app\dist\CigerTool",
    [string]$IsoLibraryRoot = "iso-library",
    [string]$PrimaryArtifactName = "CigerTool-Workspace.iso",
    [string]$DebugArtifactName = "CigerTool-Workspace-debug.zip",
    [switch]$PlanOnly,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Write-ReleaseLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Write-Host $line
    Add-Content -Path $script:LogFile -Value $line
}

function Resolve-ProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$PathValue
    )

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot $PathValue))
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$PathValue)
    New-Item -ItemType Directory -Force -Path $PathValue | Out-Null
}

function Get-DirectorySizeBytes {
    param([Parameter(Mandatory = $true)][string]$PathValue)

    if (-not (Test-Path -LiteralPath $PathValue)) {
        return [UInt64]0
    }

    $files = Get-ChildItem -LiteralPath $PathValue -Recurse -File -Force
    if ($null -eq $files -or $files.Count -eq 0) {
        return [UInt64]0
    }

    $measure = $files | Measure-Object -Property Length -Sum
    if ($null -eq $measure.Sum) {
        return [UInt64]0
    }

    return [UInt64]$measure.Sum
}

function Assert-Path {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $PathValue)) {
        throw "$Description bulunamadi: $PathValue"
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CurrentIdentityName {
    try {
        return [Security.Principal.WindowsIdentity]::GetCurrent().Name
    }
    catch {
        return "<unknown>"
    }
}

function Assert-PathWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$RootPath
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($PathValue)
    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Yol beklenen kok altinda degil: $resolvedPath | kok=$resolvedRoot"
    }
}

function Remove-GeneratedPath {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    if (-not (Test-Path -LiteralPath $PathValue)) {
        return
    }

    Assert-PathWithinRoot -PathValue $PathValue -RootPath $AllowedRoot
    Remove-Item -LiteralPath $PathValue -Recurse -Force
    Write-ReleaseLog "Eski generate path temizlendi: $PathValue"
}

function Ensure-AppBuild {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$AppBuildRoot
    )

    $appExe = Join-Path $AppBuildRoot "CigerTool.exe"
    if (Test-Path -LiteralPath $appExe) {
        Write-ReleaseLog "Mevcut paketlenmis uygulama yeniden kullaniliyor: $appExe"
        return
    }

    Write-ReleaseLog "Paketlenmis uygulama bulunamadi, package_cigertool_app.ps1 calistiriliyor."
    & (Join-Path $ProjectRoot "build\internal\package_cigertool_app.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "package_cigertool_app.ps1 basarisiz oldu."
    }

    Assert-Path -PathValue $appExe -Description "Paketlenmis CigerTool uygulamasi"
}

function Get-WorkspaceSection {
    param([Parameter(Mandatory = $true)][string]$GrubContent)

    $isoMarker = 'submenu "ISO Library"'
    $index = $GrubContent.IndexOf($isoMarker)
    if ($index -lt 0) {
        return $GrubContent
    }

    return $GrubContent.Substring(0, $index)
}

function Assert-NoSetupFlowInWorkspace {
    param([Parameter(Mandatory = $true)][string]$GrubCfgPath)

    $content = Get-Content -LiteralPath $GrubCfgPath -Raw -Encoding utf8
    $workspaceSection = Get-WorkspaceSection -GrubContent $content

    foreach ($blockedToken in @("boot.wim", "setup.exe", "Windows Setup")) {
        if ($workspaceSection -match [regex]::Escape($blockedToken)) {
            throw "Workspace girisinde yasakli setup akisi izi bulundu: $blockedToken"
        }
    }

    foreach ($requiredToken in @(
        'menuentry "CigerTool Workspace"',
        'chainloader /EFI/Microsoft/Boot/bootmgfw.efi',
        '/EFI/Microsoft/Boot/BCD',
        '/workspace/CigerToolWorkspace.vhdx'
    )) {
        if ($workspaceSection -notmatch [regex]::Escape($requiredToken)) {
            throw "Workspace girisinde beklenen token bulunamadi: $requiredToken"
        }
    }
}

function Assert-PlanInputsAndOutputs {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$WorkspaceWimSource,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    $requiredPaths = @(
        @{ path = $WorkspaceWimSource; description = "Workspace source WIM" }
        @{ path = (Join-Path $ProjectRoot "workspace\startup\Start-CigerToolWorkspace.ps1"); description = "Workspace startup hook" }
        @{ path = (Join-Path $ProjectRoot "workspace\startup\CigerToolWorkspace.Runtime.ps1"); description = "Workspace runtime helper" }
        @{ path = (Join-Path $OutputRoot "manifests\workspace-plan.json"); description = "Workspace plan manifest" }
        @{ path = (Join-Path $OutputRoot "manifests\CigerToolWorkspace.Unattend.xml"); description = "Workspace unattend manifest" }
        @{ path = (Join-Path $OutputRoot "usb-layout\CigerTool.workspace.json"); description = "Workspace marker" }
        @{ path = (Join-Path $OutputRoot "usb-layout\EFI\CigerTool\grub.cfg"); description = "Workspace GRUB config" }
        @{ path = (Join-Path $OutputRoot "usb-layout\EFI\CigerTool\boot-manifest.json"); description = "Boot manifest" }
        @{ path = (Join-Path $OutputRoot "usb-layout\EFI\Boot\bootx64.efi"); description = "UEFI boot image" }
        @{ path = (Join-Path $OutputRoot "workspace-stage\Program Files\CigerToolWorkspace\startup\Start-CigerToolWorkspace.ps1"); description = "Staged startup hook" }
        @{ path = (Join-Path $OutputRoot "workspace-stage\Program Files\CigerTool\CigerTool.exe"); description = "Staged CigerTool executable" }
        @{ path = (Join-Path $OutputRoot "usb-layout\isos\windows"); description = "ISO Library windows root" }
        @{ path = (Join-Path $OutputRoot "usb-layout\isos\linux"); description = "ISO Library linux root" }
        @{ path = (Join-Path $OutputRoot "usb-layout\isos\tools"); description = "ISO Library tools root" }
    )

    foreach ($entry in $requiredPaths) {
        Assert-Path -PathValue $entry.path -Description $entry.description
    }

    Assert-NoSetupFlowInWorkspace -GrubCfgPath (Join-Path $OutputRoot "usb-layout\EFI\CigerTool\grub.cfg")
}

function Assert-ReleaseInputsAndOutputs {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$WorkspaceWimSource,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    Assert-PlanInputsAndOutputs -ProjectRoot $ProjectRoot -WorkspaceWimSource $WorkspaceWimSource -OutputRoot $OutputRoot

    foreach ($entry in @(
        @{ path = (Join-Path $OutputRoot "usb-layout\EFI\Microsoft\Boot\bootmgfw.efi"); description = "Workspace boot loader" }
        @{ path = (Join-Path $OutputRoot "usb-layout\EFI\Microsoft\Boot\BCD"); description = "Workspace BCD" }
        @{ path = (Join-Path $OutputRoot "usb-layout\workspace\CigerToolWorkspace.vhdx"); description = "Workspace VHDX" }
    )) {
        Assert-Path -PathValue $entry.path -Description $entry.description
    }

    $bootManifest = Get-Content -LiteralPath (Join-Path $OutputRoot "usb-layout\EFI\CigerTool\boot-manifest.json") -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not $bootManifest.workspace.loader_present) {
        throw "Workspace loader boot-manifest icinde mevcut degil."
    }
    if (-not $bootManifest.workspace.bcd_present) {
        throw "Workspace BCD boot-manifest icinde mevcut degil."
    }
    if (-not $bootManifest.workspace.vhd_present) {
        throw "Workspace VHDX boot-manifest icinde mevcut degil."
    }
    if (-not $bootManifest.workspace.marker_present) {
        throw "Workspace marker boot-manifest icinde mevcut degil."
    }
    if (-not $bootManifest.iso_library.windows_present -or -not $bootManifest.iso_library.linux_present -or -not $bootManifest.iso_library.tools_present) {
        throw "ISO Library koklerinden biri boot-manifest icinde eksik."
    }
}

function Assert-LockedWorkspaceDefaults {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $unattendPath = Join-Path $ProjectRoot "workspace\unattend\CigerToolWorkspace.Unattend.xml"
    $startupPath = Join-Path $ProjectRoot "workspace\startup\Start-CigerToolWorkspace.ps1"
    $prepareScriptPath = Join-Path $ProjectRoot "build\internal\prepare_workspace_runtime.ps1"

    $unattendContent = Get-Content -LiteralPath $unattendPath -Raw -Encoding utf8
    $startupContent = Get-Content -LiteralPath $startupPath -Raw -Encoding utf8
    $prepareScriptContent = Get-Content -LiteralPath $prepareScriptPath -Raw -Encoding utf8

    foreach ($requiredToken in @(
        "tr-TR",
        "Turkey Standard Time",
        "<ComputerName>CigerTool</ComputerName>",
        "<Username>CigerTool</Username>",
        "<SkipMachineOOBE>true</SkipMachineOOBE>",
        "<SkipUserOOBE>true</SkipUserOOBE>",
        "<HideOnlineAccountScreens>true</HideOnlineAccountScreens>",
        "<HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>"
    )) {
        if ($unattendContent -notmatch [regex]::Escape($requiredToken)) {
            throw "Locked workspace default dogrulamasi basarisiz. Eksik unattend token: $requiredToken"
        }
    }

    foreach ($requiredToken in @(
        "AutoAdminLogon",
        "DefaultUserName",
        "DefaultPassword",
        "New-ItemProperty",
        "ForceAutoLogon",
        "AutoLogonCount",
        "PortableOperatingSystem",
        "Start-CigerToolWorkspace.ps1",
        "Set-AllIntl:tr-TR",
        'Set-TimeZone:"Turkey Standard Time"'
    )) {
        if ($prepareScriptContent -notmatch [regex]::Escape($requiredToken)) {
            throw "Locked workspace default dogrulamasi basarisiz. Eksik prepare token: $requiredToken"
        }
    }

    foreach ($requiredToken in @(
        "CIGERTOOL_RUNTIME",
        "CIGERTOOL_TOOLS_ROOT",
        "CIGERTOOL_ISOS_ROOT",
        "workspace-startup.log",
        "CigerTool otomatik baslatildi"
    )) {
        if ($startupContent -notmatch [regex]::Escape($requiredToken)) {
            throw "Locked workspace default dogrulamasi basarisiz. Eksik startup token: $requiredToken"
        }
    }
}

function New-DebugZip {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactPath,
        [Parameter(Mandatory = $true)][string]$ReleaseManifestPath
    )

    $stagingRoot = Join-Path $env:TEMP ("cigertool-release-debug-" + [guid]::NewGuid().ToString("N"))
    $logsRoot = Join-Path $ProjectRoot "artifacts\logs"
    $manifestsRoot = Join-Path $OutputRoot "manifests"
    $bootManifest = Join-Path $OutputRoot "usb-layout\EFI\CigerTool\boot-manifest.json"
    $workspaceMarker = Join-Path $OutputRoot "usb-layout\CigerTool.workspace.json"

    Ensure-Directory -PathValue $stagingRoot
    try {
        foreach ($item in @($logsRoot, $manifestsRoot)) {
            if (Test-Path -LiteralPath $item) {
                Copy-Item -LiteralPath $item -Destination (Join-Path $stagingRoot (Split-Path -Path $item -Leaf)) -Recurse -Force
            }
        }

        foreach ($file in @($bootManifest, $workspaceMarker, $ReleaseManifestPath)) {
            if (Test-Path -LiteralPath $file) {
                $targetDir = Join-Path $stagingRoot "release"
                Ensure-Directory -PathValue $targetDir
                Copy-Item -LiteralPath $file -Destination $targetDir -Force
            }
        }

        if (Test-Path -LiteralPath $ArtifactPath) {
            Remove-Item -LiteralPath $ArtifactPath -Force
        }
        Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $ArtifactPath -CompressionLevel Optimal
        Write-ReleaseLog "Debug artifact olusturuldu: $ArtifactPath"
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-IsoFromDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [string]$VolumeName = "CIGERTOOL"
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class CigerToolComStreamSaver {
    public static void SaveToFile(object comObject, string path) {
        var stream = (IStream)comObject;
        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
            var buffer = new byte[64 * 1024];
            var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
            try {
                while (true) {
                    Marshal.WriteInt32(readPtr, 0);
                    stream.Read(buffer, buffer.Length, readPtr);
                    int read = Marshal.ReadInt32(readPtr);
                    if (read <= 0) {
                        break;
                    }
                    output.Write(buffer, 0, read);
                }
            }
            finally {
                Marshal.FreeCoTaskMem(readPtr);
            }
        }
    }
}
'@ -ErrorAction SilentlyContinue

    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $fsi.FileSystemsToCreate = 4
    $fsi.UDFRevision = 0x0250
    $fsi.VolumeName = $VolumeName
    $fsi.StageFiles = $false
    $fsi.UseRestrictedCharacterSet = $false

    $bootImagePath = Join-Path $SourceDirectory "EFI\Boot\bootx64.efi"
    Assert-Path -PathValue $bootImagePath -Description "UEFI boot image"

    $bootImageStream = New-Object -ComObject ADODB.Stream
    $bootImageStream.Type = 1
    $bootImageStream.Open()
    $bootImageStream.LoadFromFile($bootImagePath)

    $bootOptions = New-Object -ComObject IMAPI2FS.BootOptions
    $bootOptions.AssignBootImage($bootImageStream)
    $bootOptions.PlatformId = 0xEF
    $bootOptions.Manufacturer = "CigerTool"
    $fsi.BootImageOptions = $bootOptions

    $sourceBytes = Get-DirectorySizeBytes -PathValue $SourceDirectory
    $headroomBytes = [UInt64]([Math]::Max([double](512MB), [double][Math]::Ceiling($sourceBytes * 0.05)))
    $requiredBlocks = [UInt64][Math]::Ceiling(($sourceBytes + $headroomBytes) / 2048.0)
    if ($requiredBlocks -gt [UInt64][int]::MaxValue) {
        throw "ISO kapasitesi Int32 block sinirini asiyor. kaynak=$SourceDirectory | boyut=$sourceBytes byte"
    }
    $fsi.FreeMediaBlocks = [int]$requiredBlocks
    $sourceGb = [Math]::Round($sourceBytes / 1GB, 2)
    $headroomGb = [Math]::Round($headroomBytes / 1GB, 2)
    Write-ReleaseLog "ISO kapasitesi kaynak boyutuna gore ayarlandi | kaynak=$sourceGb GB | headroom=$headroomGb GB | free_media_blocks=$requiredBlocks"
    Write-ReleaseLog "ISO UEFI boot image olarak kullaniliyor: $bootImagePath"

    try {
        $fsi.Root.AddTree($SourceDirectory, $false)
        $result = $fsi.CreateResultImage()
        [CigerToolComStreamSaver]::SaveToFile($result.ImageStream, $DestinationPath)
    }
    finally {
        try {
            $bootImageStream.Close()
        }
        catch {
        }
    }
    Write-ReleaseLog "ISO artifact olusturuldu: $DestinationPath"
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedOutputRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $OutputRoot
$resolvedArtifactRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $ArtifactRoot
$resolvedWorkspaceWimPath = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $WorkspaceWimPath
$resolvedAppBuildRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $AppBuildRoot
$resolvedIsoLibraryRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $IsoLibraryRoot
$releaseLogRoot = Join-Path $resolvedArtifactRoot "logs"
$primaryArtifactPath = Join-Path $resolvedArtifactRoot $PrimaryArtifactName
$debugArtifactPath = Join-Path $resolvedArtifactRoot $DebugArtifactName
$hashArtifactPath = $primaryArtifactPath + ".sha256"
$releaseManifestPath = Join-Path $resolvedArtifactRoot "CigerTool-Workspace.release.json"

foreach ($path in @($resolvedArtifactRoot, $releaseLogRoot)) {
    Ensure-Directory -PathValue $path
}

$script:LogFile = Join-Path $releaseLogRoot "release-build.log"
Set-Content -Path $script:LogFile -Value ""

Write-ReleaseLog "CigerTool final artifact generation baslatiliyor."
Write-ReleaseLog "Ana build girisi: build\\build_cigertool_release.ps1"

Assert-Path -PathValue $resolvedWorkspaceWimPath -Description "Hazir workspace WIM girdisi (beklenen yol: inputs\\workspace\\install.wim)"
Assert-Path -PathValue $resolvedIsoLibraryRoot -Description "ISO Library kaynak klasoru"
Assert-LockedWorkspaceDefaults -ProjectRoot $projectRoot

if ((-not $PlanOnly) -and (-not (Test-IsAdministrator))) {
    $identityName = Get-CurrentIdentityName
    throw "Final artifact generation elevasyon gerektirir. Mevcut Windows kimligi: $identityName. Bu build, VHDX hazirlama icin diskpart ve DISM kullaniyor. Self-hosted runner servisini yerel administrator hesabi ile calistirin veya runner'i yukseltilmis bir terminalden run.cmd ile baslatin."
}

if (-not $SkipTests) {
    Write-ReleaseLog "Unit testler calistiriliyor."
    & python -m unittest discover -s tests -p "test_*.py"
    if ($LASTEXITCODE -ne 0) {
        throw "Unit testler basarisiz oldu."
    }
}

Ensure-AppBuild -ProjectRoot $projectRoot -AppBuildRoot $resolvedAppBuildRoot

foreach ($generatedPath in @(
    (Join-Path $resolvedOutputRoot "workspace"),
    (Join-Path $resolvedOutputRoot "workspace-stage"),
    (Join-Path $resolvedOutputRoot "usb-layout")
)) {
    Remove-GeneratedPath -PathValue $generatedPath -AllowedRoot $resolvedOutputRoot
}

foreach ($artifactPath in @($primaryArtifactPath, $debugArtifactPath, $hashArtifactPath, $releaseManifestPath)) {
    if (Test-Path -LiteralPath $artifactPath) {
        Remove-Item -LiteralPath $artifactPath -Force
    }
}

& (Join-Path $projectRoot "build\internal\stage_release_layout.ps1") `
    -WorkspaceWimPath $resolvedWorkspaceWimPath `
    -OutputRoot $resolvedOutputRoot `
    -AppBuildRoot $resolvedAppBuildRoot `
    -IsoLibraryRoot $resolvedIsoLibraryRoot `
    -PlanOnly:$PlanOnly

if ($PlanOnly) {
    Assert-PlanInputsAndOutputs -ProjectRoot $projectRoot -WorkspaceWimSource $resolvedWorkspaceWimPath -OutputRoot $resolvedOutputRoot
    Write-ReleaseLog "PlanOnly modu tamamlandi. Gercek artifact uretimi atlandi."
    return
}

Assert-ReleaseInputsAndOutputs -ProjectRoot $projectRoot -WorkspaceWimSource $resolvedWorkspaceWimPath -OutputRoot $resolvedOutputRoot

$usbLayoutRoot = Join-Path $resolvedOutputRoot "usb-layout"
New-IsoFromDirectory -SourceDirectory $usbLayoutRoot -DestinationPath $primaryArtifactPath -VolumeName "CIGERTOOL"

$hash = Get-FileHash -LiteralPath $primaryArtifactPath -Algorithm SHA256
Set-Content -Path $hashArtifactPath -Value ($hash.Hash.ToLowerInvariant() + "  " + [System.IO.Path]::GetFileName($primaryArtifactPath)) -Encoding ascii
Write-ReleaseLog "SHA256 hash yazildi: $hashArtifactPath"

$releaseManifest = [ordered]@{
    product = "CigerTool Workspace"
    built_at = (Get-Date).ToString("o")
    source_workspace_wim = $resolvedWorkspaceWimPath
    iso_library_source = $resolvedIsoLibraryRoot
    primary_artifact = [ordered]@{
        name = [System.IO.Path]::GetFileName($primaryArtifactPath)
        path = $primaryArtifactPath
        type = "iso"
        packaging_strategy = "writable-usb-distribution-iso"
        writing_model = "USB'ye Rufus veya benzeri araclarla yazdirilabilir UEFI bootable ISO; sonrasinda /isos/* dizinleri kullanici tarafinda yazilabilir kalir."
    }
    secondary_artifacts = @(
        [ordered]@{
            name = [System.IO.Path]::GetFileName($hashArtifactPath)
            path = $hashArtifactPath
            type = "sha256"
        },
        [ordered]@{
            name = [System.IO.Path]::GetFileName($debugArtifactPath)
            path = $debugArtifactPath
            type = "debug-zip"
        }
    )
    workspace = [ordered]@{
        loader = "/EFI/Microsoft/Boot/bootmgfw.efi"
        bcd = "/EFI/Microsoft/Boot/BCD"
        vhd = "/workspace/CigerToolWorkspace.vhdx"
        marker = "/CigerTool.workspace.json"
        startup_hook = "C:\Program Files\CigerToolWorkspace\startup\Start-CigerToolWorkspace.ps1"
    }
    iso_library = [ordered]@{
        windows = "/isos/windows"
        linux = "/isos/linux"
        tools = "/isos/tools"
    }
    release_readiness = [ordered]@{
        setup_path_removed_from_workspace = $true
        oobe_questions_expected = $false
        direct_desktop_expected = $true
        cigertool_autostart_expected = $true
    }
}
$releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -Path $releaseManifestPath -Encoding utf8
Write-ReleaseLog "Release manifest yazildi: $releaseManifestPath"

New-DebugZip -ProjectRoot $projectRoot -OutputRoot $resolvedOutputRoot -ArtifactPath $debugArtifactPath -ReleaseManifestPath $releaseManifestPath

Write-ReleaseLog "Final artifact generation tamamlandi."

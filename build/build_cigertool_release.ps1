param(
    [string]$WorkspaceWimPath = "inputs\workspace\install.wim",
    [string]$OutputRoot = "build-output\workspace",
    [string]$ArtifactRoot = "artifacts",
    [string]$AppBuildRoot = "build-output\app\dist\CigerTool",
    [string]$PrimaryArtifactName = "CigerTool.iso",
    [string]$DebugArtifactName = "CigerTool-debug.zip",
    [string]$BootWimPath = "",
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

function Assert-NoDiskImages {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $diskImages = @(Get-ChildItem -LiteralPath $RootPath -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Extension -in @(".vhd", ".vhdx")
    })
    if ($diskImages.Count -gt 0) {
        throw "Release layout icinde VHD/VHDX bulunmamali: $($diskImages[0].FullName)"
    }
}

function Assert-PlanInputsAndOutputs {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$InstallWimSource,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    foreach ($entry in @(
        @{ path = $InstallWimSource; description = "Install WIM source" }
        @{ path = (Join-Path $ProjectRoot "workspace\startup\Start-CigerToolWorkspace.ps1"); description = "Startup hook" }
        @{ path = (Join-Path $OutputRoot "manifests\release-plan.json"); description = "Release plan manifest" }
        @{ path = (Join-Path $OutputRoot "manifests\CigerTool.Unattend.xml"); description = "Unattend manifest copy" }
        @{ path = (Join-Path $OutputRoot "manifests\startup\Start-CigerToolWorkspace.ps1"); description = "Startup manifest asset" }
        @{ path = (Join-Path $OutputRoot "media-root\bootmgr"); description = "Root bootmgr" }
        @{ path = (Join-Path $OutputRoot "media-root\boot\BCD"); description = "BIOS BCD" }
        @{ path = (Join-Path $OutputRoot "media-root\boot\boot.sdi"); description = "boot.sdi" }
        @{ path = (Join-Path $OutputRoot "media-root\boot\etfsboot.com"); description = "BIOS boot image" }
        @{ path = (Join-Path $OutputRoot "media-root\efi\boot\bootx64.efi"); description = "UEFI bootx64" }
        @{ path = (Join-Path $OutputRoot "media-root\efi\microsoft\boot\BCD"); description = "UEFI BCD" }
        @{ path = (Join-Path $OutputRoot "media-root\efi\microsoft\boot\efisys.bin"); description = "UEFI boot image" }
        @{ path = (Join-Path $OutputRoot "media-root\sources"); description = "sources directory" }
    )) {
        Assert-Path -PathValue $entry.path -Description $entry.description
    }

    Assert-NoDiskImages -RootPath $OutputRoot
}

function Assert-ReleaseInputsAndOutputs {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$InstallWimSource,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    Assert-PlanInputsAndOutputs -ProjectRoot $ProjectRoot -InstallWimSource $InstallWimSource -OutputRoot $OutputRoot

    foreach ($entry in @(
        @{ path = (Join-Path $OutputRoot "media-root\sources\boot.wim"); description = "boot.wim" }
        @{ path = (Join-Path $OutputRoot "media-root\sources\install.wim"); description = "install.wim" }
    )) {
        Assert-Path -PathValue $entry.path -Description $entry.description
    }
}

function Assert-LockedWorkspaceDefaults {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $unattendPath = Join-Path $ProjectRoot "workspace\unattend\CigerToolWorkspace.Unattend.xml"
    $startupPath = Join-Path $ProjectRoot "workspace\startup\Start-CigerToolWorkspace.ps1"
    $prepareScriptPath = Join-Path $ProjectRoot "build\internal\prepare_standard_release_media.ps1"

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
            throw "Locked release default dogrulamasi basarisiz. Eksik unattend token: $requiredToken"
        }
    }

    foreach ($requiredToken in @(
        "AutoAdminLogon",
        "DefaultUserName",
        "DefaultPassword",
        "ForceAutoLogon",
        "AutoLogonCount",
        "startnet.cmd",
        "Apply-CigerToolImage.cmd",
        "dism.exe /Apply-Image",
        "bcdboot W:\Windows",
        "create partition efi size=100",
        "%MEDIA_ROOT%\sources\install.wim"
    )) {
        if ($prepareScriptContent -notmatch [regex]::Escape($requiredToken)) {
            throw "Locked release default dogrulamasi basarisiz. Eksik prepare token: $requiredToken"
        }
    }

    foreach ($requiredToken in @(
        "CIGERTOOL_RUNTIME",
        "workspace-startup.log",
        "CigerTool otomatik baslatildi"
    )) {
        if ($startupContent -notmatch [regex]::Escape($requiredToken)) {
            throw "Locked release default dogrulamasi basarisiz. Eksik startup token: $requiredToken"
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

    Ensure-Directory -PathValue $stagingRoot
    try {
        foreach ($item in @($logsRoot, $manifestsRoot)) {
            if (Test-Path -LiteralPath $item) {
                Copy-Item -LiteralPath $item -Destination (Join-Path $stagingRoot (Split-Path -Path $item -Leaf)) -Recurse -Force
            }
        }

        if (Test-Path -LiteralPath $ReleaseManifestPath) {
            $targetDir = Join-Path $stagingRoot "release"
            Ensure-Directory -PathValue $targetDir
            Copy-Item -LiteralPath $ReleaseManifestPath -Destination $targetDir -Force
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

    foreach ($required in @(
        (Join-Path $SourceDirectory "boot\etfsboot.com"),
        (Join-Path $SourceDirectory "efi\microsoft\boot\efisys.bin"),
        (Join-Path $SourceDirectory "sources\boot.wim"),
        (Join-Path $SourceDirectory "sources\install.wim")
    )) {
        Assert-Path -PathValue $required -Description "Hybrid ISO girdi dosyasi"
    }

    $isoScript = Join-Path $projectRoot "build\internal\create_hybrid_iso.py"
    $output = & python $isoScript --source $SourceDirectory --output $DestinationPath --volume-name $VolumeName 2>&1 | ForEach-Object { $_.ToString() }
    foreach ($line in $output) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-ReleaseLog "ISO writer: $line"
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Hybrid ISO olusturma basarisiz oldu."
    }

    Write-ReleaseLog "Hybrid BIOS + UEFI ISO artifact olusturuldu: $DestinationPath"
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedOutputRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $OutputRoot
$resolvedArtifactRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $ArtifactRoot
$resolvedWorkspaceWimPath = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $WorkspaceWimPath
$resolvedAppBuildRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $AppBuildRoot
$releaseLogRoot = Join-Path $resolvedArtifactRoot "logs"
$primaryArtifactPath = Join-Path $resolvedArtifactRoot $PrimaryArtifactName
$debugArtifactPath = Join-Path $resolvedArtifactRoot $DebugArtifactName
$hashArtifactPath = $primaryArtifactPath + ".sha256"
$releaseManifestPath = Join-Path $resolvedArtifactRoot "CigerTool.release.json"

foreach ($path in @($resolvedArtifactRoot, $releaseLogRoot)) {
    Ensure-Directory -PathValue $path
}

$script:LogFile = Join-Path $releaseLogRoot "release-build.log"
Set-Content -Path $script:LogFile -Value ""

Write-ReleaseLog "CigerTool final artifact generation baslatiliyor."
Write-ReleaseLog "Ana build girisi: build\\build_cigertool_release.ps1"

Assert-Path -PathValue $resolvedWorkspaceWimPath -Description "Hazir install.wim girdisi (beklenen yol: inputs\\workspace\\install.wim)"
Assert-LockedWorkspaceDefaults -ProjectRoot $projectRoot

if ((-not $PlanOnly) -and (-not (Test-IsAdministrator))) {
    $identityName = Get-CurrentIdentityName
    throw "Final artifact generation elevasyon gerektirir. Mevcut Windows kimligi: $identityName. Bu build, WIM mount ve DISM tabanli image servis islemleri yaptigi icin administrator haklari gerektirir."
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
    (Join-Path $resolvedOutputRoot "usb-layout"),
    (Join-Path $resolvedOutputRoot "media-root"),
    (Join-Path $resolvedOutputRoot "mount"),
    (Join-Path $resolvedOutputRoot "work"),
    (Join-Path $resolvedOutputRoot "manifests")
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
    -BootWimPath $BootWimPath `
    -PlanOnly:$PlanOnly

if ($PlanOnly) {
    Assert-PlanInputsAndOutputs -ProjectRoot $projectRoot -InstallWimSource $resolvedWorkspaceWimPath -OutputRoot $resolvedOutputRoot
    Write-ReleaseLog "PlanOnly modu tamamlandi. Gercek artifact uretimi atlandi."
    return
}

Assert-ReleaseInputsAndOutputs -ProjectRoot $projectRoot -InstallWimSource $resolvedWorkspaceWimPath -OutputRoot $resolvedOutputRoot

$mediaRoot = Join-Path $resolvedOutputRoot "media-root"
New-IsoFromDirectory -SourceDirectory $mediaRoot -DestinationPath $primaryArtifactPath -VolumeName "CIGERTOOL"

$hash = Get-FileHash -LiteralPath $primaryArtifactPath -Algorithm SHA256
Set-Content -Path $hashArtifactPath -Value ($hash.Hash.ToLowerInvariant() + "  " + [System.IO.Path]::GetFileName($primaryArtifactPath)) -Encoding ascii
Write-ReleaseLog "SHA256 hash yazildi: $hashArtifactPath"

$isoItem = Get-Item -LiteralPath $primaryArtifactPath
$isoSizeGb = [Math]::Round($isoItem.Length / 1GB, 2)
if ($isoItem.Length -gt 3GB) {
    throw "ISO boyutu hedefi asti. Boyut=$isoSizeGb GB | hedef en fazla 3 GB"
}
Write-ReleaseLog "ISO boyutu hedef dahilinde: $isoSizeGb GB"

$releaseManifest = [ordered]@{
    product = "CigerTool"
    built_at = (Get-Date).ToString("o")
    source_install_wim = $resolvedWorkspaceWimPath
    primary_artifact = [ordered]@{
        name = [System.IO.Path]::GetFileName($primaryArtifactPath)
        path = $primaryArtifactPath
        type = "iso"
        packaging_strategy = "standard-windows-hybrid-iso"
        rufus_compatible = $true
        writing_model = "Rufus ile dogrudan USB'ye yazdirilabilir hibrit ISO."
        firmware = @("bios", "uefi")
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
    media_layout = [ordered]@{
        bootmgr = "/bootmgr"
        bios_bcd = "/boot/BCD"
        uefi_bcd = "/efi/microsoft/boot/BCD"
        boot_wim = "/sources/boot.wim"
        install_wim = "/sources/install.wim"
    }
    runtime_defaults = [ordered]@{
        oobe_expected = $false
        setup_ui_expected = $false
        autologin_user = "CigerTool"
        language = "tr-TR"
        direct_desktop_expected = $true
        cigertool_autostart_expected = $true
    }
}
$releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -Path $releaseManifestPath -Encoding utf8
Write-ReleaseLog "Release manifest yazildi: $releaseManifestPath"

New-DebugZip -ProjectRoot $projectRoot -OutputRoot $resolvedOutputRoot -ArtifactPath $debugArtifactPath -ReleaseManifestPath $releaseManifestPath

Write-ReleaseLog "Final artifact generation tamamlandi."

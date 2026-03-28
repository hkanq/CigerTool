param(
    [string]$OutputIso = "artifacts\CigerTool-by-hkannq.iso",
    [string]$AppBuildRoot = "build-output\app\dist\CigerTool",
    [switch]$RequirePrebootMenu
)

$ErrorActionPreference = "Stop"

function Write-BuildLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Write-Output $line
    Add-Content -Path $script:LogFile -Value $line
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    Write-BuildLog ("Komut: {0} {1}" -f $FilePath, ($Arguments -join " "))
    & $FilePath @Arguments 2>&1 | Tee-Object -FilePath $script:LogFile -Append | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw ("Komut basarisiz oldu ({0}): {1}" -f $LASTEXITCODE, $FilePath)
    }
}

function Assert-Path {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if (-not (Test-Path $PathValue)) {
        throw "$Description bulunamadi: $PathValue"
    }
}

function Resolve-AdkRoot {
    $candidates = @(
        $env:CIGERTOOL_ADK_ROOT,
        "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit",
        "C:\Program Files\Windows Kits\10\Assessment and Deployment Kit"
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    throw "Windows ADK bulunamadi. build\\scripts\\install_adk.ps1 ile kurulum yapin veya CIGERTOOL_ADK_ROOT tanimlayin."
}

function Find-ToolPath {
    param(
        [string]$Root,
        [string]$Name
    )
    $item = Get-ChildItem -Path $Root -Recurse -Filter $Name -File | Select-Object -First 1
    if (-not $item) {
        throw "$Name bulunamadi."
    }
    return $item.FullName
}

function Add-OptionalComponent {
    param(
        [string]$OcRoot,
        [string]$MountPath,
        [string]$Pattern
    )

    $cab = Get-ChildItem -Path $OcRoot -Filter $Pattern -File | Select-Object -First 1
    if (-not $cab) {
        Write-BuildLog "Opsiyonel paket bulunamadi ve atlandi: $Pattern" "WARN"
        return
    }

    Invoke-Native -FilePath "dism.exe" -Arguments @("/Image:$MountPath", "/Add-Package", "/PackagePath:$($cab.FullName)")
    $langCab = Get-ChildItem -Path (Join-Path $OcRoot "en-us") -Filter ($cab.BaseName + "_en-us.cab") -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($langCab) {
        Invoke-Native -FilePath "dism.exe" -Arguments @("/Image:$MountPath", "/Add-Package", "/PackagePath:$($langCab.FullName)")
    }
}

function Validate-MediaLayout {
    param(
        [string]$MediaRoot,
        [bool]$PrebootRequired
    )

    foreach ($required in @(
        (Join-Path $MediaRoot "sources\boot.wim"),
        (Join-Path $MediaRoot "EFI\Microsoft\Boot\BCD"),
        (Join-Path $MediaRoot "boot\bcd")
    )) {
        Assert-Path -PathValue $required -Description "Gerekli medya dosyasi"
    }

    if ($PrebootRequired) {
        Assert-Path -PathValue (Join-Path $MediaRoot "EFI\CigerTool\grubx64.efi") -Description "Pre-boot EFI uygulamasi"
    }
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$artifactRoot = Join-Path $projectRoot "artifacts"
$logRoot = Join-Path $artifactRoot "logs"
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$script:LogFile = Join-Path $logRoot "winpe-build.log"
Set-Content -Path $script:LogFile -Value ""

$adkRoot = Resolve-AdkRoot
$copype = Find-ToolPath -Root $adkRoot -Name "copype.cmd"
$makeMedia = Find-ToolPath -Root $adkRoot -Name "MakeWinPEMedia.cmd"
$ocRoot = Join-Path $adkRoot "Windows Preinstallation Environment\amd64\WinPE_OCs"
Assert-Path -PathValue $ocRoot -Description "WinPE optional component klasoru"

$appRoot = (Resolve-Path (Join-Path $projectRoot $AppBuildRoot)).Path
Assert-Path -PathValue $appRoot -Description "PyInstaller uygulama cikti klasoru"

$isoPath = Join-Path $projectRoot $OutputIso
$isoParent = Split-Path $isoPath -Parent
New-Item -ItemType Directory -Force -Path $isoParent | Out-Null

$workRoot = Join-Path $projectRoot "winpe-work"
$mountPath = Join-Path $workRoot "mount"
$mediaRoot = Join-Path $workRoot "media"
$mediaToolsRoot = Join-Path $mediaRoot "tools"
$mediaIsosRoot = Join-Path $mediaRoot "isos"
$mediaWindowsIsoRoot = Join-Path $mediaIsosRoot "windows"
$mediaLinuxIsoRoot = Join-Path $mediaIsosRoot "linux"
$mediaToolsIsoRoot = Join-Path $mediaIsosRoot "tools"
$mediaLegacyIsoRoot = Join-Path $mediaRoot "iso-library"
$mounted = $false
$commitChanges = $false

Write-BuildLog "ADK: $adkRoot"
Write-BuildLog "Uygulama klasoru: $appRoot"
Write-BuildLog "Cikti ISO: $isoPath"

if (Test-Path $workRoot) {
    Write-BuildLog "Eski calisma klasoru temizleniyor."
    Remove-Item -Recurse -Force $workRoot
}

Invoke-Native -FilePath "cmd.exe" -Arguments @("/c", "`"$copype`" amd64 `"$workRoot`"")

$bootWim = Join-Path $mediaRoot "sources\boot.wim"
Assert-Path -PathValue $bootWim -Description "boot.wim"
New-Item -ItemType Directory -Force -Path $mountPath | Out-Null

try {
    Invoke-Native -FilePath "dism.exe" -Arguments @("/Mount-Image", "/ImageFile:$bootWim", "/Index:1", "/MountDir:$mountPath")
    $mounted = $true

    foreach ($pattern in @(
        "WinPE-WMI*.cab",
        "WinPE-NetF*.cab",
        "WinPE-Scripting*.cab",
        "WinPE-PowerShell*.cab",
        "WinPE-StorageWMI*.cab",
        "WinPE-DismCmdlets*.cab"
    )) {
        Add-OptionalComponent -OcRoot $ocRoot -MountPath $mountPath -Pattern $pattern
    }

    $targetAppRoot = Join-Path $mountPath "CigerTool"
    New-Item -ItemType Directory -Force -Path $targetAppRoot | Out-Null
    Copy-Item -Path (Join-Path $appRoot "*") -Destination $targetAppRoot -Recurse -Force
    Copy-Item -Path (Join-Path $projectRoot "winpe\files\*") -Destination $mountPath -Recurse -Force
    $commitChanges = $true
    Write-BuildLog "WinPE image icine uygulama ve startup dosyalari kopyalandi."
}
finally {
    if ($mounted) {
        if ($commitChanges) {
            Invoke-Native -FilePath "dism.exe" -Arguments @("/Unmount-Image", "/MountDir:$mountPath", "/Commit")
        }
        else {
            Write-BuildLog "Mount edilen image degisiklik kaydetmeden kapatiliyor." "WARN"
            Invoke-Native -FilePath "dism.exe" -Arguments @("/Unmount-Image", "/MountDir:$mountPath", "/Discard")
        }
    }
}

New-Item -ItemType Directory -Force -Path $mediaToolsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $mediaWindowsIsoRoot | Out-Null
New-Item -ItemType Directory -Force -Path $mediaLinuxIsoRoot | Out-Null
New-Item -ItemType Directory -Force -Path $mediaToolsIsoRoot | Out-Null
New-Item -ItemType Directory -Force -Path $mediaLegacyIsoRoot | Out-Null

if (Test-Path (Join-Path $projectRoot "tools")) {
    Copy-Item -Path (Join-Path $projectRoot "tools\*") -Destination $mediaToolsRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-BuildLog "tools klasoru medya kokune kopyalandi."
}
if (Test-Path (Join-Path $projectRoot "isos\windows")) {
    Copy-Item -Path (Join-Path $projectRoot "isos\windows\*") -Destination $mediaWindowsIsoRoot -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path (Join-Path $projectRoot "isos\linux")) {
    Copy-Item -Path (Join-Path $projectRoot "isos\linux\*") -Destination $mediaLinuxIsoRoot -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path (Join-Path $projectRoot "isos\tools")) {
    Copy-Item -Path (Join-Path $projectRoot "isos\tools\*") -Destination $mediaToolsIsoRoot -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path (Join-Path $projectRoot "iso-library")) {
    Copy-Item -Path (Join-Path $projectRoot "iso-library\*") -Destination $mediaLegacyIsoRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-BuildLog "ISO kutuphane klasorleri medyaya kopyalandi."
if (-not (Test-Path (Join-Path $projectRoot "isos"))) {
    Write-BuildLog "Yeni /isos yapisi henuz yoksa legacy iso-library uyumlulugu korunur." "WARN"
}

$prebootBuilt = $false
try {
    $prebootBuilt = & (Join-Path $projectRoot "build\scripts\build_preboot_menu.ps1") -MediaRoot $mediaRoot -ArtifactRoot "artifacts\logs" -RequireMenu:$RequirePrebootMenu
    if ($prebootBuilt) {
        Write-BuildLog "Pre-boot ISO library menusu hazirlandi."
    }
    else {
        Write-BuildLog "Pre-boot menusu hazirlanamadi, WinPE dogrudan acilisla devam edilecek." "WARN"
    }
}
catch {
    if ($RequirePrebootMenu) {
        throw
    }
    Write-BuildLog ("Pre-boot menu hatasi: " + $_.Exception.Message) "WARN"
}

Validate-MediaLayout -MediaRoot $mediaRoot -PrebootRequired ([bool]$RequirePrebootMenu)

Invoke-Native -FilePath "cmd.exe" -Arguments @("/c", "`"$makeMedia`" /ISO `"$workRoot`" `"$isoPath`"")
Assert-Path -PathValue $isoPath -Description "ISO dosyasi"

$isoItem = Get-Item $isoPath
if ($isoItem.Length -lt 200MB) {
    throw "ISO boyutu beklenenden kucuk: $($isoItem.Length) byte"
}

$hash = Get-FileHash -Algorithm SHA256 -Path $isoPath
$hashPath = "$isoPath.sha256"
$metadataPath = "$isoPath.json"
Set-Content -Path $hashPath -Value $hash.Hash
@{
    iso = $isoItem.FullName
    size_bytes = $isoItem.Length
    sha256 = $hash.Hash
    built_at = (Get-Date).ToString("o")
    adk_root = $adkRoot
    preboot_menu = [bool]$prebootBuilt
} | ConvertTo-Json -Depth 3 | Set-Content -Path $metadataPath

Write-BuildLog "ISO dogrulandi ve hash uretildi."
Write-BuildLog "WinPE ISO hazir: $isoPath"

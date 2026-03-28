param(
    [Parameter(Mandatory = $true)][string]$MediaRoot,
    [string]$ArtifactRoot = "artifacts",
    [switch]$RequireMenu
)

$ErrorActionPreference = "Stop"

function Write-BuildLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Write-Host $line
    Add-Content -Path $script:LogFile -Value $line
}

function Convert-ToMsysPath {
    param([string]$PathValue)
    $resolved = [System.IO.Path]::GetFullPath($PathValue).Replace("\", "/")
    if ($resolved.Length -lt 3 -or $resolved[1] -ne ":") {
        return $resolved
    }
    return "/" + $resolved[0].ToString().ToLowerInvariant() + $resolved.Substring(2)
}

function Resolve-GrubBash {
    $candidates = @(
        $env:CIGERTOOL_GRUB_BASH,
        "C:\msys64\usr\bin\bash.exe",
        "C:\tools\msys64\usr\bin\bash.exe"
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    return $null
}

function Get-BcdIdentifier {
    param([string]$Text)
    $match = [regex]::Match($Text, "{[^}]+}")
    if (-not $match.Success) {
        throw "BCD girdisi olusturuldu ama kimlik ayiklanamadi: $Text"
    }
    return $match.Value
}

function Add-UefiBootMenuEntry {
    param(
        [string]$StorePath,
        [string]$EfiRelativePath
    )

    if (-not (Test-Path $StorePath)) {
        Write-BuildLog "UEFI BCD store bulunamadi, menu girdisi atlandi: $StorePath" "WARN"
        return $false
    }

    $createOutput = & bcdedit /store $StorePath /create /d "ISO Library" /application BOOTAPP 2>&1 | Out-String
    $identifier = Get-BcdIdentifier $createOutput
    & bcdedit /store $StorePath /set $identifier device boot | Out-Null
    & bcdedit /store $StorePath /set $identifier path $EfiRelativePath | Out-Null
    & bcdedit /store $StorePath /set "{bootmgr}" displaybootmenu yes | Out-Null
    & bcdedit /store $StorePath /displayorder $identifier /addlast | Out-Null
    & bcdedit /store $StorePath /timeout 8 | Out-Null
    Write-BuildLog "UEFI boot menusune ISO Library girdisi eklendi: $identifier"
    return $true
}

function Resolve-Wimboot {
    param(
        [string]$ProjectRoot,
        [string]$MediaRoot
    )
    $candidates = @(
        (Join-Path $ProjectRoot "build\assets\preboot\wimboot"),
        (Join-Path $ProjectRoot "tools\boot\wimboot"),
        (Join-Path $MediaRoot "tools\boot\wimboot")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $target = Join-Path $ProjectRoot "build\assets\preboot\wimboot"
    try {
        Invoke-WebRequest -Uri "https://github.com/ipxe/wimboot/releases/latest/download/wimboot" -OutFile $target
        Write-BuildLog "wimboot indirildi: $target"
        return $target
    } catch {
        Write-BuildLog "wimboot indirilemedi: $($_.Exception.Message)" "WARN"
        return $null
    }
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$artifactDir = Join-Path $projectRoot $ArtifactRoot
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$script:LogFile = Join-Path $artifactDir "preboot-menu.log"
Set-Content -Path $script:LogFile -Value ""

$mediaPath = (Resolve-Path $MediaRoot).Path
$efiTargetDir = Join-Path $mediaPath "EFI\CigerTool"
$efiTargetPath = Join-Path $efiTargetDir "grubx64.efi"
$efiCfgTarget = Join-Path $efiTargetDir "grub.cfg"
New-Item -ItemType Directory -Force -Path $efiTargetDir | Out-Null

$bashPath = Resolve-GrubBash
if (-not $bashPath) {
    $message = "GRUB toolchain bulunamadi. MSYS2 MINGW64 GRUB paketi kurulu olmali veya CIGERTOOL_GRUB_BASH tanimlanmali."
    if ($RequireMenu) {
        throw $message
    }
    Write-BuildLog $message "WARN"
    return $false
}

$wimbootSource = Resolve-Wimboot -ProjectRoot $projectRoot -MediaRoot $mediaPath
$wimbootTarget = Join-Path $efiTargetDir "wimboot"
$wimbootGrubPath = ""
if ($wimbootSource -and (Test-Path $wimbootSource)) {
    Copy-Item -Path $wimbootSource -Destination $wimbootTarget -Force
    $wimbootGrubPath = "/EFI/CigerTool/wimboot"
}

$renderScript = Join-Path $projectRoot "build\scripts\generate_grub_menu.py"
$generatorOutput = & python $renderScript --media-root $mediaPath --output $efiCfgTarget --wimboot-path $wimbootGrubPath 2>&1 | ForEach-Object { $_.ToString() }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $efiCfgTarget)) {
    throw "Dinamik GRUB menu uretilemedi."
}
foreach ($line in $generatorOutput) {
    if (-not [string]::IsNullOrWhiteSpace($line)) {
        Write-BuildLog "GRUB profil: $line"
    }
}
Write-BuildLog "Dinamik GRUB menu hazirlandi: $efiCfgTarget"

$tempRoot = Join-Path $env:TEMP "cigertool-preboot"
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$tempEfi = Join-Path $tempRoot "grubx64.efi"
if (Test-Path $tempEfi) {
    Remove-Item -Force $tempEfi
}

$msysCfg = Convert-ToMsysPath $efiCfgTarget
$msysOut = Convert-ToMsysPath $tempEfi
$grubCommand = "export MSYSTEM=MINGW64; export PATH=/mingw64/bin:/usr/bin:`$PATH; grub-mkstandalone -O x86_64-efi -o '$msysOut' ""boot/grub/grub.cfg=$msysCfg"""
Write-BuildLog "GRUB EFI binary olusturuluyor."
& $bashPath -lc $grubCommand
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tempEfi)) {
    throw "grub-mkstandalone basarisiz oldu."
}

Copy-Item -Path $tempEfi -Destination $efiTargetPath -Force
Write-BuildLog "GRUB EFI binary hazirlandi: $efiTargetPath"

$bcdStore = Join-Path $mediaPath "EFI\Microsoft\Boot\BCD"
$added = Add-UefiBootMenuEntry -StorePath $bcdStore -EfiRelativePath "\EFI\CigerTool\grubx64.efi"
if (-not $added -and $RequireMenu) {
    throw "UEFI boot menusu olusturulamadi."
}

return $true

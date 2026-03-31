param(
    [string]$InstallWimPath = "inputs\workspace\install.wim",
    [string]$OutputRoot = "build-output\workspace",
    [string]$AppBuildRoot = "build-output\app\dist\CigerTool",
    [string]$PayloadRoot = "workspace\payload",
    [int]$ImageIndex = 1,
    [string]$BootWimPath = "",
    [switch]$PlanOnly
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

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$PathValue)
    New-Item -ItemType Directory -Force -Path $PathValue | Out-Null
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

function Assert-Path {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $PathValue)) {
        throw "$Description bulunamadi: $PathValue"
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
    Write-BuildLog "Eski generate path temizlendi: $PathValue"
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Optional
    )

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        if ($Optional) {
            Ensure-Directory -PathValue $DestinationPath
            Write-BuildLog "$Description atlandi, kaynak mevcut degil: $SourcePath" "WARN"
            return
        }

        throw "$Description bulunamadi: $SourcePath"
    }

    Ensure-Directory -PathValue $DestinationPath
    foreach ($item in @(Get-ChildItem -LiteralPath $SourcePath -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $DestinationPath -Recurse -Force
    }
    Write-BuildLog "$Description kopyalandi | kaynak=$SourcePath | hedef=$DestinationPath"
}

function Copy-SourcesToDestination {
    param(
        [Parameter(Mandatory = $true)][string[]]$SourcePaths,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Optional
    )

    $copiedAny = $false
    foreach ($sourcePath in $SourcePaths) {
        if (-not [string]::IsNullOrWhiteSpace($sourcePath) -and (Test-Path -LiteralPath $sourcePath)) {
            Copy-DirectoryContents -SourcePath $sourcePath -DestinationPath $DestinationPath -Description $Description
            $copiedAny = $true
        }
    }

    if (-not $copiedAny) {
        if ($Optional) {
            Ensure-Directory -PathValue $DestinationPath
            Write-BuildLog "$Description atlandi, kaynak mevcut degil." "WARN"
            return
        }

        throw "$Description icin kaynak bulunamadi."
    }
}

function Copy-CigerToolSupportScripts {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DestinationScriptsRoot
    )

    Ensure-Directory -PathValue $DestinationScriptsRoot
    $scriptSourceRoot = Join-Path $ProjectRoot "cigertool\scripts"
    $copied = 0
    foreach ($script in @(Get-ChildItem -LiteralPath $scriptSourceRoot -Filter "invoke_*.ps1" -File -ErrorAction SilentlyContinue)) {
        Copy-Item -LiteralPath $script.FullName -Destination (Join-Path $DestinationScriptsRoot $script.Name) -Force
        $copied += 1
    }

    if ($copied -gt 0) {
        Write-BuildLog "CigerTool destek scriptleri kopyalandi | adet=$copied | hedef=$DestinationScriptsRoot"
    }
    else {
        Write-BuildLog "CigerTool destek scriptleri bulunamadi: $scriptSourceRoot" "WARN"
    }
}

function Ensure-RegistryKey {
    param([Parameter(Mandatory = $true)][string]$PathValue)

    if (Test-Path -LiteralPath $PathValue) {
        return
    }

    $parentPath = Split-Path -Path $PathValue -Parent
    if (-not [string]::IsNullOrWhiteSpace($parentPath) -and $parentPath -ne $PathValue) {
        Ensure-RegistryKey -PathValue $parentPath
    }

    if (-not (Test-Path -LiteralPath $PathValue)) {
        New-Item -Path $parentPath -Name (Split-Path -Path $PathValue -Leaf) -Force | Out-Null
    }
}

function Set-RegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][AllowEmptyString()][object]$Value,
        [Parameter(Mandatory = $true)][ValidateSet("String", "DWord")][string]$PropertyType
    )

    Ensure-RegistryKey -PathValue $PathValue
    New-ItemProperty -Path $PathValue -Name $Name -Value $Value -PropertyType $PropertyType -Force | Out-Null
}

function Set-ImageOfflineRegistry {
    param([Parameter(Mandatory = $true)][string]$WindowsRoot)

    $softwareHive = Join-Path $WindowsRoot "Windows\System32\config\SOFTWARE"
    $systemHive = Join-Path $WindowsRoot "Windows\System32\config\SYSTEM"
    Assert-Path -PathValue $softwareHive -Description "offline SOFTWARE hive"
    Assert-Path -PathValue $systemHive -Description "offline SYSTEM hive"
    & reg.exe load HKLM\CTWSOFT $softwareHive | Out-Null
    & reg.exe load HKLM\CTWSYS $systemHive | Out-Null
    try {
        $runCommand = 'powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\Program Files\CigerToolWorkspace\startup\Start-CigerToolWorkspace.ps1"'
        $winlogonPath = "Registry::HKEY_LOCAL_MACHINE\CTWSOFT\Microsoft\Windows NT\CurrentVersion\Winlogon"
        $runPath = "Registry::HKEY_LOCAL_MACHINE\CTWSOFT\Microsoft\Windows\CurrentVersion\Run"
        $policyPath = "Registry::HKEY_LOCAL_MACHINE\CTWSOFT\Microsoft\Windows\CurrentVersion\Policies\System"
        $cloudContentPath = "Registry::HKEY_LOCAL_MACHINE\CTWSOFT\Policies\Microsoft\Windows\CloudContent"
        $oobePath = "Registry::HKEY_LOCAL_MACHINE\CTWSOFT\Microsoft\Windows\CurrentVersion\OOBE"

        Set-RegistryValue -PathValue $winlogonPath -Name "AutoAdminLogon" -Value "1" -PropertyType String
        Set-RegistryValue -PathValue $winlogonPath -Name "DefaultUserName" -Value "CigerTool" -PropertyType String
        Set-RegistryValue -PathValue $winlogonPath -Name "DefaultPassword" -Value "" -PropertyType String
        Set-RegistryValue -PathValue $winlogonPath -Name "ForceAutoLogon" -Value "1" -PropertyType String
        Set-RegistryValue -PathValue $winlogonPath -Name "AutoLogonCount" -Value 999 -PropertyType DWord
        Set-RegistryValue -PathValue $runPath -Name "CigerToolWorkspace" -Value $runCommand -PropertyType String
        Set-RegistryValue -PathValue $policyPath -Name "EnableFirstLogonAnimation" -Value 0 -PropertyType DWord
        Set-RegistryValue -PathValue $cloudContentPath -Name "DisableConsumerFeatures" -Value 1 -PropertyType DWord
        Set-RegistryValue -PathValue $oobePath -Name "HideEULAPage" -Value 1 -PropertyType DWord
        Set-RegistryValue -PathValue $oobePath -Name "HideWirelessSetupInOOBE" -Value 1 -PropertyType DWord
        Set-RegistryValue -PathValue $oobePath -Name "SkipMachineOOBE" -Value 1 -PropertyType DWord
        Set-RegistryValue -PathValue $oobePath -Name "SkipUserOOBE" -Value 1 -PropertyType DWord
        Write-BuildLog "Offline registry autologon ve ilk acilis bastirma ayarlari uygulandi."
    }
    finally {
        & reg.exe unload HKLM\CTWSOFT | Out-Null
        & reg.exe unload HKLM\CTWSYS | Out-Null
    }
}

function Invoke-LoggedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $output = & $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    foreach ($line in $output) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            Write-BuildLog ((Split-Path -Path $FilePath -Leaf) + ": " + $line.Trim())
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Export-WimImage {
    param(
        [Parameter(Mandatory = $true)][string]$SourceImagePath,
        [Parameter(Mandatory = $true)][int]$SourceIndex,
        [Parameter(Mandatory = $true)][string]$DestinationImagePath,
        [switch]$SetBootable
    )

    if (Test-Path -LiteralPath $DestinationImagePath) {
        Remove-Item -LiteralPath $DestinationImagePath -Force
    }

    $params = @{
        SourceImagePath = $SourceImagePath
        SourceIndex = $SourceIndex
        DestinationImagePath = $DestinationImagePath
        CompressionType = "max"
        CheckIntegrity = $true
    }
    if ($SetBootable) {
        $params.SetBootable = $true
    }

    Export-WindowsImage @params | Out-Null
    Write-BuildLog "WIM export tamamlandi: $DestinationImagePath"
}

function Mount-WimImage {
    param(
        [Parameter(Mandatory = $true)][string]$ImagePath,
        [Parameter(Mandatory = $true)][string]$MountDir,
        [int]$Index = 1
    )

    Ensure-Directory -PathValue $MountDir
    Invoke-LoggedProcess -FilePath "dism.exe" -Arguments @(
        "/Mount-Image",
        "/ImageFile:$ImagePath",
        "/Index:$Index",
        "/MountDir:$MountDir"
    ) -FailureMessage "WIM mount basarisiz oldu."
}

function Dismount-WimImage {
    param(
        [Parameter(Mandatory = $true)][string]$MountDir,
        [switch]$Commit
    )

    $action = if ($Commit) { "/Commit" } else { "/Discard" }
    Invoke-LoggedProcess -FilePath "dism.exe" -Arguments @(
        "/Unmount-Image",
        "/MountDir:$MountDir",
        $action
    ) -FailureMessage "WIM unmount basarisiz oldu."
}

function Resolve-BaseBootWimPath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = [System.IO.Path]::GetFullPath($ExplicitPath)
        if (Test-Path -LiteralPath $resolved) {
            return $resolved
        }
    }

    foreach ($candidate in @(
        "C:\Windows\System32\Recovery\Winre.wim",
        "C:\Recovery\WindowsRE\Winre.wim",
        "C:\Recovery\OEM\Winre.wim"
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    try {
        $reagentOutput = & reagentc /info 2>&1 | ForEach-Object { $_.ToString() }
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in $reagentOutput) {
                if ($line -match "Windows RE location:\s*(.+)$") {
                    $location = $matches[1].Trim()
                    if (-not [string]::IsNullOrWhiteSpace($location) -and $location -ne "Not configured") {
                        $candidate = [System.IO.Path]::Combine($location, "Winre.wim")
                        if (Test-Path -LiteralPath $candidate) {
                            return $candidate
                        }
                    }
                }
            }
        }
    }
    catch {
    }

    throw "boot.wim kaynagi bulunamadi. -BootWimPath ile WinRE/boot.wim yolu belirtin."
}

function Copy-OptionalFile {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        return
    }

    Ensure-Directory -PathValue (Split-Path -Path $DestinationPath -Parent)
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
}

function Copy-StandardBootAssets {
    param(
        [Parameter(Mandatory = $true)][string]$MediaRoot,
        [switch]$SkipBootWimResolution
    )

    $pcatDvdRoot = "C:\Windows\Boot\DVD\PCAT"
    $efiDvdRoot = "C:\Windows\Boot\DVD\EFI"
    $efiDvdImage = Join-Path $efiDvdRoot "en-US\efisys_noprompt.bin"
    if (-not (Test-Path -LiteralPath $efiDvdImage)) {
        $efiDvdImage = Join-Path $efiDvdRoot "en-US\efisys.bin"
    }

    foreach ($required in @(
        "C:\Windows\Boot\PCAT\bootmgr",
        (Join-Path $pcatDvdRoot "BCD"),
        (Join-Path $pcatDvdRoot "boot.sdi"),
        (Join-Path $pcatDvdRoot "etfsboot.com"),
        "C:\Windows\Boot\EFI\bootmgfw.efi",
        (Join-Path $efiDvdRoot "BCD"),
        $efiDvdImage
    )) {
        Assert-Path -PathValue $required -Description "Standard boot asset"
    }

    Ensure-Directory -PathValue (Join-Path $MediaRoot "boot")
    Ensure-Directory -PathValue (Join-Path $MediaRoot "efi\boot")
    Ensure-Directory -PathValue (Join-Path $MediaRoot "efi\microsoft\boot")
    Ensure-Directory -PathValue (Join-Path $MediaRoot "sources")

    Copy-Item -LiteralPath "C:\Windows\Boot\PCAT\bootmgr" -Destination (Join-Path $MediaRoot "bootmgr") -Force
    Copy-Item -LiteralPath (Join-Path $pcatDvdRoot "BCD") -Destination (Join-Path $MediaRoot "boot\BCD") -Force
    Copy-Item -LiteralPath (Join-Path $pcatDvdRoot "boot.sdi") -Destination (Join-Path $MediaRoot "boot\boot.sdi") -Force
    Copy-Item -LiteralPath (Join-Path $pcatDvdRoot "etfsboot.com") -Destination (Join-Path $MediaRoot "boot\etfsboot.com") -Force
    Copy-Item -LiteralPath "C:\Windows\Boot\EFI\bootmgfw.efi" -Destination (Join-Path $MediaRoot "efi\boot\bootx64.efi") -Force
    Copy-Item -LiteralPath "C:\Windows\Boot\EFI\bootmgfw.efi" -Destination (Join-Path $MediaRoot "efi\microsoft\boot\bootmgfw.efi") -Force
    Copy-Item -LiteralPath (Join-Path $efiDvdRoot "BCD") -Destination (Join-Path $MediaRoot "efi\microsoft\boot\BCD") -Force
    Copy-Item -LiteralPath $efiDvdImage -Destination (Join-Path $MediaRoot "efi\microsoft\boot\efisys.bin") -Force

    foreach ($locale in @("tr-TR", "en-US")) {
        Copy-DirectoryContents -SourcePath (Join-Path "C:\Windows\Boot\PCAT" $locale) -DestinationPath (Join-Path $MediaRoot ("boot\" + $locale)) -Description ("PCAT locale " + $locale) -Optional
        Copy-DirectoryContents -SourcePath (Join-Path "C:\Windows\Boot\EFI" $locale) -DestinationPath (Join-Path $MediaRoot ("efi\microsoft\boot\" + $locale)) -Description ("EFI locale " + $locale) -Optional
    }

    Copy-DirectoryContents -SourcePath "C:\Windows\Boot\Fonts" -DestinationPath (Join-Path $MediaRoot "boot\fonts") -Description "Boot fonts"
    Copy-DirectoryContents -SourcePath "C:\Windows\Boot\Fonts" -DestinationPath (Join-Path $MediaRoot "efi\microsoft\boot\fonts") -Description "EFI fonts"
    Copy-DirectoryContents -SourcePath "C:\Windows\Boot\Resources" -DestinationPath (Join-Path $MediaRoot "boot\resources") -Description "Boot resources"
    Copy-DirectoryContents -SourcePath "C:\Windows\Boot\Resources" -DestinationPath (Join-Path $MediaRoot "efi\microsoft\boot\resources") -Description "EFI resources"

    foreach ($optional in @(
        @{ source = "C:\Windows\Boot\EFI\bootmgr.efi"; destination = (Join-Path $MediaRoot "efi\microsoft\boot\bootmgr.efi") }
        @{ source = "C:\Windows\Boot\EFI\memtest.efi"; destination = (Join-Path $MediaRoot "efi\microsoft\boot\memtest.efi") }
        @{ source = "C:\Windows\Boot\EFI\winsipolicy.p7b"; destination = (Join-Path $MediaRoot "efi\microsoft\boot\winsipolicy.p7b") }
        @{ source = "C:\Windows\Boot\EFI\boot.stl"; destination = (Join-Path $MediaRoot "efi\microsoft\boot\boot.stl") }
        @{ source = "C:\Windows\Boot\Resources\bootres.dll"; destination = (Join-Path $MediaRoot "efi\microsoft\boot\resources\bootres.dll") }
    )) {
        Copy-OptionalFile -SourcePath $optional.source -DestinationPath $optional.destination
    }

    Write-BuildLog "Standart Windows BIOS + UEFI boot assetleri stage edildi."
}

function Write-BootWimStartupFiles {
    param([Parameter(Mandatory = $true)][string]$BootMountRoot)

    $helperRoot = Join-Path $BootMountRoot "CigerTool"
    Ensure-Directory -PathValue $helperRoot

    $startnetPath = Join-Path $BootMountRoot "Windows\System32\startnet.cmd"
    @'
@echo off
wpeinit
call X:\CigerTool\Apply-CigerToolImage.cmd
'@ | Set-Content -Path $startnetPath -Encoding ascii

    $applyScriptPath = Join-Path $helperRoot "Apply-CigerToolImage.cmd"
    @'
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "LOG=X:\Windows\Temp\CigerTool-AutoApply.log"
echo ==== CigerTool auto apply basladi ==== > "%LOG%"

set "MEDIA_ROOT="
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
  if exist "%%D:\sources\install.wim" (
    set "MEDIA_ROOT=%%D:"
    goto media_found
  )
)

:media_found
if not defined MEDIA_ROOT (
  echo install.wim bulunamadi. >> "%LOG%"
  cmd.exe
  exit /b 1
)

wpeutil UpdateBootInfo >> "%LOG%" 2>&1
set "FIRMWARE=UEFI"
for /f "tokens=3" %%A in ('reg query HKLM\System\CurrentControlSet\Control /v PEFirmwareType 2^>nul ^| find "PEFirmwareType"') do set "FWVALUE=%%A"
if /I "%FWVALUE%"=="0x1" set "FIRMWARE=BIOS"

set "TARGET_DISK="
for /f "skip=1 tokens=1" %%D in ('wmic diskdrive where "InterfaceType!='USB'" get Index 2^>nul') do (
  if not "%%D"=="" (
    set "TARGET_DISK=%%D"
    goto disk_found
  )
)

:disk_found
if not defined TARGET_DISK set "TARGET_DISK=0"

set "DP=%TEMP%\cigertool-autoapply-diskpart.txt"
if /I "%FIRMWARE%"=="UEFI" (
  > "%DP%" echo select disk %TARGET_DISK%
  >> "%DP%" echo clean
  >> "%DP%" echo convert gpt
  >> "%DP%" echo create partition efi size=100
  >> "%DP%" echo format quick fs=fat32 label=SYSTEM
  >> "%DP%" echo assign letter=S
  >> "%DP%" echo create partition msr size=16
  >> "%DP%" echo create partition primary
  >> "%DP%" echo format quick fs=ntfs label=CigerTool
  >> "%DP%" echo assign letter=W
) else (
  > "%DP%" echo select disk %TARGET_DISK%
  >> "%DP%" echo clean
  >> "%DP%" echo convert mbr
  >> "%DP%" echo create partition primary size=550
  >> "%DP%" echo format quick fs=ntfs label=SYSTEM
  >> "%DP%" echo active
  >> "%DP%" echo assign letter=S
  >> "%DP%" echo create partition primary
  >> "%DP%" echo format quick fs=ntfs label=CigerTool
  >> "%DP%" echo assign letter=W
)

diskpart /s "%DP%" >> "%LOG%" 2>&1 || goto fail
dism.exe /Apply-Image /ImageFile:"%MEDIA_ROOT%\sources\install.wim" /Index:1 /ApplyDir:W:\ >> "%LOG%" 2>&1 || goto fail
if /I "%FIRMWARE%"=="UEFI" (
  bcdboot W:\Windows /s S: /f UEFI >> "%LOG%" 2>&1 || goto fail
) else (
  bcdboot W:\Windows /s S: /f BIOS >> "%LOG%" 2>&1 || goto fail
)

echo Auto apply tamamlandi. Sistem yeniden baslatiliyor. >> "%LOG%"
wpeutil reboot
exit /b 0

:fail
echo Auto apply basarisiz oldu. Log: %LOG% >> "%LOG%"
cmd.exe
exit /b 1
'@ | Set-Content -Path $applyScriptPath -Encoding ascii

    Write-BuildLog "boot.wim auto-apply startup dosyalari yazildi."
}

function Prepare-InstallImage {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$SourceInstallWim,
        [Parameter(Mandatory = $true)][string]$WorkImagePath,
        [Parameter(Mandatory = $true)][string]$MountRoot,
        [Parameter(Mandatory = $true)][string]$FinalInstallWim,
        [Parameter(Mandatory = $true)][string]$AppSourceRoot,
        [Parameter(Mandatory = $true)][string]$PayloadSourceRoot,
        [Parameter(Mandatory = $true)][int]$ImageIndex,
        [Parameter(Mandatory = $true)][string]$UnattendSource,
        [Parameter(Mandatory = $true)][string]$StartupSource
    )

    Export-WimImage -SourceImagePath $SourceInstallWim -SourceIndex $ImageIndex -DestinationImagePath $WorkImagePath
    Mount-WimImage -ImagePath $WorkImagePath -MountDir $MountRoot -Index 1
    $mounted = $true
    try {
        Ensure-Directory -PathValue (Join-Path $MountRoot "Windows\Panther")
        Copy-Item -LiteralPath $UnattendSource -Destination (Join-Path $MountRoot "Windows\Panther\Unattend.xml") -Force
        Copy-DirectoryContents -SourcePath $StartupSource -DestinationPath (Join-Path $MountRoot "Program Files\CigerToolWorkspace\startup") -Description "Runtime startup"

        if (Test-Path -LiteralPath $AppSourceRoot) {
            Copy-DirectoryContents -SourcePath $AppSourceRoot -DestinationPath (Join-Path $MountRoot "Program Files\CigerTool") -Description "Runtime app"
            Copy-CigerToolSupportScripts -ProjectRoot $ProjectRoot -DestinationScriptsRoot (Join-Path $MountRoot "Program Files\CigerTool\scripts")
        }

        Copy-SourcesToDestination -SourcePaths @((Join-Path $PayloadSourceRoot "Desktop")) -DestinationPath (Join-Path $MountRoot "Users\Public\Desktop") -Description "payload Desktop" -Optional
        Copy-SourcesToDestination -SourcePaths @((Join-Path $PayloadSourceRoot "ProgramFiles")) -DestinationPath (Join-Path $MountRoot "Program Files") -Description "payload ProgramFiles" -Optional
        Copy-SourcesToDestination -SourcePaths @((Join-Path $PayloadSourceRoot "Users")) -DestinationPath (Join-Path $MountRoot "Users") -Description "payload Users" -Optional

        Set-ImageOfflineRegistry -WindowsRoot $MountRoot
    }
    finally {
        if ($mounted) {
            Dismount-WimImage -MountDir $MountRoot -Commit
        }
    }

    Export-WimImage -SourceImagePath $WorkImagePath -SourceIndex 1 -DestinationImagePath $FinalInstallWim
}

function Prepare-BootImage {
    param(
        [Parameter(Mandatory = $true)][string]$BaseBootWimPath,
        [Parameter(Mandatory = $true)][string]$WorkImagePath,
        [Parameter(Mandatory = $true)][string]$MountRoot,
        [Parameter(Mandatory = $true)][string]$FinalBootWim
    )

    Export-WimImage -SourceImagePath $BaseBootWimPath -SourceIndex 1 -DestinationImagePath $WorkImagePath -SetBootable
    Mount-WimImage -ImagePath $WorkImagePath -MountDir $MountRoot -Index 1
    $mounted = $true
    try {
        Write-BootWimStartupFiles -BootMountRoot $MountRoot
    }
    finally {
        if ($mounted) {
            Dismount-WimImage -MountDir $MountRoot -Commit
        }
    }

    Export-WimImage -SourceImagePath $WorkImagePath -SourceIndex 1 -DestinationImagePath $FinalBootWim -SetBootable
}

function Write-PlanManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$InstallWimPath,
        [string]$BootWimPath
    )

    $payload = [ordered]@{
        product = "CigerTool ISO"
        source_install_wim = $InstallWimPath
        source_boot_wim = $BootWimPath
        layout = "standard-windows-iso"
        boot_mode = @("bios", "uefi")
        boot_entry = "sources/boot.wim"
        install_entry = "sources/install.wim"
        auto_deploy = $true
        direct_desktop_after_install = $true
        autologin_user = "CigerTool"
        language = "tr-TR"
    }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -Path $ManifestPath -Encoding utf8
    Write-BuildLog "Release plan manifest yazildi: $ManifestPath"
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedOutputRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $OutputRoot
$resolvedInstallWimPath = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $InstallWimPath
$resolvedAppBuildRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $AppBuildRoot
$resolvedPayloadRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $PayloadRoot
$artifactLogRoot = Join-Path $projectRoot "artifacts\logs"
Ensure-Directory -PathValue $artifactLogRoot
$script:LogFile = Join-Path $artifactLogRoot "standard-release-media.log"
Set-Content -Path $script:LogFile -Value ""

$manifestsRoot = Join-Path $resolvedOutputRoot "manifests"
$mediaRoot = Join-Path $resolvedOutputRoot "media-root"
$workRoot = Join-Path $resolvedOutputRoot "work"
$mountRoot = Join-Path $resolvedOutputRoot "mount"
$installMountRoot = Join-Path $mountRoot "install"
$bootMountRoot = Join-Path $mountRoot "boot"
$installWorkWim = Join-Path $workRoot "install-work.wim"
$bootWorkWim = Join-Path $workRoot "boot-work.wim"
$finalInstallWim = Join-Path $mediaRoot "sources\install.wim"
$finalBootWim = Join-Path $mediaRoot "sources\boot.wim"
$startupSource = Join-Path $projectRoot "workspace\startup"
$unattendSource = Join-Path $projectRoot "workspace\unattend\CigerToolWorkspace.Unattend.xml"
$planManifestPath = Join-Path $manifestsRoot "release-plan.json"
$bootWimSource = ""

foreach ($generatedPath in @($manifestsRoot, $mediaRoot, $workRoot, $mountRoot)) {
    Remove-GeneratedPath -PathValue $generatedPath -AllowedRoot $resolvedOutputRoot
}

foreach ($path in @($resolvedOutputRoot, $manifestsRoot, $mediaRoot, $workRoot, $mountRoot)) {
    Ensure-Directory -PathValue $path
}

Assert-Path -PathValue $resolvedInstallWimPath -Description "Kurulum install.wim girdisi"
Assert-Path -PathValue $resolvedAppBuildRoot -Description "Paketlenmis CigerTool uygulamasi"
Assert-Path -PathValue $startupSource -Description "Workspace startup kaynagi"
Assert-Path -PathValue $unattendSource -Description "CigerTool unattend dosyasi"

Copy-Item -LiteralPath $unattendSource -Destination (Join-Path $manifestsRoot "CigerTool.Unattend.xml") -Force
Copy-DirectoryContents -SourcePath $startupSource -DestinationPath (Join-Path $manifestsRoot "startup") -Description "Startup manifest assets"
Copy-StandardBootAssets -MediaRoot $mediaRoot

if ($PlanOnly) {
    Write-PlanManifest -ManifestPath $planManifestPath -InstallWimPath $resolvedInstallWimPath -BootWimPath $bootWimSource
    Write-BuildLog "PlanOnly modu: WIM servis ve auto-deploy boot.wim adimlari atlandi." "WARN"
    return
}

$bootWimSource = Resolve-BaseBootWimPath -ExplicitPath $BootWimPath
Write-BuildLog "boot.wim kaynagi bulundu: $bootWimSource"

Prepare-InstallImage `
    -ProjectRoot $projectRoot `
    -SourceInstallWim $resolvedInstallWimPath `
    -WorkImagePath $installWorkWim `
    -MountRoot $installMountRoot `
    -FinalInstallWim $finalInstallWim `
    -AppSourceRoot $resolvedAppBuildRoot `
    -PayloadSourceRoot $resolvedPayloadRoot `
    -ImageIndex $ImageIndex `
    -UnattendSource $unattendSource `
    -StartupSource $startupSource

Prepare-BootImage `
    -BaseBootWimPath $bootWimSource `
    -WorkImagePath $bootWorkWim `
    -MountRoot $bootMountRoot `
    -FinalBootWim $finalBootWim

Write-PlanManifest -ManifestPath $planManifestPath -InstallWimPath $resolvedInstallWimPath -BootWimPath $bootWimSource
Write-BuildLog "Standart Windows ISO media root hazirlandi: $mediaRoot"

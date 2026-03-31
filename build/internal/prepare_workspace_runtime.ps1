param(
    [string]$WorkspaceWimPath = "inputs\workspace\install.wim",
    [string]$OutputRoot = "build-output\workspace",
    [string]$AppBuildRoot = "build-output\app\dist\CigerTool",
    [string]$PayloadRoot = "workspace\payload",
    [string]$ToolsRoot = "tools",
    [string]$IsoLibraryRoot = "iso-library",
    [int]$ImageIndex = 1,
    [int]$WorkspaceSizeGB = 48,
    [string]$WorkspaceVhdName = "CigerToolWorkspace.vhdx",
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

function Assert-DriveLetterFree {
    param([Parameter(Mandatory = $true)][string]$Letter)

    if (Test-Path -LiteralPath ($Letter + ":\\")) {
        throw "Gecici surucu harfi dolu: $Letter"
    }
}

function Invoke-DiskPartScript {
    param([Parameter(Mandatory = $true)][string[]]$Lines)

    $scriptPath = Join-Path $env:TEMP ("cigertool-diskpart-" + [guid]::NewGuid().ToString("N") + ".txt")
    Set-Content -Path $scriptPath -Value ($Lines -join [Environment]::NewLine) -Encoding ascii
    try {
        $output = & diskpart /s $scriptPath 2>&1 | ForEach-Object { $_.ToString() }
        foreach ($line in $output) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                Write-BuildLog ("diskpart: " + $line.Trim())
            }
        }
        if ($LASTEXITCODE -ne 0) {
            throw "diskpart komutu basarisiz oldu."
        }
    }
    finally {
        Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-VhdMounted {
    param(
        [Parameter(Mandatory = $true)][string]$VhdPath,
        [Parameter(Mandatory = $true)][string]$DriveLetter,
        [Parameter(Mandatory = $true)][string]$Label,
        [int]$PartitionNumber = 1
    )

    if (Test-Path -LiteralPath ($DriveLetter + ":\\")) {
        return
    }

    Write-BuildLog "$Label surucusu bagli degil, yeniden baglanmaya calisiliyor: $VhdPath" "WARN"
    Invoke-DiskPartScript -Lines @(
        "select vdisk file=""$VhdPath""",
        "attach vdisk",
        "select partition $PartitionNumber",
        "assign letter=$DriveLetter noerr"
    )

    if (-not (Test-Path -LiteralPath ($DriveLetter + ":\\"))) {
        throw "$Label surucusu yeniden baglanamadi: $DriveLetter"
    }

    Write-BuildLog "$Label surucusu hazir: $DriveLetter"
}

function Assert-SufficientFreeSpace {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][UInt64]$RequiredBytes,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($PathValue))
    $drive = [System.IO.DriveInfo]::new($root)
    if (-not $drive.IsReady) {
        throw "$Description icin kullanilan surucu hazir degil: $root"
    }

    if ([UInt64]$drive.AvailableFreeSpace -lt $RequiredBytes) {
        $freeGb = [math]::Round($drive.AvailableFreeSpace / 1GB, 2)
        $requiredGb = [math]::Round($RequiredBytes / 1GB, 2)
        throw "$Description icin yetersiz bos alan. Surucu=$root | bos=$freeGb GB | gereken en az=$requiredGb GB. Daha fazla alan bosaltip tekrar deneyin."
    }

    $freeGb = [math]::Round($drive.AvailableFreeSpace / 1GB, 2)
    Write-BuildLog "$Description icin bos alan dogrulandi | surucu=$root | bos=$freeGb GB"
}

function Copy-WorkspacePayloadOverlay {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadSourceRoot,
        [Parameter(Mandatory = $true)][string]$WorkspaceWindowsRoot
    )

    Copy-SourcesToDestination -SourcePaths @(
        (Join-Path $PayloadSourceRoot "Desktop")
    ) -DestinationPath (Join-Path $WorkspaceWindowsRoot "Users\Public\Desktop") -Description "payload Desktop" -Optional

    Copy-SourcesToDestination -SourcePaths @((Join-Path $PayloadSourceRoot "ProgramFiles")) `
        -DestinationPath (Join-Path $WorkspaceWindowsRoot "Program Files") `
        -Description "payload ProgramFiles" `
        -Optional

    Copy-SourcesToDestination -SourcePaths @(
        (Join-Path $PayloadSourceRoot "Users")
    ) -DestinationPath (Join-Path $WorkspaceWindowsRoot "Users") -Description "payload Users" -Optional
}

function Copy-UsbPayloadOverlay {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadSourceRoot,
        [Parameter(Mandatory = $true)][string]$UsbLayoutRoot
    )

    Copy-SourcesToDestination -SourcePaths @(
        (Join-Path $PayloadSourceRoot "Tools")
    ) -DestinationPath (Join-Path $UsbLayoutRoot "tools") -Description "payload Tools" -Optional
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

function Set-WorkspaceOfflineRegistry {
    param([Parameter(Mandatory = $true)][string]$WorkspaceWindowsRoot)

    $softwareHive = Join-Path $WorkspaceWindowsRoot "Windows\System32\config\SOFTWARE"
    $systemHive = Join-Path $WorkspaceWindowsRoot "Windows\System32\config\SYSTEM"
    Assert-Path -PathValue $softwareHive -Description "offline SOFTWARE hive"
    Assert-Path -PathValue $systemHive -Description "offline SYSTEM hive"
    & reg.exe load HKLM\CTWSOFT $softwareHive | Out-Null
    & reg.exe load HKLM\CTWSYS $systemHive | Out-Null
    try {
        $runCommand = 'powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\Program Files\CigerToolWorkspace\startup\Start-CigerToolWorkspace.ps1"'
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_SZ /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultUserName /t REG_SZ /d CigerTool /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultPassword /t REG_SZ /d "" /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v ForceAutoLogon /t REG_SZ /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoLogonCount /t REG_DWORD /d 999 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows\CurrentVersion\Run" /v CigerToolWorkspace /t REG_SZ /d $runCommand /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows\CurrentVersion\Policies\System" /v EnableFirstLogonAnimation /t REG_DWORD /d 0 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Policies\Microsoft\Windows\CloudContent" /v DisableConsumerFeatures /t REG_DWORD /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows\CurrentVersion\OOBE" /v HideEULAPage /t REG_DWORD /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows\CurrentVersion\OOBE" /v HideWirelessSetupInOOBE /t REG_DWORD /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows\CurrentVersion\OOBE" /v SkipMachineOOBE /t REG_DWORD /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSOFT\Microsoft\Windows\CurrentVersion\OOBE" /v SkipUserOOBE /t REG_DWORD /d 1 /f | Out-Null
        & reg.exe add "HKLM\CTWSYS\ControlSet001\Control" /v PortableOperatingSystem /t REG_DWORD /d 1 /f | Out-Null
        Write-BuildLog "Offline registry workspace startup ve first-run bastirma ayarlari uygulandi."
    }
    finally {
        & reg.exe unload HKLM\CTWSOFT | Out-Null
        & reg.exe unload HKLM\CTWSYS | Out-Null
    }
}

function Get-BcdOsLoaderIdentifier {
    param([Parameter(Mandatory = $true)][string]$StorePath)

    $output = & bcdedit /store $StorePath /enum osloader 2>&1 | Out-String
    $match = [regex]::Match($output, "identifier\s+({[^}]+})")
    if (-not $match.Success) {
        throw "BCD osloader identifier ayiklanamadi."
    }
    return $match.Groups[1].Value
}

function Write-WorkspaceMarker {
    param(
        [Parameter(Mandatory = $true)][string]$UsbLayoutRoot,
        [Parameter(Mandatory = $true)][string]$WorkspaceVhdName,
        [string]$WorkspaceWimPath,
        [int]$ImageIndex
    )

    $markerPath = Join-Path $UsbLayoutRoot "CigerTool.workspace.json"
    $payload = [ordered]@{
        product = "CigerTool Workspace"
        workspace_vhd = ("workspace/" + $WorkspaceVhdName)
        boot_entry = "CigerTool Workspace"
        iso_library_root = "isos"
        locale = "tr-TR"
        region = "Turkey"
        keyboard = "Turkish"
        image_index = $ImageIndex
        source_wim = $WorkspaceWimPath
    }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -Path $markerPath -Encoding utf8
    Write-BuildLog "Workspace marker yazildi: $markerPath"
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedOutputRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $OutputRoot
$manifestRoot = Join-Path $resolvedOutputRoot "manifests"
$workspaceRoot = Join-Path $resolvedOutputRoot "workspace"
$workspaceStageRoot = Join-Path $resolvedOutputRoot "workspace-stage"
$usbLayoutRoot = Join-Path $resolvedOutputRoot "usb-layout"
$artifactLogRoot = Join-Path $projectRoot "artifacts\logs"
$payloadSourceRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $PayloadRoot
$toolsSourceRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $ToolsRoot
$isoLibrarySourceRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $IsoLibraryRoot
$appSourceRoot = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $AppBuildRoot
$workspaceWimSource = Resolve-ProjectPath -ProjectRoot $projectRoot -PathValue $WorkspaceWimPath
$unattendSource = Join-Path $projectRoot "workspace\unattend\CigerToolWorkspace.Unattend.xml"
$workspaceStartupSource = Join-Path $projectRoot "workspace\startup"
$workspaceVhdPath = Join-Path $workspaceRoot $WorkspaceVhdName
$efiVhdPath = Join-Path $workspaceRoot "CigerTool-EfiSystem.vhdx"
$usbWorkspaceRoot = Join-Path $usbLayoutRoot "workspace"
$usbWorkspaceVhdPath = Join-Path $usbWorkspaceRoot $WorkspaceVhdName

foreach ($path in @($artifactLogRoot, $resolvedOutputRoot, $manifestRoot, $workspaceRoot, $workspaceStageRoot, $usbLayoutRoot, $usbWorkspaceRoot)) {
    Ensure-Directory -PathValue $path
}

$script:LogFile = Join-Path $artifactLogRoot "workspace-build.log"
Set-Content -Path $script:LogFile -Value ""

$stageProgramFiles = Join-Path $workspaceStageRoot "Program Files"
$stageWorkspaceRuntime = Join-Path $stageProgramFiles "CigerToolWorkspace"
$stageAppRoot = Join-Path $stageProgramFiles "CigerTool"
Ensure-Directory -PathValue $stageWorkspaceRuntime
Ensure-Directory -PathValue $stageAppRoot

Assert-Path -PathValue $workspaceWimSource -Description "Hazir workspace WIM girdisi (beklenen yol: inputs\workspace\install.wim)"
Assert-Path -PathValue $unattendSource -Description "Workspace unattend"
Assert-Path -PathValue $workspaceStartupSource -Description "Workspace startup kaynak klasoru"

$workspaceWimSize = (Get-Item -LiteralPath $workspaceWimSource).Length
$requiredFreeBytes = [UInt64]([Math]::Max([double](36GB), [double]($workspaceWimSize * 3)))
if ($PlanOnly) {
    Write-BuildLog "PlanOnly modu: bos alan on kontrolu atlandi." "WARN"
}
else {
    Assert-SufficientFreeSpace -PathValue $resolvedOutputRoot -RequiredBytes $requiredFreeBytes -Description "Workspace release build"
}

Copy-DirectoryContents -SourcePath $workspaceStartupSource -DestinationPath (Join-Path $stageWorkspaceRuntime "startup") -Description "workspace startup stage"
Copy-Item -LiteralPath $unattendSource -Destination (Join-Path $manifestRoot "CigerToolWorkspace.Unattend.xml") -Force
Write-BuildLog "Unattend manifest kopyalandi."

if (Test-Path -LiteralPath $appSourceRoot) {
    Copy-DirectoryContents -SourcePath $appSourceRoot -DestinationPath $stageAppRoot -Description "CigerTool app stage"
    Copy-CigerToolSupportScripts -ProjectRoot $projectRoot -DestinationScriptsRoot (Join-Path $stageAppRoot "scripts")
} elseif (-not $PlanOnly) {
    throw "Paketlenmis CigerTool uygulamasi bulunamadi: $appSourceRoot"
} else {
    Write-BuildLog "PlanOnly modunda uygulama paketi bulunamadi, stage atlandi: $appSourceRoot" "WARN"
}

Copy-WorkspacePayloadOverlay -PayloadSourceRoot $payloadSourceRoot -WorkspaceWindowsRoot $workspaceStageRoot
Copy-DirectoryContents -SourcePath $toolsSourceRoot -DestinationPath (Join-Path $usbLayoutRoot "tools") -Description "USB tools stage" -Optional
Copy-UsbPayloadOverlay -PayloadSourceRoot $payloadSourceRoot -UsbLayoutRoot $usbLayoutRoot
Copy-DirectoryContents -SourcePath $isoLibrarySourceRoot -DestinationPath (Join-Path $usbLayoutRoot "isos") -Description "USB ISO Library stage" -Optional
foreach ($section in @("windows", "linux", "tools")) {
    Ensure-Directory -PathValue (Join-Path $usbLayoutRoot ("isos\" + $section))
}

Write-WorkspaceMarker -UsbLayoutRoot $usbLayoutRoot -WorkspaceVhdName $WorkspaceVhdName -WorkspaceWimPath $workspaceWimSource -ImageIndex $ImageIndex

$planManifest = [ordered]@{
    product = "CigerTool Workspace"
    workspace_wim_path = $workspaceWimSource
    strategy = [ordered]@{
        source_format = "prepared-wim"
        runtime_format = "native-boot-vhdx"
        boot_model = "uefi-bootmgr-chainload"
        rationale = "WIM kaynak snapshot olarak kalir; VHDX yazilabilir masaustu runtime ve BCD hedefi saglar."
    }
    image_index = $ImageIndex
    workspace_vhd = $workspaceVhdPath
    workspace_size_gb = $WorkspaceSizeGB
    locale = [ordered]@{
        language = "tr-TR"
        region = "Turkey"
        keyboard = "tr-TR"
        timezone = "Turkey Standard Time"
    }
    startup = [ordered]@{
        autologon_user = "CigerTool"
        run_key_script = "C:\Program Files\CigerToolWorkspace\startup\Start-CigerToolWorkspace.ps1"
    }
    build_layout = [ordered]@{
        workspace_stage_root = $workspaceStageRoot
        workspace_runtime_stage = $stageWorkspaceRuntime
        app_runtime_stage = $stageAppRoot
        workspace_disk_root = $workspaceRoot
        usb_layout_root = $usbLayoutRoot
    }
    payload_contract = [ordered]@{
        desktop = (Join-Path $payloadSourceRoot "Desktop")
        program_files = (Join-Path $payloadSourceRoot "ProgramFiles")
        tools = (Join-Path $payloadSourceRoot "Tools")
        users = (Join-Path $payloadSourceRoot "Users")
    }
    usb_layout = [ordered]@{
        workspace_vhd = $usbWorkspaceVhdPath
        marker = (Join-Path $usbLayoutRoot "CigerTool.workspace.json")
        tools_root = (Join-Path $usbLayoutRoot "tools")
        isos_root = (Join-Path $usbLayoutRoot "isos")
        iso_library_source = $isoLibrarySourceRoot
    }
}
$planManifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $manifestRoot "workspace-plan.json") -Encoding utf8
Write-BuildLog "Workspace plan manifest yazildi."

if ($PlanOnly) {
    Write-BuildLog "PlanOnly modu: Windows image apply ve BCD hazirlama adimlari atlandi." "WARN"
    return
}

Assert-DriveLetterFree -Letter "W"
Assert-DriveLetterFree -Letter "S"

try {
    $installImage = $workspaceWimSource
    Write-BuildLog "Workspace WIM kaynagi bulundu: $installImage"

    Invoke-DiskPartScript -Lines @(
        "create vdisk file=""$workspaceVhdPath"" maximum=$($WorkspaceSizeGB * 1024) type=expandable",
        "select vdisk file=""$workspaceVhdPath""",
        "attach vdisk",
        "convert gpt noerr",
        "create partition primary",
        "format quick fs=ntfs label=""CigerTool""",
        "assign letter=W"
    )

    & dism.exe /Apply-Image /ImageFile:$installImage /Index:$ImageIndex /ApplyDir:W:\ | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "DISM image apply basarisiz oldu."
    }
    Write-BuildLog "Windows image workspace VHDX icine uygulandi."

    & dism.exe /Image:W:\ /Set-AllIntl:tr-TR | Out-Null
    & dism.exe /Image:W:\ /Set-TimeZone:"Turkey Standard Time" | Out-Null
    Write-BuildLog "Offline locale ve timezone ayarlari uygulandi."

    Ensure-VhdMounted -VhdPath $workspaceVhdPath -DriveLetter "W" -Label "Workspace VHD"
    Copy-Item -LiteralPath $unattendSource -Destination "W:\Windows\Panther\Unattend.xml" -Force
    Copy-DirectoryContents -SourcePath $workspaceStartupSource -DestinationPath "W:\Program Files\CigerToolWorkspace\startup" -Description "workspace startup runtime"
    if (Test-Path -LiteralPath $appSourceRoot) {
        Copy-DirectoryContents -SourcePath $appSourceRoot -DestinationPath "W:\Program Files\CigerTool" -Description "workspace app runtime"
        Copy-CigerToolSupportScripts -ProjectRoot $projectRoot -DestinationScriptsRoot "W:\Program Files\CigerTool\scripts"
    }

    Copy-WorkspacePayloadOverlay -PayloadSourceRoot $payloadSourceRoot -WorkspaceWindowsRoot "W:\"
    Set-WorkspaceOfflineRegistry -WorkspaceWindowsRoot "W:\"

    Invoke-DiskPartScript -Lines @(
        "create vdisk file=""$efiVhdPath"" maximum=256 type=fixed",
        "select vdisk file=""$efiVhdPath""",
        "attach vdisk",
        "convert gpt noerr",
        "create partition efi size=128",
        "format quick fs=fat32 label=""SYSTEM""",
        "assign letter=S"
    )

    Ensure-VhdMounted -VhdPath $workspaceVhdPath -DriveLetter "W" -Label "Workspace VHD"
    Ensure-VhdMounted -VhdPath $efiVhdPath -DriveLetter "S" -Label "EFI VHD"
    & bcdboot W:\Windows /s S: /f UEFI /d | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "bcdboot basarisiz oldu."
    }

    $bcdStore = "S:\EFI\Microsoft\Boot\BCD"
    $loaderId = Get-BcdOsLoaderIdentifier -StorePath $bcdStore
    & bcdedit /store $bcdStore /set $loaderId description "CigerTool Workspace" | Out-Null
    & bcdedit /store $bcdStore /set $loaderId device "vhd=[locate]\workspace\$WorkspaceVhdName" | Out-Null
    & bcdedit /store $bcdStore /set $loaderId osdevice "vhd=[locate]\workspace\$WorkspaceVhdName" | Out-Null
    & bcdedit /store $bcdStore /set $loaderId systemroot \Windows | Out-Null
    & bcdedit /store $bcdStore /set $loaderId detecthal on | Out-Null
    & bcdedit /store $bcdStore /set "{bootmgr}" default $loaderId | Out-Null
    & bcdedit /store $bcdStore /set "{bootmgr}" timeout 3 | Out-Null
    Write-BuildLog "BCD store workspace VHDX native boot icin guncellendi."

    Ensure-VhdMounted -VhdPath $efiVhdPath -DriveLetter "S" -Label "EFI VHD"
    Ensure-Directory -PathValue (Join-Path $usbLayoutRoot "EFI")
    Ensure-Directory -PathValue (Join-Path $usbLayoutRoot "Boot")
    Copy-DirectoryContents -SourcePath "S:\EFI" -DestinationPath (Join-Path $usbLayoutRoot "EFI") -Description "USB EFI layout"
    if (Test-Path -LiteralPath "S:\Boot") {
        Copy-DirectoryContents -SourcePath "S:\Boot" -DestinationPath (Join-Path $usbLayoutRoot "Boot") -Description "USB Boot layout" -Optional
    }
    Copy-Item -LiteralPath $workspaceVhdPath -Destination $usbWorkspaceVhdPath -Force
    Write-BuildLog "Workspace VHDX USB layout altina kopyalandi: $usbWorkspaceVhdPath"
}
finally {
    foreach ($diskPath in @($workspaceVhdPath, (Join-Path $workspaceRoot "CigerTool-EfiSystem.vhdx"))) {
        if (Test-Path -LiteralPath $diskPath) {
            try {
                Invoke-DiskPartScript -Lines @(
                    "select vdisk file=""$diskPath""",
                    "detach vdisk"
                )
            }
            catch {
                Write-BuildLog "VHD detach uyarisi: $diskPath | $($_.Exception.Message)" "WARN"
            }
        }
    }
}

Write-BuildLog "Workspace OS hazirlama akisi tamamlandi: $resolvedOutputRoot"

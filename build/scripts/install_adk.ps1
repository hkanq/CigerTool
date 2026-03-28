param(
    [string]$AdkUrl = "https://go.microsoft.com/fwlink/?linkid=2289980",
    [string]$WinPeUrl = "https://go.microsoft.com/fwlink/?linkid=2289981",
    [string]$InstallRoot = "C:\Program Files (x86)\Windows Kits\10"
)

$ErrorActionPreference = "Stop"

function Write-InstallLog {
    param([string]$Message)
    $line = "{0} [INFO] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-Output $line
    Add-Content -Path $script:LogFile -Value $line
}

function Find-RequiredTool {
    param(
        [string]$Root,
        [string]$Name
    )
    Get-ChildItem -Path $Root -Recurse -Filter $Name -File -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-AdkInstalled {
    param([string]$Root)
    if (-not (Test-Path $Root)) {
        return $false
    }
    $copype = Find-RequiredTool -Root $Root -Name "copype.cmd"
    $makeMedia = Find-RequiredTool -Root $Root -Name "MakeWinPEMedia.cmd"
    return $null -ne $copype -and $null -ne $makeMedia
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$logRoot = Join-Path $projectRoot "artifacts\logs"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$script:LogFile = Join-Path $logRoot "adk-install.log"
Set-Content -Path $script:LogFile -Value ""

if (Test-AdkInstalled -Root $InstallRoot) {
    Write-InstallLog "ADK ve WinPE Add-on zaten kurulu, kurulum atlandi."
    return
}

$tempBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$downloadRoot = Join-Path $tempBase "cigertool-adk"
New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

$adkSetup = Join-Path $downloadRoot "adksetup.exe"
$winpeSetup = Join-Path $downloadRoot "adkwinpesetup.exe"

Write-InstallLog "ADK indiriliyor."
Invoke-WebRequest -Uri $AdkUrl -OutFile $adkSetup
Write-InstallLog "WinPE Add-on indiriliyor."
Invoke-WebRequest -Uri $WinPeUrl -OutFile $winpeSetup

Write-InstallLog "ADK kurulumu baslatiliyor."
Start-Process -FilePath $adkSetup -ArgumentList "/quiet /norestart /ceip off /features OptionId.DeploymentTools /installpath `"$InstallRoot`"" -Wait -NoNewWindow

Write-InstallLog "WinPE Add-on kurulumu baslatiliyor."
Start-Process -FilePath $winpeSetup -ArgumentList "/quiet /norestart /ceip off /features OptionId.WindowsPreinstallationEnvironment /installpath `"$InstallRoot`"" -Wait -NoNewWindow

if (-not (Test-AdkInstalled -Root $InstallRoot)) {
    throw "ADK kurulumu tamamlandi ancak copype.cmd veya MakeWinPEMedia.cmd bulunamadi."
}

Write-InstallLog "ADK ve WinPE Add-on kuruldu ve dogrulandi."

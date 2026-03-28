param(
    [Parameter(Mandatory = $true)][int]$SourceDisk,
    [Parameter(Mandatory = $true)][int]$TargetDisk,
    [Parameter(Mandatory = $true)][ValidateSet("smart", "system")][string]$CloneMode
)

$ErrorActionPreference = "Stop"

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Yonetici yetkisi gerekli."
    }
}

function Get-FreeDriveLetter {
    $used = (Get-Volume | Where-Object DriveLetter | Select-Object -ExpandProperty DriveLetter)
    foreach ($letter in @("R","S","T","U","V","W","Y","Z")) {
        if ($used -notcontains $letter) {
            return $letter
        }
    }
    throw "Bos surucu harfi bulunamadi."
}

function Ensure-PartitionLetter {
    param([Microsoft.Management.Infrastructure.CimInstance]$Partition)
    if ($Partition.DriveLetter) {
        return $Partition.DriveLetter
    }
    $letter = Get-FreeDriveLetter
    Add-PartitionAccessPath -DiskNumber $Partition.DiskNumber -PartitionNumber $Partition.PartitionNumber -AccessPath "$letter`:\"
    return $letter
}

function Get-VolumeUsage {
    param([string]$DriveLetter)
    $volume = Get-Volume -DriveLetter $DriveLetter
    return @{
        Size = [int64]$volume.Size
        Free = [int64]$volume.SizeRemaining
        Used = [int64]($volume.Size - $volume.SizeRemaining)
        FileSystem = $volume.FileSystem
        Label = $volume.FileSystemLabel
    }
}

function Invoke-FileClone {
    param(
        [string]$Source,
        [string]$Target,
        [switch]$IsWindows
    )
    $extra = @("/COPY:DATSOU", "/DCOPY:DAT", "/R:1", "/W:1", "/XJ", "/MT:16", "/MIR")
    if ($IsWindows) {
        $extra += @("/XF", "pagefile.sys", "hiberfil.sys", "swapfile.sys")
    }
    $cmd = @("robocopy", "$Source`:\", "$Target`:\") + $extra
    & $cmd[0] $cmd[1] $cmd[2] $cmd[3..($cmd.Length - 1)]
    $exitCode = $LASTEXITCODE
    if ($exitCode -gt 7) {
        throw "Robocopy basarisiz oldu: $exitCode"
    }
}

Assert-Admin

$source = Get-Disk -Number $SourceDisk
$target = Get-Disk -Number $TargetDisk
if ($source.Number -eq $target.Number) {
    throw "Kaynak ve hedef disk ayni olamaz."
}

$sourcePartitions = Get-Partition -DiskNumber $SourceDisk | Sort-Object PartitionNumber
$windowsPartition = $null
$efiPartition = $null
$recoveryPartition = $null
$dataPartitions = @()

foreach ($partition in $sourcePartitions) {
    if ($partition.GptType -eq "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}") {
        $efiPartition = $partition
        continue
    }
    if ($partition.GptType -eq "{DE94BBA4-06D1-4D40-A16A-BFD50179D6AC}") {
        $recoveryPartition = $partition
        continue
    }

    $letter = $null
    try {
        $letter = Ensure-PartitionLetter $partition
    } catch {
        continue
    }

    if (Test-Path "$letter`:\Windows\System32\config\SYSTEM") {
        $windowsPartition = $partition
        continue
    }

    if ($CloneMode -eq "smart") {
        $dataPartitions += $partition
    }
}

if (-not $windowsPartition) {
    throw "Windows bolumu tespit edilemedi."
}

$windowsLetter = Ensure-PartitionLetter $windowsPartition
$windowsUsage = Get-VolumeUsage $windowsLetter

$planned = @()
$planned += [pscustomobject]@{
    Name = "EFI"
    Size = if ($efiPartition) { [math]::Max([int64]$efiPartition.Size, 260MB) } else { 260MB }
    FileSystem = "FAT32"
    Type = "EFI"
    SourceLetter = if ($efiPartition) { Ensure-PartitionLetter $efiPartition } else { $null }
    IsWindows = $false
}
$planned += [pscustomobject]@{
    Name = "MSR"
    Size = 16MB
    FileSystem = ""
    Type = "MSR"
    SourceLetter = $null
    IsWindows = $false
}
$planned += [pscustomobject]@{
    Name = "Windows"
    Size = [math]::Min([int64]$windowsPartition.Size, [math]::Max([int64]($windowsUsage.Used * 1.15), 45GB))
    FileSystem = "NTFS"
    Type = "WINDOWS"
    SourceLetter = $windowsLetter
    IsWindows = $true
}

if ($CloneMode -eq "smart") {
    foreach ($partition in $dataPartitions) {
        $letter = Ensure-PartitionLetter $partition
        $usage = Get-VolumeUsage $letter
        $planned += [pscustomobject]@{
            Name = "Data-$letter"
            Size = [math]::Min([int64]$partition.Size, [math]::Max([int64]($usage.Used * 1.15), 4GB))
            FileSystem = if ($usage.FileSystem) { $usage.FileSystem } else { "NTFS" }
            Type = "DATA"
            SourceLetter = $letter
            IsWindows = $false
        }
    }
}

if ($recoveryPartition) {
    $planned += [pscustomobject]@{
        Name = "Recovery"
        Size = [math]::Max([int64]$recoveryPartition.Size, 900MB)
        FileSystem = "NTFS"
        Type = "RECOVERY"
        SourceLetter = $null
        IsWindows = $false
    }
}

$requiredBytes = ($planned | Measure-Object -Property Size -Sum).Sum
if ([int64]$target.Size -lt $requiredBytes) {
    throw "Hedef disk yetersiz. Gerekli alan: $([math]::Round($requiredBytes / 1GB, 2)) GB"
}

$remainingBytes = [int64]$target.Size - [int64]$requiredBytes
if ($remainingBytes -gt 1GB) {
    $expandable = $planned | Where-Object { $_.Type -in @("DATA", "WINDOWS") } | Select-Object -Last 1
    if ($expandable) {
        $expandable.Size = [int64]$expandable.Size + $remainingBytes
    }
}

Set-Disk -Number $TargetDisk -IsReadOnly $false -ErrorAction SilentlyContinue | Out-Null
Set-Disk -Number $TargetDisk -IsOffline $false -ErrorAction SilentlyContinue | Out-Null
Clear-Disk -Number $TargetDisk -RemoveData -Confirm:$false
Initialize-Disk -Number $TargetDisk -PartitionStyle GPT

$targetWindowsLetter = $null
$targetEfiLetter = $null

foreach ($entry in $planned) {
    switch ($entry.Type) {
        "EFI" {
            $part = New-Partition -DiskNumber $TargetDisk -Size $entry.Size -GptType "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}" -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem FAT32 -NewFileSystemLabel "SYSTEM" -Confirm:$false | Out-Null
            $targetEfiLetter = (Get-Partition -DiskNumber $TargetDisk -PartitionNumber $part.PartitionNumber).DriveLetter
            if ($entry.SourceLetter) {
                Invoke-FileClone -Source $entry.SourceLetter -Target $targetEfiLetter
            }
        }
        "MSR" {
            New-Partition -DiskNumber $TargetDisk -Size $entry.Size -GptType "{E3C9E316-0B5C-4DB8-817D-F92DF00215AE}" | Out-Null
        }
        "RECOVERY" {
            $part = New-Partition -DiskNumber $TargetDisk -Size $entry.Size -GptType "{DE94BBA4-06D1-4D40-A16A-BFD50179D6AC}" -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel $entry.Name -Confirm:$false | Out-Null
        }
        default {
            $part = New-Partition -DiskNumber $TargetDisk -Size $entry.Size -GptType "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}" -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem $entry.FileSystem -NewFileSystemLabel $entry.Name -Confirm:$false | Out-Null
            $targetLetter = (Get-Partition -DiskNumber $TargetDisk -PartitionNumber $part.PartitionNumber).DriveLetter
            if ($entry.SourceLetter) {
                Invoke-FileClone -Source $entry.SourceLetter -Target $targetLetter -IsWindows:$entry.IsWindows
            }
            if ($entry.Type -eq "WINDOWS") {
                $targetWindowsLetter = $targetLetter
            }
        }
    }
}

if (-not $targetWindowsLetter -or -not $targetEfiLetter) {
    throw "Hedef Windows veya EFI bolumu olusturulamadi."
}

& bcdboot "$targetWindowsLetter`:\Windows" /s "$targetEfiLetter`:" /f ALL
Write-Output "Clone tamamlandi. Boot kaydi hazirlandi."

param(
    [Parameter(Mandatory = $true)][int]$SourceDisk,
    [Parameter(Mandatory = $true)][int]$TargetDisk
)

$ErrorActionPreference = "Stop"

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Yonetici yetkisi gerekli."
    }
}

Assert-Admin

$source = Get-Disk -Number $SourceDisk
$target = Get-Disk -Number $TargetDisk
if ($source.Number -eq $target.Number) {
    throw "Kaynak ve hedef disk ayni olamaz."
}
if ($target.Size -lt $source.Size) {
    throw "RAW clone icin hedef disk kaynak diskten kucuk olamaz."
}

Set-Disk -Number $TargetDisk -IsReadOnly $false -ErrorAction SilentlyContinue | Out-Null
Set-Disk -Number $TargetDisk -IsOffline $true -ErrorAction SilentlyContinue | Out-Null

$sourcePath = "\\.\PhysicalDrive$SourceDisk"
$targetPath = "\\.\PhysicalDrive$TargetDisk"
$bufferSize = 4MB
$buffer = New-Object byte[] $bufferSize

$sourceStream = [System.IO.File]::Open($sourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$targetStream = [System.IO.File]::Open($targetPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)

try {
    $totalBytes = [int64]$source.Size
    $writtenBytes = [int64]0
    while ($true) {
        $read = $sourceStream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) {
            break
        }
        $targetStream.Write($buffer, 0, $read)
        $writtenBytes += $read
        $pct = [math]::Round(($writtenBytes / $totalBytes) * 100, 2)
        Write-Output ("RAW clone ilerliyor: {0}% ({1}/{2} GB)" -f $pct, [math]::Round($writtenBytes / 1GB, 2), [math]::Round($totalBytes / 1GB, 2))
    }
    $targetStream.Flush()
}
finally {
    $targetStream.Close()
    $sourceStream.Close()
    Set-Disk -Number $TargetDisk -IsOffline $false -ErrorAction SilentlyContinue | Out-Null
}

Write-Output "RAW clone tamamlandi."


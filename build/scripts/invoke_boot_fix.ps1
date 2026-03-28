param(
    [Parameter(Mandatory = $true)][string]$WindowsDrive,
    [int]$EfiDisk = -1,
    [int]$MbrDisk = -1
)

$ErrorActionPreference = "Stop"

function Get-OrAssignDriveLetter {
    param([Microsoft.Management.Infrastructure.CimInstance]$Partition)

    if ($Partition.DriveLetter) {
        return $Partition.DriveLetter
    }

    $letters = @("S","T","U","V","W","Y","Z")
    foreach ($letter in $letters) {
        try {
            Add-PartitionAccessPath -DiskNumber $Partition.DiskNumber -PartitionNumber $Partition.PartitionNumber -AccessPath "$letter`:\"
            return $letter
        } catch {
        }
    }

    throw "Bos surucu harfi atanamadi."
}

if ($EfiDisk -ge 0) {
    $efi = Get-Partition -DiskNumber $EfiDisk | Where-Object { $_.GptType -eq "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}" } | Select-Object -First 1
    if (-not $efi) {
        throw "EFI bolumu bulunamadi."
    }
    $efiLetter = Get-OrAssignDriveLetter $efi
    & bcdboot "$WindowsDrive`:\Windows" /s "$efiLetter`:" /f ALL
    Write-Output "UEFI boot kaydi yenilendi."
    exit 0
}

if ($MbrDisk -ge 0) {
    $active = Get-Partition -DiskNumber $MbrDisk | Where-Object { $_.IsActive -eq $true } | Select-Object -First 1
    if (-not $active) {
        $active = Get-Partition -DiskNumber $MbrDisk | Select-Object -First 1
        Set-Partition -DiskNumber $active.DiskNumber -PartitionNumber $active.PartitionNumber -IsActive $true
    }
    $systemLetter = Get-OrAssignDriveLetter $active
    & bcdboot "$WindowsDrive`:\Windows" /s "$systemLetter`:" /f BIOS
    & bootsect /nt60 SYS /mbr
    Write-Output "MBR boot kaydi yenilendi."
    exit 0
}

throw "EFI veya MBR hedefi belirtilmeli."


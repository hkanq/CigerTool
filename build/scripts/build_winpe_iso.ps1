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
    Write-Host $line
    Add-Content -Path $script:LogFile -Value $line
}

function Normalize-ExternalOutputLine {
    param(
        [AllowNull()][object]$Value
    )

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrEmpty($text)) {
        return ""
    }

    return $text.Replace([string][char]0, "").TrimEnd("`r", "`n")
}

function Read-ExternalOutputFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) {
        return @()
    }

    $text = if ($bytes.Length -ge 2 -and $bytes[1] -eq 0) {
        [System.Text.Encoding]::Unicode.GetString($bytes)
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0) {
        [System.Text.Encoding]::BigEndianUnicode.GetString($bytes)
    }
    else {
        [System.Text.Encoding]::UTF8.GetString($bytes)
    }

    $normalizedText = $text.Replace([string][char]0, "")
    if ([string]::IsNullOrWhiteSpace($normalizedText)) {
        return @()
    }

    return @(
        $normalizedText -split "`r?`n" |
        ForEach-Object { Normalize-ExternalOutputLine $_ } |
        Where-Object { $_ -ne $null -and $_ -ne "" }
    )
}

function Test-IsLikelyMsysPath {
    param(
        [AllowNull()][string]$PathValue
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $false
    }

    $candidate = $PathValue.Trim()
    if (-not $candidate.StartsWith("/")) {
        return $false
    }
    if ($candidate -match 'declare\s+-x') {
        return $false
    }
    if ($candidate -match '=') {
        return $false
    }
    if ($candidate -match '[\"\'']') {
        return $false
    }

    return $true
}

function Assert-ValidMsysPathValue {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-IsLikelyMsysPath -PathValue $PathValue)) {
        throw ("Invalid MSYS path conversion result for {0}: {1}" -f $Label, $PathValue)
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    Write-BuildLog ("Komut: {0} {1}" -f $FilePath, ($Arguments -join " "))
    if ([System.IO.Path]::GetFileName($FilePath).Equals("dism.exe", [System.StringComparison]::OrdinalIgnoreCase)) {
        $imageTarget = $Arguments | Where-Object { $_ -like "/Image:*" } | Select-Object -First 1
        $mountTarget = $Arguments | Where-Object { $_ -like "/MountDir:*" } | Select-Object -First 1
        $imageFile = $Arguments | Where-Object { $_ -like "/ImageFile:*" } | Select-Object -First 1
        if ($imageTarget -or $mountTarget -or $imageFile) {
            $imageValue = if ($imageTarget) { $imageTarget } else { "-" }
            $mountValue = if ($mountTarget) { $mountTarget } else { "-" }
            $imageFileValue = if ($imageFile) { $imageFile } else { "-" }
            Write-BuildLog (
                "DISM hedefi: mounted image | image={0} | mount={1} | imagefile={2}" -f
                $imageValue,
                $mountValue,
                $imageFileValue
            )
        }
        else {
            Write-BuildLog "DISM hedefi: host"
        }
    }
    & $FilePath @Arguments 2>&1 | Tee-Object -FilePath $script:LogFile -Append | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw ("Komut basarisiz oldu ({0}): {1}" -f $LASTEXITCODE, $FilePath)
    }
}

function Invoke-CmdBatchFile {
    param(
        [Parameter(Mandatory = $true)][string]$BatchPath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory
    )

    Write-BuildLog ("Batch arac yolu: {0}" -f $BatchPath)
    $batchExists = Test-Path -LiteralPath $BatchPath -PathType Leaf
    Write-BuildLog ("Batch arac mevcut mu: {0}" -f $batchExists)
    if (-not $batchExists) {
        throw "Batch araci bulunamadi: $BatchPath"
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        Write-BuildLog ("Batch calisma klasoru: {0}" -f $WorkingDirectory)
        New-Item -ItemType Directory -Force -Path $WorkingDirectory | Out-Null
    }

    $quotedArgs = foreach ($arg in $Arguments) {
        if ($null -eq $arg) {
            '""'
        }
        elseif ($arg.IndexOfAny([char[]]@(' ', '"', '&', '(', ')')) -ge 0) {
            '"' + $arg.Replace('"', '""') + '"'
        }
        else {
            $arg
        }
    }

    $commandText = '"' + $BatchPath.Replace('"', '""') + '"'
    if ($quotedArgs.Count -gt 0) {
        $commandText += " " + ($quotedArgs -join " ")
    }

    $cmdArgument = '"' + $commandText + '"'
    Write-BuildLog ("cmd.exe /d /s /c {0}" -f $cmdArgument)

    $stdoutFile = Join-Path $env:TEMP ("cigertool-batch-{0}.stdout.log" -f ([guid]::NewGuid().ToString("N")))
    $stderrFile = Join-Path $env:TEMP ("cigertool-batch-{0}.stderr.log" -f ([guid]::NewGuid().ToString("N")))

    try {
        $process = Start-Process -FilePath "cmd.exe" `
            -ArgumentList @("/d", "/s", "/c", $cmdArgument) `
            -WorkingDirectory $WorkingDirectory `
            -Wait `
            -PassThru `
            -NoNewWindow `
            -RedirectStandardOutput $stdoutFile `
            -RedirectStandardError $stderrFile

        foreach ($capturedFile in @($stdoutFile, $stderrFile)) {
            if (Test-Path -LiteralPath $capturedFile) {
                Get-Content -LiteralPath $capturedFile | ForEach-Object {
                    if (-not [string]::IsNullOrWhiteSpace($_)) {
                        Write-BuildLog $_
                    }
                }
            }
        }

        if ($process.ExitCode -ne 0) {
            throw ("Batch komutu basarisiz oldu ({0}): {1} | cmd.exe /d /s /c {2}" -f $process.ExitCode, $BatchPath, $cmdArgument)
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile, $stderrFile -Force -ErrorAction SilentlyContinue
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

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-BuildLog ("Kopya hazirlaniyor | {0} | kaynak={1} | hedef={2}" -f $Description, $SourcePath, $DestinationPath)
    try {
        Assert-Path -PathValue $SourcePath -Description $Description
        New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null
        $items = @(Get-ChildItem -LiteralPath $SourcePath -Force -ErrorAction Stop)
        foreach ($item in $items) {
            Write-BuildLog ("Kopyalaniyor | {0} | oge={1}" -f $Description, $item.FullName)
            Copy-Item -LiteralPath $item.FullName -Destination $DestinationPath -Recurse -Force
        }
    }
    catch {
        throw ("Kopya basarisiz | {0} | kaynak={1} | hedef={2} | hata={3}" -f $Description, $SourcePath, $DestinationPath, $_.Exception.Message)
    }

    Write-BuildLog (
        "Kopya tamamlandi | {0} | kaynak={1} | hedef={2} | oge_sayisi={3}" -f
        $Description,
        $SourcePath,
        $DestinationPath,
        $items.Count
    )
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

function Resolve-WinPeArchitecture {
    param(
        [Parameter(Mandatory = $true)][string]$AdkRoot
    )

    $winPeRoots = @(
        (Join-Path $AdkRoot "Windows Preinstallation Environment")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }

    if (-not $winPeRoots -or $winPeRoots.Count -eq 0) {
        throw "Windows ADK bulundu ancak 'Windows Preinstallation Environment' klasoru bulunamadi. WinPE add-on kurulumu eksik olabilir."
    }

    $preferredArchitectures = @("amd64", "x86", "arm64")
    $rank = @{
        "amd64" = 0
        "x86"   = 1
        "arm64" = 2
    }
    $discoveredDirectories = New-Object System.Collections.Generic.List[string]
    $usableArchitectures = New-Object System.Collections.Generic.List[object]

    foreach ($winPeRoot in $winPeRoots) {
        Write-BuildLog ("WinPE root: {0}" -f $winPeRoot)
        $childDirectories = @(Get-ChildItem -LiteralPath $winPeRoot -Directory -ErrorAction SilentlyContinue)
        $childNames = @($childDirectories | Select-Object -ExpandProperty Name)
        if ($childNames.Count -gt 0) {
            Write-BuildLog ("WinPE mimari klasorleri: {0}" -f ($childNames -join ", "))
            foreach ($childName in $childNames) {
                $discoveredDirectories.Add($childName)
            }
        }
        else {
            Write-BuildLog "WinPE root altinda hic mimari klasoru bulunamadi." "WARN"
        }

        foreach ($architecture in $preferredArchitectures) {
            $architectureRoot = Join-Path $winPeRoot $architecture
            if (-not (Test-Path -LiteralPath $architectureRoot -PathType Container)) {
                continue
            }

            $ocRoot = Join-Path $architectureRoot "WinPE_OCs"
            $mediaRoot = Join-Path $architectureRoot "Media"
            $proofCandidates = @($mediaRoot, $ocRoot)
            $proofPath = $proofCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (-not $proofPath) {
                Write-BuildLog ("WinPE mimari klasoru bulundu ama kullanilabilir payload kaniti yok: {0}" -f $architectureRoot) "WARN"
                continue
            }

            if (-not (Test-Path -LiteralPath $mediaRoot -PathType Container)) {
                Write-BuildLog ("WinPE mimari klasoru bulundu ama Media eksik: {0}" -f $architectureRoot) "WARN"
                continue
            }

            if (-not (Test-Path -LiteralPath $ocRoot -PathType Container)) {
                Write-BuildLog ("WinPE mimari klasoru bulundu ama WinPE_OCs eksik: {0}" -f $architectureRoot) "WARN"
                continue
            }

            $usableArchitectures.Add([pscustomobject]@{
                Name             = $architecture
                WinPeRoot        = $winPeRoot
                ArchitectureRoot = $architectureRoot
                OcRoot           = $ocRoot
                MediaRoot        = $mediaRoot
                ProofPath        = $proofPath
                Rank             = $rank[$architecture]
            })
        }
    }

    if ($usableArchitectures.Count -eq 0) {
        $searchedRoots = $winPeRoots -join ", "
        $foundDirectories = if ($discoveredDirectories.Count -gt 0) {
            ($discoveredDirectories | Select-Object -Unique) -join ", "
        }
        else {
            "(hicbiri)"
        }
        throw ("WinPE add-on kurulu olabilir ancak kullanilabilir mimari payload bulunamadi. Aranan kokler: {0}. Bulunan mimari klasorleri: {1}" -f $searchedRoots, $foundDirectories)
    }

    $selected = $usableArchitectures |
        Sort-Object -Property Rank, Name |
        Select-Object -First 1

    Write-BuildLog ("Secilen WinPE mimarisi: {0}" -f $selected.Name)
    Write-BuildLog ("Secilen WinPE mimari kok dizini: {0}" -f $selected.ArchitectureRoot)
    Write-BuildLog ("Secimi dogrulayan dizin: {0}" -f $selected.ProofPath)
    return $selected
}

function Resolve-CopypeArchitectureTokens {
    param(
        [Parameter(Mandatory = $true)][string]$CopypePath,
        [Parameter(Mandatory = $true)][string]$FilesystemArchitecture
    )

    $defaultTokenMap = @{
        "amd64" = @("amd64", "x64")
        "x86"   = @("x86")
        "arm64" = @("arm64")
    }

    $tokenCandidates = New-Object System.Collections.Generic.List[string]
    foreach ($token in ($defaultTokenMap[$FilesystemArchitecture])) {
        if (-not [string]::IsNullOrWhiteSpace($token) -and -not $tokenCandidates.Contains($token)) {
            $tokenCandidates.Add($token)
        }
    }

    try {
        $copypeContent = Get-Content -LiteralPath $CopypePath -Raw -ErrorAction Stop
        $hintLines = @(
            ($copypeContent -split "`r?`n") |
                Where-Object { $_ -match '(?i)(usage|architecture|amd64|x64|x86|arm64)' } |
                Select-Object -First 8
        )
        if ($hintLines.Count -gt 0) {
            Write-BuildLog ("copype ipucu satirlari: {0}" -f ($hintLines -join " || "))
        }

        $hasAmd64Token = $copypeContent -match '(?i)(?<![A-Za-z0-9_])amd64(?![A-Za-z0-9_])'
        $hasX64Token = $copypeContent -match '(?i)(?<![A-Za-z0-9_])x64(?![A-Za-z0-9_])'
        $hasX86Token = $copypeContent -match '(?i)(?<![A-Za-z0-9_])x86(?![A-Za-z0-9_])'
        $hasArm64Token = $copypeContent -match '(?i)(?<![A-Za-z0-9_])arm64(?![A-Za-z0-9_])'

        $discoveredTokens = New-Object System.Collections.Generic.List[string]
        if ($hasAmd64Token) { $discoveredTokens.Add("amd64") }
        if ($hasX64Token) { $discoveredTokens.Add("x64") }
        if ($hasX86Token) { $discoveredTokens.Add("x86") }
        if ($hasArm64Token) { $discoveredTokens.Add("arm64") }
        if ($discoveredTokens.Count -gt 0) {
            Write-BuildLog ("copype icinde gorulen mimari tokenleri: {0}" -f ($discoveredTokens -join ", "))
        }

        if ($FilesystemArchitecture -eq "amd64") {
            if ($hasX64Token -and -not $hasAmd64Token) {
                $tokenCandidates.Clear()
                $tokenCandidates.Add("x64")
                $tokenCandidates.Add("amd64")
            }
            elseif ($hasAmd64Token -and -not $hasX64Token) {
                $tokenCandidates.Clear()
                $tokenCandidates.Add("amd64")
                $tokenCandidates.Add("x64")
            }
        }
    }
    catch {
        Write-BuildLog ("copype icerigi okunamadi, varsayilan token sirasi kullanilacak: " + $_.Exception.Message) "WARN"
    }

    if ($tokenCandidates.Count -eq 0) {
        throw ("copype icin gecerli token adayi olusturulamadi. Dosya sistemi mimarisi: {0}" -f $FilesystemArchitecture)
    }

    Write-BuildLog ("copype dosya sistemi mimarisi: {0}" -f $FilesystemArchitecture)
    Write-BuildLog ("copype komut token adaylari: {0}" -f ($tokenCandidates -join ", "))
    return @($tokenCandidates.ToArray())
}

function Resolve-WinPeRequiredSourceFile {
    param(
        [Parameter(Mandatory = $true)][string]$ArchitectureRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string]$AdkRoot
    )

    $searchRoot = $ArchitectureRoot
    Write-BuildLog ("Kaynak arama basliyor | goreli={0} | search_root={1}" -f $RelativePath, $searchRoot)

    if ($RelativePath -ieq "sources\boot.wim") {
        Write-BuildLog "Media\\sources\\boot.wim eksik. WinPE WIM fallback aramasi baslatiliyor." "WARN"
        $candidateFiles = @(
            Get-ChildItem -LiteralPath $ArchitectureRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ieq "winpe.wim" -or $_.Name -ieq "boot.wim" }
        )
        if ($candidateFiles.Count -gt 0) {
            Write-BuildLog ("boot.wim/winpe.wim adaylari: {0}" -f (($candidateFiles | Select-Object -ExpandProperty FullName) -join " | "))
        }
        else {
            Write-BuildLog "boot.wim/winpe.wim fallback adaylari bulunamadi." "WARN"
            return $null
        }

        return $candidateFiles |
            Sort-Object @{
                Expression = {
                    if ($_.FullName -match '(?i)[\\/]en-us[\\/]winpe\.wim$') { return 0 }
                    if ($_.FullName -match '(?i)[\\/]winpe\.wim$') { return 1 }
                    if ($_.FullName -match '(?i)[\\/]sources[\\/]boot\.wim$') { return 2 }
                    if ($_.FullName -match '(?i)[\\/]boot\.wim$') { return 3 }
                    return 10
                }
            }, FullName |
            Select-Object -First 1
    }

    if ($RelativePath -ieq "boot\etfsboot.com") {
        Write-BuildLog "WinPE Media altinda etfsboot.com yok. ADK BIOS boot asset fallback aramasi baslatiliyor." "WARN"
        $knownAdkRoots = @(
            (Join-Path $AdkRoot "Deployment Tools\amd64\Oscdimg"),
            (Join-Path $AdkRoot "Deployment Tools\x86\Oscdimg"),
            (Join-Path $AdkRoot "Deployment Tools\arm64\Oscdimg"),
            (Join-Path $AdkRoot "Deployment Tools\Oscdimg"),
            (Join-Path $AdkRoot "Deployment Tools")
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }

        if ($knownAdkRoots.Count -gt 0) {
            Write-BuildLog ("etfsboot.com icin ADK arama kokleri: {0}" -f ($knownAdkRoots -join ", "))
        }
        else {
            Write-BuildLog "etfsboot.com icin bilinen ADK arama koku bulunamadi; genis ADK aramasi denenecek." "WARN"
        }

        $candidateFiles = @()
        foreach ($root in $knownAdkRoots) {
            Write-BuildLog ("etfsboot.com araniyor | root={0}" -f $root)
            $candidateFiles += @(Get-ChildItem -LiteralPath $root -Recurse -Force -File -Filter "etfsboot.com" -ErrorAction SilentlyContinue)
        }

        if ($candidateFiles.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($AdkRoot) -and (Test-Path -LiteralPath $AdkRoot -PathType Container)) {
            Write-BuildLog ("etfsboot.com icin genis ADK aramasi basliyor | root={0}" -f $AdkRoot) "WARN"
            $candidateFiles = @(
                Get-ChildItem -LiteralPath $AdkRoot -Recurse -Force -File -Filter "etfsboot.com" -ErrorAction SilentlyContinue
            )
        }

        if ($candidateFiles.Count -gt 0) {
            $uniqueCandidates = @($candidateFiles | Sort-Object -Property FullName -Unique)
            Write-BuildLog ("etfsboot.com adaylari: {0}" -f (($uniqueCandidates | Select-Object -ExpandProperty FullName) -join " | "))
            return $uniqueCandidates |
                Sort-Object @{
                    Expression = {
                        if ($_.FullName -match '(?i)[\\/]Deployment Tools[\\/]amd64[\\/]Oscdimg[\\/]etfsboot\.com$') { return 0 }
                        if ($_.FullName -match '(?i)[\\/]Oscdimg[\\/]etfsboot\.com$') { return 1 }
                        return 10
                    }
                }, FullName |
                Select-Object -First 1
        }

        Write-BuildLog "etfsboot.com icin ADK fallback adaylari bulunamadi." "WARN"
        return $null
    }

    $leafName = Split-Path $RelativePath -Leaf
    $candidateFiles = @(
        Get-ChildItem -LiteralPath $ArchitectureRoot -Recurse -Force -File -Filter $leafName -ErrorAction SilentlyContinue
    )
    if ($candidateFiles.Count -eq 0) {
        Write-BuildLog ("Zorunlu dosya fallback adaylari bulunamadi | goreli={0} | yaprak={1}" -f $RelativePath, $leafName) "WARN"
        return $null
    }

    return $candidateFiles |
        Sort-Object @{
            Expression = {
                if ($_.FullName.EndsWith($RelativePath, [System.StringComparison]::OrdinalIgnoreCase)) { 0 } else { 1 }
            }
        }, FullName |
        Select-Object -First 1
}

function Ensure-WinPeStagedFile {
    param(
        [Parameter(Mandatory = $true)][string]$ArchitectureRoot,
        [Parameter(Mandatory = $true)][string]$SourceMediaRoot,
        [Parameter(Mandatory = $true)][string]$DestinationMediaRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string]$AdkRoot
    )

    $stagedPath = Join-Path $DestinationMediaRoot $RelativePath
    $preferredSource = Join-Path $SourceMediaRoot $RelativePath
    Write-BuildLog ("Manual staging dogrulamasi basliyor | staged={0} | beklenen kaynak={1}" -f $stagedPath, $preferredSource)

    if (Test-Path -LiteralPath $stagedPath) {
        Write-BuildLog ("Manual staging dogrulandi: {0}" -f $stagedPath)
        return $stagedPath
    }

    Write-BuildLog ("Staged path eksik, fallback arama basliyor | staged={0} | search_root={1}" -f $stagedPath, $ArchitectureRoot) "WARN"
    $selectedCandidate = Resolve-WinPeRequiredSourceFile -ArchitectureRoot $ArchitectureRoot -RelativePath $RelativePath -AdkRoot $AdkRoot
    if (-not $selectedCandidate) {
        throw ("Gerekli WinPE dosyasi bulunamadi | staged={0} | beklenen kaynak={1} | mimari kok={2}" -f $stagedPath, $preferredSource, $ArchitectureRoot)
    }

    Write-BuildLog ("Fallback kaynak bulundu | goreli={0} | kaynak={1} | hedef={2}" -f $RelativePath, $selectedCandidate.FullName, $stagedPath)
    New-Item -ItemType Directory -Force -Path (Split-Path $stagedPath -Parent) | Out-Null
    Copy-Item -LiteralPath $selectedCandidate.FullName -Destination $stagedPath -Force

    if (-not (Test-Path -LiteralPath $stagedPath)) {
        throw ("Manual staging dogrulama basarisiz | staged={0} | beklenen kaynak={1}" -f $stagedPath, $preferredSource)
    }

    Write-BuildLog ("Manual staging dogrulandi: {0}" -f $stagedPath)
    return $stagedPath
}

function Stage-WinPeWorkspaceManually {
    param(
        [Parameter(Mandatory = $true)][string]$ArchitectureRoot,
        [Parameter(Mandatory = $true)][string]$SourceMediaRoot,
        [Parameter(Mandatory = $true)][string]$WorkRoot,
        [string]$AdkRoot
    )

    $destinationMediaRoot = Join-Path $WorkRoot "media"
    $destinationFwfilesRoot = Join-Path $WorkRoot "fwfiles"
    $destinationMountRoot = Join-Path $WorkRoot "mount"
    $lastManualStep = "baslangic"

    Write-BuildLog "copype tum denemelerde basarisiz oldu. Manual WinPE staging baslatiliyor." "WARN"
    Write-BuildLog ("Manual staging kaynak mimari koku: {0}" -f $ArchitectureRoot)
    Write-BuildLog ("Manual staging kaynak Media: {0}" -f $SourceMediaRoot)
    Write-BuildLog ("Manual staging hedef work root: {0}" -f $WorkRoot)
    Write-BuildLog ("Manual staging hedef media root: {0}" -f $destinationMediaRoot)
    Write-BuildLog ("Manual staging hedef fwfiles root: {0}" -f $destinationFwfilesRoot)
    Write-BuildLog ("Manual staging hedef mount root: {0}" -f $destinationMountRoot)

    try {
        $lastManualStep = "work root temizleme"
        if (Test-Path -LiteralPath $WorkRoot) {
            Write-BuildLog "Manual staging oncesi mevcut work root temizleniyor."
            Remove-Item -LiteralPath $WorkRoot -Recurse -Force
            Write-BuildLog "Manual staging mevcut work root temizlendi."
        }

        $lastManualStep = "hedef dizinleri olusturma"
        New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
        Write-BuildLog ("Olusturuldu: {0}" -f $WorkRoot)
        New-Item -ItemType Directory -Force -Path $destinationFwfilesRoot | Out-Null
        Write-BuildLog ("Olusturuldu: {0}" -f $destinationFwfilesRoot)
        New-Item -ItemType Directory -Force -Path $destinationMountRoot | Out-Null
        Write-BuildLog ("Olusturuldu: {0}" -f $destinationMountRoot)

        $lastManualStep = "Media payload kopyalama"
        Copy-DirectoryContents -SourcePath $SourceMediaRoot -DestinationPath $destinationMediaRoot -Description "WinPE Media payload"

        $lastManualStep = "fwfiles payload hazirlama"
        $fwSource = Join-Path $ArchitectureRoot "fwfiles"
        Write-BuildLog ("fwfiles kaynak adayi: {0}" -f $fwSource)
        if (Test-Path -LiteralPath $fwSource -PathType Container) {
            Copy-DirectoryContents -SourcePath $fwSource -DestinationPath $destinationFwfilesRoot -Description "WinPE fwfiles payload"
        }
        else {
            Write-BuildLog "Kaynakta ayri bir fwfiles klasoru yok; media icindeki boot dosyalariyla fwfiles iskeleti olusturuluyor." "WARN"
            foreach ($seed in @(
                @{ Source = (Join-Path $destinationMediaRoot "boot"); Destination = (Join-Path $destinationFwfilesRoot "boot"); Label = "fwfiles boot tohumu" },
                @{ Source = (Join-Path $destinationMediaRoot "EFI"); Destination = (Join-Path $destinationFwfilesRoot "EFI"); Label = "fwfiles EFI tohumu" }
            )) {
                Write-BuildLog ("fwfiles tohum kontrolu | kaynak={0} | hedef={1}" -f $seed.Source, $seed.Destination)
                if (Test-Path -LiteralPath $seed.Source -PathType Container) {
                    Copy-DirectoryContents -SourcePath $seed.Source -DestinationPath $seed.Destination -Description $seed.Label
                }
                else {
                    Write-BuildLog ("fwfiles tohum kaynagi bulunamadi: {0}" -f $seed.Source) "WARN"
                }
            }
        }

        $requiredRelativePaths = @(
            "sources\boot.wim",
            "boot\bcd",
            "boot\etfsboot.com",
            "EFI\Boot\bootx64.efi",
            "EFI\Microsoft\Boot\BCD"
        )
        $validatedPaths = New-Object System.Collections.Generic.List[string]
        foreach ($relativePath in $requiredRelativePaths) {
            $lastManualStep = "gerekli path dogrulama: $relativePath"
            $validatedPath = Ensure-WinPeStagedFile -ArchitectureRoot $ArchitectureRoot -SourceMediaRoot $SourceMediaRoot -DestinationMediaRoot $destinationMediaRoot -RelativePath $relativePath -AdkRoot $AdkRoot
            $validatedPaths.Add($validatedPath)
        }

        $lastManualStep = "son ozet"
        Write-BuildLog ("Manual staging basarili | staged media root={0}" -f $destinationMediaRoot)
        Write-BuildLog ("Manual staging dogrulanan dosyalar: {0}" -f ($validatedPaths -join ", "))
    }
    catch {
        throw (
            "Manual WinPE staging basarisiz. Mimari kok={0}; kaynak Media={1}; hedef work root={2}; son adim={3}; hata={4}" -f
            $ArchitectureRoot,
            $SourceMediaRoot,
            $WorkRoot,
            $lastManualStep,
            $_.Exception.Message
        )
    }
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

function Resolve-MsysBash {
    $rawEnvPath = $env:CIGERTOOL_MSYS_BASH
    Write-BuildLog "MSYS bash raw env: $rawEnvPath"

    if (-not [string]::IsNullOrWhiteSpace($rawEnvPath)) {
        $normalizedEnvPath = $rawEnvPath.Trim().Trim('"').Trim("'")
        Write-BuildLog "MSYS bash normalized env: $normalizedEnvPath"

        $exists = Test-Path -LiteralPath $normalizedEnvPath -PathType Leaf
        Write-BuildLog "MSYS bash Test-Path(LiteralPath): $exists"

        if ($exists) {
            $resolvedEnvPath = $normalizedEnvPath
            try {
                $resolvedEnvPath = (Resolve-Path -LiteralPath $normalizedEnvPath).Path
                Write-BuildLog "MSYS bash resolved env path: $resolvedEnvPath"
            }
            catch {
                Write-BuildLog ("MSYS bash Resolve-Path atlandi: " + $_.Exception.Message) "WARN"
            }

            try {
                $versionOutput = & $resolvedEnvPath --version 2>&1 | Select-Object -First 1
                if ($LASTEXITCODE -eq 0) {
                    Write-BuildLog "MSYS bash execution test: basarili"
                    if ($versionOutput) {
                        Write-BuildLog ("MSYS bash version: " + $versionOutput.ToString())
                    }
                    return [string]$resolvedEnvPath
                }
                Write-BuildLog "MSYS bash execution test: basarisiz donus kodu" "WARN"
            }
            catch {
                Write-BuildLog ("MSYS bash execution test hatasi: " + $_.Exception.Message) "WARN"
            }
        }
    }

    $fallbacks = @()
    $command = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        $fallbacks += $command.Source
    }

    foreach ($candidate in $fallbacks) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }
        $normalizedCandidate = $candidate.Trim().Trim('"').Trim("'")
        Write-BuildLog "MSYS bash fallback adayi: $normalizedCandidate"
        if (-not (Test-Path -LiteralPath $normalizedCandidate -PathType Leaf)) {
            continue
        }
        try {
            $resolvedCandidate = (Resolve-Path -LiteralPath $normalizedCandidate).Path
        }
        catch {
            $resolvedCandidate = $normalizedCandidate
        }
        try {
            $null = & $resolvedCandidate --version 2>&1 | Select-Object -First 1
            if ($LASTEXITCODE -eq 0) {
                Write-BuildLog "MSYS bash fallback execution test: basarili"
                return [string]$resolvedCandidate
            }
        }
        catch {
            Write-BuildLog ("MSYS bash fallback execution test hatasi: " + $_.Exception.Message) "WARN"
        }
    }

    throw "MSYS2 bash bulunamadi. Workflow, msys2 shell icinden CIGERTOOL_MSYS_BASH degiskenini saglamalidir."
}

function Convert-ToMsysPath {
    param([string]$PathValue)
    $resolved = [System.IO.Path]::GetFullPath($PathValue).Replace("\", "/")
    if ($resolved.Length -lt 3 -or $resolved[1] -ne ":") {
        return $resolved
    }
    return "/" + $resolved[0].ToString().ToLowerInvariant() + $resolved.Substring(2)
}

function Convert-ToBashSingleQuoted {
    param([string]$Value)
    $quote = [string][char]39
    $bashEscape = $quote + '"' + $quote + '"' + $quote
    $segments = [regex]::Split($Value, [regex]::Escape($quote))
    return $quote + ($segments -join $bashEscape) + $quote
}

function New-MsysScript {
    param([string[]]$Commands)
    return (($Commands | Where-Object { $_ -and $_.Trim() }) -join "`n")
}

function Convert-WindowsPathToMsysUsingBash {
    param(
        [Parameter(Mandatory = $true)][string]$BashPath,
        [Parameter(Mandatory = $true)][string]$WindowsPath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $normalizedWindowsPath = [System.IO.Path]::GetFullPath($WindowsPath)
    $quotedWindowsPath = Convert-ToBashSingleQuoted $normalizedWindowsPath
    $scriptText = New-MsysScript @(
        'export MSYSTEM=MSYS'
        'export PATH=/usr/bin:/mingw64/bin:$PATH'
        "cygpath -u $quotedWindowsPath"
    )
    $result = Invoke-MsysCommandResult -BashPath $BashPath -Description $Description -ScriptText $scriptText
    if ($result.ExitCode -ne 0) {
        $outputSummary = ($result.Output | Select-Object -Last 20) -join " || "
        throw ("MSYS path donusumu basarisiz | windows_path={0} | cikti={1}" -f $normalizedWindowsPath, $outputSummary)
    }

    $stdoutLines = @(
        $result.Stdout |
        Where-Object { $_ -and $_.Trim() } |
        ForEach-Object { $_.Trim() }
    )
    $stderrSummary = ($result.Stderr | Select-Object -Last 20) -join " || "
    Write-BuildLog ("[result] MSYS path donusumu stdout satirlari | windows_path={0} | count={1}" -f $normalizedWindowsPath, $stdoutLines.Count)
    if (-not [string]::IsNullOrWhiteSpace($stderrSummary)) {
        Write-BuildLog ("[result] MSYS path donusumu stderr ozeti | windows_path={0} | stderr={1}" -f $normalizedWindowsPath, $stderrSummary) "WARN"
    }

    $validPathLines = @($stdoutLines | Where-Object { Test-IsLikelyMsysPath -PathValue $_ })
    $resolvedMsysPath = $validPathLines | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($resolvedMsysPath)) {
        $stdoutSummary = ($stdoutLines -join " || ")
        throw ("Invalid MSYS path conversion result for {0}: {1}" -f $Description, $stdoutSummary)
    }

    $resolvedMsysPath = $resolvedMsysPath.Trim()
    Assert-ValidMsysPathValue -PathValue $resolvedMsysPath -Label $Description
    Write-BuildLog ("[result] MSYS path donusumu | windows_path={0} | msys_path={1} | valid={2}" -f $normalizedWindowsPath, $resolvedMsysPath, $true)
    return $resolvedMsysPath
}

function Assert-MsysVisiblePath {
    param(
        [Parameter(Mandatory = $true)][string]$BashPath,
        [Parameter(Mandatory = $true)][string]$MsysPath,
        [Parameter(Mandatory = $true)][string]$Description,
        [ValidateSet("file", "directory")][string]$PathKind = "file"
    )

    $quotedPath = Convert-ToBashSingleQuoted $MsysPath
    $probeCommand = if ($PathKind -eq "directory") {
        "if [ -d $quotedPath ]; then ls -ld $quotedPath; else echo MISSING:$quotedPath; exit 44; fi"
    }
    else {
        "if [ -f $quotedPath ]; then ls -l $quotedPath; else echo MISSING:$quotedPath; exit 44; fi"
    }
    $scriptText = New-MsysScript @(
        'export MSYSTEM=MSYS'
        'export PATH=/usr/bin:/mingw64/bin:$PATH'
        $probeCommand
    )
    $result = Invoke-MsysCommandResult -BashPath $BashPath -Description $Description -ScriptText $scriptText
    if ($result.ExitCode -ne 0) {
        $outputSummary = ($result.Output | Select-Object -Last 20) -join " || "
        throw ("MSYS path preflight basarisiz | kind={0} | msys_path={1} | cikti={2}" -f $PathKind, $MsysPath, $outputSummary)
    }
}

function Invoke-MsysCommand {
    param(
        [Parameter(Mandatory = $true)][string]$BashPath,
        [Parameter(Mandatory = $true)][string]$ScriptText,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$PassThruOutput
    )

    $result = Invoke-MsysCommandResult -BashPath $BashPath -ScriptText $ScriptText -Description $Description
    if ($result.ExitCode -ne 0) {
        $outputSummary = ($result.Output | Select-Object -Last 50) -join " || "
        if ([string]::IsNullOrWhiteSpace($outputSummary)) {
            throw ("MSYS komut basarisiz oldu ({0}): {1}" -f $result.ExitCode, $Description)
        }
        throw ("MSYS komut basarisiz oldu ({0}): {1} | cikti={2}" -f $result.ExitCode, $Description, $outputSummary)
    }

    if ($PassThruOutput) {
        return $result
    }

    return $result
}

function Invoke-MsysCommandResult {
    param(
        [Parameter(Mandatory = $true)][string]$BashPath,
        [Parameter(Mandatory = $true)][string]$ScriptText,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-BuildLog ("MSYS komut: {0}" -f $Description)
    $stdoutFile = Join-Path $env:TEMP ("cigertool-msys-{0}.stdout.log" -f ([guid]::NewGuid().ToString("N")))
    $stderrFile = Join-Path $env:TEMP ("cigertool-msys-{0}.stderr.log" -f ([guid]::NewGuid().ToString("N")))
    $wrappedScript = @(
        'set +e'
        $ScriptText
        '__CT_EXIT_CODE__=$?'
        'printf ''\n__CT_EXIT_CODE__:%s\n'' "$__CT_EXIT_CODE__"'
        'exit "$__CT_EXIT_CODE__"'
    ) -join "`n"
    $bashArguments = @("--noprofile", "--norc", "-c", $wrappedScript)
    $scriptPreview = $wrappedScript
    if ($scriptPreview.Length -gt 200) {
        $scriptPreview = $scriptPreview.Substring(0, 200)
    }
    $scriptPreview = $scriptPreview.Replace("`r", "\r").Replace("`n", "\n")
    Write-BuildLog ("MSYS bash path: {0}" -f $BashPath)
    Write-BuildLog ("MSYS bash script length: {0}" -f $wrappedScript.Length)
    Write-BuildLog ("MSYS bash script preview: {0}" -f $scriptPreview)
    Write-BuildLog ("MSYS bash args: {0}" -f (($bashArguments | ForEach-Object {
        if ($_ -eq $wrappedScript) {
            '<script>'
        }
        elseif ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join " "))

    try {
        $previousLocation = Get-Location
        try {
            Set-Location -LiteralPath $previousLocation.Path
            & $BashPath --noprofile --norc -c $wrappedScript 1> $stdoutFile 2> $stderrFile
            $nativeExitCode = $LASTEXITCODE
        }
        finally {
            Set-Location -LiteralPath $previousLocation.Path
        }

        $stdoutLines = @(Read-ExternalOutputFile -Path $stdoutFile)
        $stderrLines = @(Read-ExternalOutputFile -Path $stderrFile)
        $exitCodeLine = $stdoutLines | Where-Object { $_ -match '^__CT_EXIT_CODE__:(-?\d+)$' } | Select-Object -Last 1
        $markerFound = -not [string]::IsNullOrWhiteSpace($exitCodeLine)
        if (-not $exitCodeLine) {
            $stdoutSummary = ($stdoutLines | Select-Object -Last 50) -join " || "
            $stderrSummary = ($stderrLines | Select-Object -Last 50) -join " || "
            throw ("MSYS komut gercek cikis kodunu raporlamadi: {0} | stdout={1} | stderr={2}" -f $Description, $stdoutSummary, $stderrSummary)
        }

        $capturedExitCode = [int]([regex]::Match($exitCodeLine, '^__CT_EXIT_CODE__:(-?\d+)$').Groups[1].Value)
        $stdoutLines = @($stdoutLines | Where-Object { $_ -notmatch '^__CT_EXIT_CODE__:(-?\d+)$' })

        foreach ($line in $stdoutLines) {
            Write-BuildLog ("[stdout] {0}" -f $line)
        }
        foreach ($line in $stderrLines) {
            Write-BuildLog ("[stderr] {0}" -f $line)
        }
        Write-BuildLog ("[result] MSYS exit marker found: {0}" -f $markerFound)
        Write-BuildLog ("[result] MSYS exit code: {0}" -f $capturedExitCode)
        Write-BuildLog ("[result] MSYS native exit code: {0}" -f $nativeExitCode)

        return [pscustomobject]@{
            ExitCode     = $capturedExitCode
            Output       = @($stdoutLines + $stderrLines)
            Stdout       = $stdoutLines
            Stderr       = $stderrLines
            BashExitCode = $nativeExitCode
            MarkerFound  = $markerFound
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile, $stderrFile -Force -ErrorAction SilentlyContinue
    }
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
        (Join-Path $MediaRoot "boot\bcd"),
        (Join-Path $MediaRoot "boot\etfsboot.com"),
        (Join-Path $MediaRoot "EFI\Boot\bootx64.efi")
    )) {
        Assert-Path -PathValue $required -Description "Gerekli medya dosyasi"
    }

    if ($PrebootRequired) {
        Assert-Path -PathValue (Join-Path $MediaRoot "EFI\CigerTool\grubx64.efi") -Description "Pre-boot EFI uygulamasi"
        Assert-Path -PathValue (Join-Path $MediaRoot "EFI\CigerTool\grub.cfg") -Description "Pre-boot config"
    }
}

function Get-ConservativeEfiImageSizePlan {
    param(
        [Parameter(Mandatory = $true)][string]$EfiRoot
    )

    $files = @(Get-ChildItem -LiteralPath $EfiRoot -Recurse -Force -File -ErrorAction Stop)
    $directories = @(Get-ChildItem -LiteralPath $EfiRoot -Recurse -Force -Directory -ErrorAction Stop)
    $efiBytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
    if (-not $efiBytes) {
        $efiBytes = 0
    }

    $directoryOverheadBytes = [long]($directories.Count * 256KB)
    $fileSlackBytes = [long]($files.Count * 128KB)
    $fatOverheadBytes = [long]([Math]::Max([double]128MB, [double]([Math]::Ceiling($efiBytes * 0.75))))
    $rawRequiredBytes = [long]($efiBytes + $directoryOverheadBytes + $fileSlackBytes + $fatOverheadBytes)
    $safetyMarginBytes = [long]([Math]::Max([double]256MB, [double]([Math]::Ceiling($rawRequiredBytes * 1.0))))
    $computedBytes = [long]($rawRequiredBytes + $safetyMarginBytes)
    $firstBytes = [long][Math]::Max(
        [double]512MB,
        [double]([Math]::Ceiling($computedBytes / 64MB) * 64MB)
    )
    $retryFromDouble = [double]($firstBytes * 2)
    $retryFromTree = [double]([Math]::Ceiling((($efiBytes * 6) + 512MB) / 128MB) * 128MB)
    $retryCandidateBytes = [long]([Math]::Max(
        [Math]::Max(
            [double]1GB,
            $retryFromDouble
        ),
        $retryFromTree
    ))
    $retryBytes = [long]([Math]::Ceiling($retryCandidateBytes / 128MB) * 128MB)

    Write-BuildLog (
        "EFI sizing helper | source_bytes={0} | first_pass_bytes={1} | min_enforced_bytes={2} | final_first_bytes={3}" -f
        $efiBytes,
        $computedBytes,
        512MB,
        $firstBytes
    )

    return [pscustomobject]@{
        EfiBytes               = $efiBytes
        FileCount              = $files.Count
        DirectoryCount         = $directories.Count
        DirectoryOverheadBytes = $directoryOverheadBytes
        FileSlackBytes         = $fileSlackBytes
        FatOverheadBytes       = $fatOverheadBytes
        RawRequiredBytes       = $rawRequiredBytes
        SafetyMarginBytes      = $safetyMarginBytes
        FirstBytes             = $firstBytes
        FirstMiB               = [long]([Math]::Round($firstBytes / 1MB, 0))
        RetryBytes             = $retryBytes
        RetryMiB               = [long]([Math]::Round($retryBytes / 1MB, 0))
    }
}

function New-EfiBootImage {
    param(
        [string]$BashPath,
        [string]$MediaRoot,
        [string]$ImagePath
    )

    $efiRoot = Join-Path $MediaRoot "EFI"
    Assert-Path -PathValue $efiRoot -Description "EFI klasoru"
    $efiRootWindowsPath = [System.IO.Path]::GetFullPath($efiRoot)
    $efiImageWindowsPath = [System.IO.Path]::GetFullPath($ImagePath)
    $imageParent = Split-Path $efiImageWindowsPath -Parent
    Write-BuildLog ("EFI image Windows path: {0}" -f $efiImageWindowsPath)
    Write-BuildLog ("EFI image parent dizin: {0}" -f $imageParent)
    Write-BuildLog ("EFI image parent dizin mevcut mu (oncesi): {0}" -f (Test-Path -LiteralPath $imageParent -PathType Container))
    New-Item -ItemType Directory -Force -Path $imageParent | Out-Null
    if (-not (Test-Path -LiteralPath $imageParent -PathType Container)) {
        throw ("EFI image parent dizin olusturulamadi: {0}" -f $imageParent)
    }
    Write-BuildLog ("EFI image parent dizin mevcut mu (sonrasi): {0}" -f (Test-Path -LiteralPath $imageParent -PathType Container))

    if (Test-Path -LiteralPath $efiImageWindowsPath) {
        Remove-Item -LiteralPath $efiImageWindowsPath -Force
    }

    $sizePlan = Get-ConservativeEfiImageSizePlan -EfiRoot $efiRoot
    Write-BuildLog ("EFI kaynak agaci: {0}" -f $efiRoot)
    Write-BuildLog ("EFI kaynak boyutu (recursive bytes): {0}" -f $sizePlan.EfiBytes)
    Write-BuildLog ("EFI kaynak istatistikleri | dosya={0} | dizin={1}" -f $sizePlan.FileCount, $sizePlan.DirectoryCount)
    Write-BuildLog ("EFI ham gereksinim | file_bytes={0} | dir_overhead={1} | file_slack={2} | fat_overhead={3} | raw_required={4}" -f $sizePlan.EfiBytes, $sizePlan.DirectoryOverheadBytes, $sizePlan.FileSlackBytes, $sizePlan.FatOverheadBytes, $sizePlan.RawRequiredBytes)
    Write-BuildLog ("EFI image boyut plani | margin={0} | first_bytes={1} | first_mib={2} | retry_bytes={3} | retry_mib={4}" -f $sizePlan.SafetyMarginBytes, $sizePlan.FirstBytes, $sizePlan.FirstMiB, $sizePlan.RetryBytes, $sizePlan.RetryMiB)

    $msysImagePath = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $efiImageWindowsPath -Description "cygpath -u efiboot.img"
    $msysEfiRoot = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $efiRootWindowsPath -Description "cygpath -u EFI root"
    Assert-ValidMsysPathValue -PathValue $msysImagePath -Label "efiboot.img"
    Assert-ValidMsysPathValue -PathValue $msysEfiRoot -Label "EFI root"
    Write-BuildLog ("EFI image MSYS path: {0}" -f $msysImagePath)
    Write-BuildLog ("EFI root MSYS path: {0}" -f $msysEfiRoot)
    Write-BuildLog ("EFI mtools final MSYS pathleri | efiboot={0} | efi_root={1}" -f $msysImagePath, $msysEfiRoot)
    $quotedImage = Convert-ToBashSingleQuoted $msysImagePath
    $quotedEfiRoot = Convert-ToBashSingleQuoted $msysEfiRoot
    $toolchainSetup = @(
        'export MSYSTEM=MSYS'
        'export PATH=/usr/bin:/mingw64/bin:$PATH'
    )

    $mformatScript = New-MsysScript @(
        $toolchainSetup
        'echo "MSYSTEM=$MSYSTEM"'
        'echo "PATH=$PATH"'
        'which mformat'
        "mformat -i $quotedImage -F -v CIGERTOOL_EFI ::"
    )

    $mmdScript = New-MsysScript @(
        $toolchainSetup
        'which mmd'
        "mmd -i $quotedImage ::/EFI"
    )

    $mcopyScript = New-MsysScript @(
        $toolchainSetup
        'which mcopy'
        "mcopy -i $quotedImage -s $quotedEfiRoot ::"
    )

    $attemptSizes = @(
        [long]$sizePlan.FirstBytes,
        [long]$sizePlan.RetryBytes
    ) | Select-Object -Unique

    $imageCreated = $false
    $lastEfiError = $null
    $lastMcopyOutputSummary = ""
    for ($attemptIndex = 0; $attemptIndex -lt $attemptSizes.Count; $attemptIndex++) {
        $currentImageBytes = [long]$attemptSizes[$attemptIndex]
        $currentImageMiB = [long]([Math]::Round($currentImageBytes / 1MB, 0))
        Write-BuildLog ("EFI image olusturma denemesi | deneme={0}/{1} | image_path={2} | final_bytes={3} | final_mib={4}" -f ($attemptIndex + 1), $attemptSizes.Count, $efiImageWindowsPath, $currentImageBytes, $currentImageMiB)

        if (Test-Path -LiteralPath $efiImageWindowsPath) {
            Remove-Item -LiteralPath $efiImageWindowsPath -Force
        }

        New-Item -ItemType Directory -Force -Path $imageParent | Out-Null
        $parentExists = Test-Path -LiteralPath $imageParent -PathType Container
        Write-BuildLog ("EFI image parent dizin durumu | deneme={0} | exists={1}" -f ($attemptIndex + 1), $parentExists)
        if (-not $parentExists) {
            throw ("EFI image parent dizin mformat oncesi eksik: {0}" -f $imageParent)
        }

        try {
            & fsutil.exe file createnew $efiImageWindowsPath $currentImageBytes | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw ("fsutil createnew cikis kodu: {0}" -f $LASTEXITCODE)
            }
        }
        catch {
            Write-BuildLog ("fsutil createnew basarisiz, byte-array fallback kullaniliyor | path={0} | bytes={1} | hata={2}" -f $efiImageWindowsPath, $currentImageBytes, $_.Exception.Message) "WARN"
            [System.IO.File]::WriteAllBytes($efiImageWindowsPath, (New-Object byte[] $currentImageBytes))
        }
        Write-BuildLog ("EFI image file created: {0} ({1} bytes)" -f $efiImageWindowsPath, $currentImageBytes)

        $imageExists = Test-Path -LiteralPath $efiImageWindowsPath -PathType Leaf
        $actualImageLength = if ($imageExists) { (Get-Item -LiteralPath $efiImageWindowsPath).Length } else { -1 }
        Write-BuildLog ("EFI image dosya durumu | deneme={0} | exists={1} | size_bytes={2}" -f ($attemptIndex + 1), $imageExists, $actualImageLength)
        if (-not $imageExists) {
            throw ("EFI image dosyasi mformat oncesi bulunamadi: {0}" -f $efiImageWindowsPath)
        }
        if ($actualImageLength -ne $currentImageBytes) {
            throw ("EFI image dosya boyutu beklenenle eslesmiyor | path={0} | actual={1} | expected={2}" -f $efiImageWindowsPath, $actualImageLength, $currentImageBytes)
        }
        if ($actualImageLength -le 0) {
            throw ("EFI image dosya boyutu sifir veya negatif | path={0} | actual={1}" -f $efiImageWindowsPath, $actualImageLength)
        }

        try {
            Assert-MsysVisiblePath -BashPath $BashPath -MsysPath $msysImagePath -Description ("MSYS preflight ls -l efiboot.img deneme {0}" -f ($attemptIndex + 1)) -PathKind file
        }
        catch {
            throw (
                "EFI image MSYS preflight basarisiz | windows_path={0} | msys_path={1} | parent_exists={2} | file_exists={3} | file_length={4} | hata={5}" -f
                $efiImageWindowsPath,
                $msysImagePath,
                $parentExists,
                $imageExists,
                $actualImageLength,
                $_.Exception.Message
            )
        }

        Invoke-MsysCommand -BashPath $BashPath -Description "mformat -i $msysImagePath -F -v CIGERTOOL_EFI ::" -ScriptText $mformatScript
        Invoke-MsysCommand -BashPath $BashPath -Description "mmd -i $msysImagePath ::/EFI" -ScriptText $mmdScript

        $mcopyResult = Invoke-MsysCommandResult -BashPath $BashPath -Description "mcopy -i $msysImagePath -s $msysEfiRoot ::" -ScriptText $mcopyScript
        $lastMcopyOutputSummary = ($mcopyResult.Output | Select-Object -Last 50) -join " || "
        if ($mcopyResult.ExitCode -eq 0) {
            $imageCreated = $true
            Write-BuildLog ("UEFI boot image olusturuldu | image_path={0} | final_bytes={1} | final_mib={2}" -f $efiImageWindowsPath, $currentImageBytes, $currentImageMiB)
            break
        }

        $lastEfiError = "MSYS komut basarisiz oldu ({0}): {1} | cikti={2}" -f $mcopyResult.ExitCode, "mcopy -i $msysImagePath -s $msysEfiRoot ::", $lastMcopyOutputSummary
        Write-BuildLog ("EFI mcopy hatasi | deneme={0} | image_path={1} | final_bytes={2} | exit_code={3} | cikti={4}" -f ($attemptIndex + 1), $efiImageWindowsPath, $currentImageBytes, $mcopyResult.ExitCode, $lastMcopyOutputSummary) "WARN"
        $isDiskFull = $lastMcopyOutputSummary -match '(?i)disk full'
        if ($isDiskFull -and $attemptIndex -lt ($attemptSizes.Count - 1)) {
            Write-BuildLog ("EFI image kopyasi disk full nedeniyle yeniden daha buyuk boyutla denenecek | ilk_bytes={0} | retry_bytes={1}" -f $sizePlan.FirstBytes, $sizePlan.RetryBytes) "WARN"
            continue
        }

        throw ("UEFI boot image mcopy basarisiz oldu | efi_tree_bytes={0} | first_image_bytes={1} | retry_image_bytes={2} | cikti={3}" -f $sizePlan.EfiBytes, $sizePlan.FirstBytes, $sizePlan.RetryBytes, $lastMcopyOutputSummary)
    }

    if (-not $imageCreated) {
        throw ("UEFI boot image olusturulamadi | efi_tree_bytes={0} | first_image_bytes={1} | retry_image_bytes={2} | hata={3}" -f $sizePlan.EfiBytes, $sizePlan.FirstBytes, $sizePlan.RetryBytes, $lastEfiError)
    }
}

function Build-IsoWithXorriso {
    param(
        [string]$BashPath,
        [string]$MediaRoot,
        [string]$IsoPath,
        [string]$EfiImageRelativePath
    )

    $mediaRootWindowsPath = [System.IO.Path]::GetFullPath($MediaRoot)
    $isoWindowsPath = [System.IO.Path]::GetFullPath($IsoPath)
    $efiImageWindowsPath = [System.IO.Path]::GetFullPath((Join-Path $MediaRoot $EfiImageRelativePath.Replace("/", "\")))
    $biosBootWindowsPath = [System.IO.Path]::GetFullPath((Join-Path $MediaRoot "boot\etfsboot.com"))
    $bootWimWindowsPath = [System.IO.Path]::GetFullPath((Join-Path $MediaRoot "sources\boot.wim"))
    $isoParent = Split-Path $isoWindowsPath -Parent

    Assert-Path -PathValue $biosBootWindowsPath -Description "BIOS boot image"
    Assert-Path -PathValue $efiImageWindowsPath -Description "UEFI boot image"
    Assert-Path -PathValue $bootWimWindowsPath -Description "boot.wim"
    New-Item -ItemType Directory -Force -Path $isoParent | Out-Null
    Assert-Path -PathValue $isoParent -Description "ISO hedef klasoru"

    $msysMediaRoot = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $mediaRootWindowsPath -Description "cygpath -u media root"
    $msysIsoPath = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $isoWindowsPath -Description "cygpath -u ISO output"
    $msysEfiImagePath = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $efiImageWindowsPath -Description "cygpath -u final efiboot.img"
    $msysBiosBootPath = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $biosBootWindowsPath -Description "cygpath -u BIOS boot image"
    $msysBootWimPath = Convert-WindowsPathToMsysUsingBash -BashPath $BashPath -WindowsPath $bootWimWindowsPath -Description "cygpath -u boot.wim"
    foreach ($resolvedPath in @(
        @{ Label = "media root"; Value = $msysMediaRoot },
        @{ Label = "ISO output"; Value = $msysIsoPath },
        @{ Label = "final efiboot.img"; Value = $msysEfiImagePath },
        @{ Label = "BIOS boot image"; Value = $msysBiosBootPath },
        @{ Label = "boot.wim"; Value = $msysBootWimPath }
    )) {
        Assert-ValidMsysPathValue -PathValue ([string]$resolvedPath.Value) -Label ([string]$resolvedPath.Label)
    }
    Write-BuildLog ("xorriso media root Windows path: {0}" -f $mediaRootWindowsPath)
    Write-BuildLog ("xorriso ISO Windows path: {0}" -f $isoWindowsPath)
    Write-BuildLog ("xorriso UEFI image Windows path: {0}" -f $efiImageWindowsPath)
    Write-BuildLog ("xorriso BIOS image Windows path: {0}" -f $biosBootWindowsPath)
    Write-BuildLog ("xorriso boot.wim Windows path: {0}" -f $bootWimWindowsPath)
    Write-BuildLog ("xorriso media root MSYS path: {0}" -f $msysMediaRoot)
    Write-BuildLog ("xorriso ISO MSYS path: {0}" -f $msysIsoPath)
    Write-BuildLog ("xorriso UEFI image MSYS path: {0}" -f $msysEfiImagePath)
    Write-BuildLog ("xorriso BIOS image MSYS path: {0}" -f $msysBiosBootPath)
    Write-BuildLog ("xorriso boot.wim MSYS path: {0}" -f $msysBootWimPath)
    Write-BuildLog ("xorriso final MSYS pathleri | media={0} | iso={1} | efiboot={2} | bios={3} | bootwim={4}" -f $msysMediaRoot, $msysIsoPath, $msysEfiImagePath, $msysBiosBootPath, $msysBootWimPath)
    Write-BuildLog ("xorriso input existence | media_root={0} | efiboot={1} | etfsboot={2} | bootwim={3} | iso_parent={4}" -f
        (Test-Path -LiteralPath $mediaRootWindowsPath -PathType Container),
        (Test-Path -LiteralPath $efiImageWindowsPath -PathType Leaf),
        (Test-Path -LiteralPath $biosBootWindowsPath -PathType Leaf),
        (Test-Path -LiteralPath $bootWimWindowsPath -PathType Leaf),
        (Test-Path -LiteralPath $isoParent -PathType Container)
    )
    Write-BuildLog ("xorriso input sizes | efiboot_bytes={0} | bootwim_bytes={1} | etfsboot_bytes={2}" -f
        (Get-Item -LiteralPath $efiImageWindowsPath).Length,
        (Get-Item -LiteralPath $bootWimWindowsPath).Length,
        (Get-Item -LiteralPath $biosBootWindowsPath).Length
    )

    Assert-MsysVisiblePath -BashPath $BashPath -MsysPath $msysMediaRoot -Description "MSYS preflight media root" -PathKind directory
    Assert-MsysVisiblePath -BashPath $BashPath -MsysPath $msysEfiImagePath -Description "MSYS preflight final efiboot.img" -PathKind file
    Assert-MsysVisiblePath -BashPath $BashPath -MsysPath $msysBiosBootPath -Description "MSYS preflight BIOS boot image" -PathKind file
    Assert-MsysVisiblePath -BashPath $BashPath -MsysPath $msysBootWimPath -Description "MSYS preflight boot.wim" -PathKind file

    $quotedIso = Convert-ToBashSingleQuoted $msysIsoPath
    $quotedMediaRoot = Convert-ToBashSingleQuoted $msysMediaRoot
    $quotedEfiImage = Convert-ToBashSingleQuoted $EfiImageRelativePath
    $toolchainSetup = @(
        'export MSYSTEM=MSYS'
        'export PATH=/usr/bin:/mingw64/bin:$PATH'
    )
    $xorrisoCommand = @(
        'xorriso -report_about ALL -as mkisofs',
        "-iso-level 3",
        "-full-iso9660-filenames",
        "-volid CIGERTOOL",
        "-eltorito-boot boot/etfsboot.com",
        "-no-emul-boot",
        "-boot-load-size 8",
        "-eltorito-catalog boot/boot.cat",
        "-eltorito-alt-boot",
        "-e $quotedEfiImage",
        "-no-emul-boot",
        "-isohybrid-gpt-basdat",
        "-udf",
        "-joliet-long",
        "-relaxed-filenames",
        "-o $quotedIso",
        $quotedMediaRoot
    ) -join " "
    Write-BuildLog ("xorriso tam komut satiri: {0}" -f $xorrisoCommand)
    $xorrisoScript = New-MsysScript @(
        $toolchainSetup
        'which xorriso'
        'echo "=== Xorriso Preflight: media root ==="'
        "test -d $quotedMediaRoot"
        "ls -ld $quotedMediaRoot"
        'echo "=== Xorriso Preflight: EFI image ==="'
        "test -f $(Convert-ToBashSingleQuoted $msysEfiImagePath)"
        "ls -l $(Convert-ToBashSingleQuoted $msysEfiImagePath)"
        'echo "=== Xorriso Preflight: BIOS image ==="'
        "test -f $(Convert-ToBashSingleQuoted $msysBiosBootPath)"
        "ls -l $(Convert-ToBashSingleQuoted $msysBiosBootPath)"
        $xorrisoCommand
    )
    Invoke-MsysCommand -BashPath $BashPath -Description "xorriso -as mkisofs -> $msysIsoPath" -ScriptText $xorrisoScript
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
$winPeLayout = Resolve-WinPeArchitecture -AdkRoot $adkRoot
$winPeArchitecture = $winPeLayout.Name
$ocRoot = $winPeLayout.OcRoot
Assert-Path -PathValue $ocRoot -Description "WinPE optional component klasoru"

Write-BuildLog "CIGERTOOL_MSYS_BASH env: $($env:CIGERTOOL_MSYS_BASH)"
$bashPath = Resolve-MsysBash
if ($bashPath -is [System.Array]) {
    Write-BuildLog ("MSYS bash array sonucu alindi, son eleman seciliyor: {0}" -f ($bashPath.Count))
    $bashPath = $bashPath | Select-Object -Last 1
}
$bashPath = [string]$bashPath
Write-BuildLog ("MSYS bash type: {0}" -f $bashPath.GetType().FullName)
Write-BuildLog ("MSYS bash sanitized: {0}" -f $bashPath)
if ([string]::IsNullOrWhiteSpace($bashPath)) {
    throw "MSYS bash yolu bos dondu."
}

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
Write-BuildLog ("WinPE root: {0}" -f $winPeLayout.WinPeRoot)
Write-BuildLog ("WinPE mimarisi: {0}" -f $winPeArchitecture)
Write-BuildLog ("WinPE mimari kanit dizini: {0}" -f $winPeLayout.ProofPath)
Write-BuildLog "Uygulama klasoru: $appRoot"
Write-BuildLog "Cikti ISO: $isoPath"
Write-BuildLog "MSYS bash: $bashPath"
$probeScript = New-MsysScript @(
    'export MSYSTEM=MSYS'
    'export PATH=/usr/bin:/mingw64/bin:$PATH'
    'echo "MSYSTEM=$MSYSTEM"'
    'echo "PATH=$PATH"'
    'which xorriso'
    'which mcopy'
    'which mformat'
)
Invoke-MsysCommand -BashPath $bashPath -Description "MSYS toolchain probe" -ScriptText $probeScript

if (Test-Path $workRoot) {
    Write-BuildLog "Eski calisma klasoru temizleniyor."
    Remove-Item -Recurse -Force $workRoot
}

Write-BuildLog ("copype.cmd yolu: {0}" -f $copype)
Write-BuildLog ("copype.cmd mevcut mu: {0}" -f (Test-Path -LiteralPath $copype -PathType Leaf))
Write-BuildLog ("WinPE work root: {0}" -f $workRoot)
Write-BuildLog ("WinPE work root mevcut mu (oncesi): {0}" -f (Test-Path -LiteralPath $workRoot))
$copypeTokenCandidates = Resolve-CopypeArchitectureTokens -CopypePath $copype -FilesystemArchitecture $winPeArchitecture
$copypeWorkRootParent = Split-Path $workRoot -Parent
Write-BuildLog ("copype icin secilen mimari klasoru: {0}" -f $winPeArchitecture)
Write-BuildLog ("copype icin secilen mimari klasor kaniti: {0}" -f $winPeLayout.ProofPath)
New-Item -ItemType Directory -Force -Path $copypeWorkRootParent | Out-Null

$copypeSucceeded = $false
$copypeLastError = $null
$copypeSuccessfulToken = $null
$manualStagingUsed = $false
for ($tokenIndex = 0; $tokenIndex -lt $copypeTokenCandidates.Count; $tokenIndex++) {
    $copypeToken = [string]$copypeTokenCandidates[$tokenIndex]
    if (Test-Path -LiteralPath $workRoot) {
        Write-BuildLog "Yeni copype denemesi oncesi work root temizleniyor."
        Remove-Item -Recurse -Force $workRoot
    }

    Write-BuildLog ("copype denemesi | mimari klasoru={0} | komut tokeni={1}" -f $winPeArchitecture, $copypeToken)
    try {
        Invoke-CmdBatchFile -BatchPath $copype -Arguments @($copypeToken, $workRoot) -WorkingDirectory $copypeWorkRootParent
        $copypeSucceeded = $true
        $copypeSuccessfulToken = $copypeToken
        break
    }
    catch {
        $copypeLastError = $_
        Write-BuildLog (
            "copype denemesi basarisiz | mimari klasoru={0} | komut tokeni={1} | hata={2}" -f
            $winPeArchitecture,
            $copypeToken,
            $_.Exception.Message
        ) "WARN"

        if ($tokenIndex -lt ($copypeTokenCandidates.Count - 1)) {
            Write-BuildLog "copype icin esdeger alternatif token denenecek." "WARN"
        }
    }
}

if (-not $copypeSucceeded) {
    Write-BuildLog (
        "copype basarisiz. Manual staging fallback devreye giriyor. Mimari klasoru={0}; denenen tokenler={1}; son hata={2}" -f
        $winPeArchitecture,
        ($copypeTokenCandidates -join ", "),
        $copypeLastError.Exception.Message
    ) "WARN"
    Stage-WinPeWorkspaceManually -ArchitectureRoot $winPeLayout.ArchitectureRoot -SourceMediaRoot $winPeLayout.MediaRoot -WorkRoot $workRoot -AdkRoot $adkRoot
    $manualStagingUsed = $true
}
elseif ($copypeSucceeded) {
    Write-BuildLog ("copype basarili | mimari klasoru={0} | komut tokeni={1}" -f $winPeArchitecture, $copypeSuccessfulToken)
}

if ($manualStagingUsed) {
    Write-BuildLog ("WinPE workspace manual staging ile hazirlandi | mimari klasoru={0} | kaynak Media={1}" -f $winPeLayout.ArchitectureRoot, $winPeLayout.MediaRoot)
}

Write-BuildLog ("WinPE work root mevcut mu (sonrasi): {0}" -f (Test-Path -LiteralPath $workRoot))
Assert-Path -PathValue $workRoot -Description "WinPE work root"
Assert-Path -PathValue $mediaRoot -Description "copype media klasoru"
Assert-Path -PathValue (Join-Path $workRoot "fwfiles") -Description "copype fwfiles klasoru"

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

$efiImageRelativePath = "efi/cigertool/efiboot.img"
$efiImagePath = Join-Path $mediaRoot $efiImageRelativePath.Replace("/", "\")
$efiImageBuildPath = Join-Path $workRoot "staging\efiboot.img"
Write-BuildLog ("EFI image gecici build path: {0}" -f $efiImageBuildPath)
Write-BuildLog ("EFI image final media path: {0}" -f $efiImagePath)
New-Item -ItemType Directory -Force -Path (Split-Path $efiImageBuildPath -Parent) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $efiImagePath -Parent) | Out-Null
if (Test-Path -LiteralPath $efiImagePath) {
    Remove-Item -LiteralPath $efiImagePath -Force
}
New-EfiBootImage -BashPath $bashPath -MediaRoot $mediaRoot -ImagePath $efiImageBuildPath
Copy-Item -LiteralPath $efiImageBuildPath -Destination $efiImagePath -Force
Assert-Path -PathValue $efiImagePath -Description "Final UEFI boot image"
Build-IsoWithXorriso -BashPath $bashPath -MediaRoot $mediaRoot -IsoPath $isoPath -EfiImageRelativePath $efiImageRelativePath
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
    msys_bash = $bashPath
} | ConvertTo-Json -Depth 3 | Set-Content -Path $metadataPath

Write-BuildLog "ISO dogrulandi ve hash uretildi."
Write-BuildLog "WinPE ISO hazir: $isoPath"

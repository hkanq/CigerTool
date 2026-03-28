param(
    [string]$DistRoot = "build-output\app\dist",
    [string]$WorkRoot = "build-output\app\work",
    [string]$SpecRoot = "build-output\app\spec"
)

$ErrorActionPreference = "Stop"

python -m PyInstaller `
  --noconfirm `
  --clean `
  --windowed `
  --name CigerTool `
  --distpath $DistRoot `
  --workpath $WorkRoot `
  --specpath $SpecRoot `
  --paths . `
  --collect-all PySide6 `
  cigertool\__main__.py

Write-Output "PyInstaller build tamamlandi: $DistRoot\CigerTool"


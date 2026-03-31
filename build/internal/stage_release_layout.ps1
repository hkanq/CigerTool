param(
    [string]$WorkspaceWimPath = "inputs\workspace\install.wim",
    [string]$OutputRoot = "build-output\workspace",
    [string]$AppBuildRoot = "build-output\app\dist\CigerTool",
    [string]$PayloadRoot = "workspace\payload",
    [int]$ImageIndex = 1,
    [string]$BootWimPath = "",
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"

Write-Host "CigerTool standart ISO build pipeline baslatiliyor."
Write-Host "Bu pipeline install.wim + boot.wim tabanli, BIOS + UEFI uyumlu, Rufus ile yazdirilabilir medya uretir."

& (Join-Path $PSScriptRoot "prepare_standard_release_media.ps1") `
    -InstallWimPath $WorkspaceWimPath `
    -OutputRoot $OutputRoot `
    -AppBuildRoot $AppBuildRoot `
    -PayloadRoot $PayloadRoot `
    -ImageIndex $ImageIndex `
    -BootWimPath $BootWimPath `
    -PlanOnly:$PlanOnly

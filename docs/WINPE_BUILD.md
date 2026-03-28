# WinPE Build

## Gereksinimler

- Windows ADK
- Windows PE Add-on
- Python 3.12+
- PyInstaller

## Yerel Build Akisi

1. `python -m pip install -r requirements.txt`
2. `powershell -ExecutionPolicy Bypass -File build\\scripts\\build_app.ps1`
3. `powershell -ExecutionPolicy Bypass -File build\\scripts\\build_winpe_iso.ps1`

## GitHub Actions

Workflow dosyasi:

- `.github/workflows/build-iso.yml`

Pipeline su artifact'i uretir:

- `CigerTool-by-hkannq.iso`
- `CigerTool-by-hkannq.iso.sha256`
- `CigerTool-by-hkannq.iso.json`

## Pre-Boot Menu

Workflow, uygun GRUB toolchain mevcutsa `isos/windows`, `isos/linux` ve `isos/tools` klasorlerini tarayan bir UEFI pre-boot menu de uretir. Build sirasinda bu katman zorunlu tutulabilir:

- `build\\scripts\\build_winpe_iso.ps1 -RequirePrebootMenu`

Windows ISO'lari icin `wimboot` binary'si bulunursa GRUB menu bunu kullanmaya calisir; aksi halde EFI fallback devreye girer.

## Neden WinPE Shell Yaklasimi?

WinPE icinde klasik masaustu deneyimi yerine uygulamayi shell olarak acmak daha deterministik ve daha hizli bir acilis saglar. Bu sayede login gerektirmeden dogrudan clone arayuzu baslatilir.

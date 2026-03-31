# CigerTool by hkannq

CigerTool, tek bir USB ile iki isi birden yapan hazir calisma alani urunudur:

- `CigerTool Workspace`
  Hazir Windows workspace'i dogrudan masaustune acar ve `CigerTool` uygulamasini otomatik baslatir.
- `ISO Library`
  USB'ye sonradan birakilan ISO dosyalarini acilis menusunde gosterir.

Bu repo artik WinPE-first veya Windows Setup-first mantigi kullanmaz. Ana urun davranisi, hazirlanmis workspace imaji uzerinden kurulur.

## Girdi

Zorunlu yerel kaynak dosya:

- `inputs/workspace/install.wim`

Bu dosya:

- build icin yerel girdidir
- git'e commit edilmez
- `.gitignore` tarafindan korunur
- manuel release calistirilmadan once repo working tree icinde bulunmalidir

## Build

Tek resmi build girisi:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build_cigertool_release.ps1
```

Sadece plan ve staging dogrulamasi icin:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build_cigertool_release.ps1 -PlanOnly
```

Gercek artifact uretimi icin yonetici yetkisi gerekir. Bunun nedeni `diskpart`, `DISM` ve `bcdboot` ile VHDX hazirlama adimlarinin yukseltilmis hak istemesidir.

## GitHub Actions

- `push` akisi sadece validation calistirir
- `push` uzerinde gercek ISO build yapilmaz
- `workflow_dispatch` + `build_mode=release` gercek ISO build yoludur
- bu release modu, repo working tree icindeki yerel `inputs/workspace/install.wim` dosyasini kullanir

Manual Actions release oncesi:

1. `inputs/workspace/install.wim` dosyasini repo working tree icine yerlestir
2. self-hosted Windows runner uzerinden `Build CigerTool Release` workflow'unu calistir
3. `build_mode=release` sec

## Ana Cikti

Birincil artifact:

- `artifacts/CigerTool-Workspace.iso`

GitHub Actions artifact adi:

- `CigerTool-Workspace`

Ikincil artifact'ler:

- `artifacts/CigerTool-Workspace.iso.sha256`
- `artifacts/CigerTool-Workspace-debug.zip`
- `artifacts/CigerTool-Workspace.release.json`

## ISO Library

Repo icindeki kaynak klasor:

- `iso-library/windows`
- `iso-library/linux`
- `iso-library/tools`

Build sirasinda bu icerik USB duzeninde su koklere tasinir:

- `/isos/windows`
- `/isos/linux`
- `/isos/tools`

Son kullanici USB'yi yazdiktan sonra yeni ISO'lari dogrudan bu `/isos/*` dizinlerine birakabilir. Acilis menusu her boot sirasinda bu dizinleri yeniden tarar.

## Klasor Ozeti

- `build/`
  Final release build girisi ve ic yardimci scriptler
- `boot/`
  GRUB tabanli acilis katmani ve boot asset'leri
- `workspace/`
  Hazir Windows workspace startup, unattend ve payload katmani
- `cigertool/`
  Ana uygulama kodu ve runtime operasyon scriptleri
- `iso-library/`
  Build kaynak ISO kutuphanesi
- `tools/`
  USB'ye tasinacak portable araclar
- `docs/`
  Mimari, boot, release ve durum belgeleri
- `inputs/`
  Build girdileri

## Urun Davranisi

Acilis menusunde varsayilan giris:

- `CigerTool Workspace`

Ikinci ana giris:

- `ISO Library`

`CigerTool Workspace` hedef davranisi:

- Windows Setup yok
- OOBE yok
- parola yok
- dogrudan masaustu
- `CigerTool` auto-start

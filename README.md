# CigerTool by hkannq

CigerTool, tek bir USB ile iki işi birden yapan hazır çalışma alanı ürünüdür:

- `CigerTool Workspace`
  Hazır Windows workspace'ini doğrudan masaüstüne açar ve `CigerTool` uygulamasını otomatik başlatır.
- `ISO Library`
  USB'ye sonradan bırakılan ISO dosyalarını açılış menüsünde gösterir.

Bu repo artık WinPE-first veya Windows Setup-first mantığı kullanmaz. Ana ürün davranışı, hazırlanmış workspace imajı üzerinden kurulur.

## Girdi

Zorunlu yerel kaynak dosya:

- `inputs/workspace/install.wim`

Bu dosya:

- build için yerel girdidir
- git'e commit edilmez
- `.gitignore` tarafından korunur

## Build

Tek resmi build girişi:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build_cigertool_release.ps1
```

Sadece plan ve staging doğrulaması için:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build_cigertool_release.ps1 -PlanOnly
```

Gerçek artifact üretimi için yönetici yetkisi gerekir. Bunun nedeni `diskpart`, `DISM` ve `bcdboot` ile VHDX hazırlama adımlarının yükseltilmiş hak istemesidir.

## GitHub Actions

- `push` akışı sadece validation çalıştırır
- `push` üzerinde gerçek ISO build yapılmaz
- `workflow_dispatch` + `build_mode=release` gerçek ISO build yoludur
- release modu self-hosted Windows runner üzerinde çalışır
- release işi kalıcı yerel repo kopyasını kullanır:
  - `C:\actions-runner\cigertool-release\repo`

Manual Actions release öncesi:

1. Self-hosted runner makinesinde şu klasöre `install.wim` koy:
   - `C:\actions-runner\cigertool-release\repo\inputs\workspace\install.wim`
2. Aynı makinede Python 3.12+ kurulu ve `python` komutu PATH içinde olsun
3. Self-hosted runner servisi yerel administrator haklarıyla çalışsın
   - `NT AUTHORITY\NETWORK SERVICE` hesabı yeterli değildir
   - gerekirse runner servisini administrator hesabıyla yeniden kur ya da `run.cmd` dosyasını yükseltilmiş terminalde başlat
4. GitHub Actions üzerinden `Build CigerTool Release` workflow'unu `build_mode=release` ile çalıştır

## Ana Çıktı

Birincil artifact:

- `artifacts/CigerTool-Workspace.iso`

GitHub Actions artifact adı:

- `CigerTool-Workspace`

İkincil artifact'ler:

- `artifacts/CigerTool-Workspace.iso.sha256`
- `artifacts/CigerTool-Workspace-debug.zip`
- `artifacts/CigerTool-Workspace.release.json`

## ISO Library

Repo içindeki kaynak klasör:

- `iso-library/windows`
- `iso-library/linux`
- `iso-library/tools`

Build sırasında bu içerik USB düzeninde şu köklere taşınır:

- `/isos/windows`
- `/isos/linux`
- `/isos/tools`

Son kullanıcı USB'yi yazdıktan sonra yeni ISO'ları doğrudan bu `/isos/*` dizinlerine bırakabilir. Açılış menüsü her boot sırasında bu dizinleri yeniden tarar.

## Klasör Özeti

- `build/`
  Final release build girişi ve iç yardımcı scriptler
- `boot/`
  GRUB tabanlı açılış katmanı ve boot asset'leri
- `workspace/`
  Hazır Windows workspace startup, unattend ve payload katmanı
- `cigertool/`
  Ana uygulama kodu ve runtime operasyon scriptleri
- `iso-library/`
  Build kaynak ISO kütüphanesi
- `tools/`
  USB'ye taşınacak portable araçlar
- `docs/`
  Mimari, boot, release ve durum belgeleri
- `inputs/`
  Build girdileri

## Ürün Davranışı

Açılış menüsünde varsayılan giriş:

- `CigerTool Workspace`

İkinci ana giriş:

- `ISO Library`

`CigerTool Workspace` hedef davranışı:

- Windows Setup yok
- OOBE yok
- parola yok
- doğrudan masaüstü
- `CigerTool` auto-start

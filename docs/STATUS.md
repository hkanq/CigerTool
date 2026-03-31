# Status

## Durum

- Tarih: 2026-03-31
- Asama: Release workflow architecture fix
- Sonuc: push ve manual release akisları ayrildi; release modu artik sadece yerel `inputs/workspace/install.wim` kullanir

## Tamamlananlar

- `push` akisi validation/plan only olarak korundu
- `workflow_dispatch` + `build_mode=release` gercek ISO build yolu olarak ayrildi
- release workflow icindeki `workspace_wim_url` ve URL indirme mantigi kaldirildi
- manual release artik yalnizca repo working tree icindeki `inputs/workspace/install.wim` dosyasini kabul ediyor
- `.gitignore` yerel WIM girdisini acikca koruyacak sekilde guncellendi
- README ve release dokumanlari yeni akisla hizalandi

## Ana Build Girisi

- `build/build_cigertool_release.ps1`

Plan dogrulama:

- `build/build_cigertool_release.ps1 -PlanOnly`

## Release Ozet

- `push` -> `cigertool-release-plan`
- `workflow_dispatch` + `build_mode=release` -> `CigerTool-Workspace`

## Kalan Riskler

- Gercek full build hala yonetici hakli Windows ortami ister
- Manual release akisinin calismasi icin self-hosted Windows runner gerekir
- Self-hosted runner working tree icinde `inputs/workspace/install.wim` dosyasi release oncesi hazir olmalidir
- Gercek USB/VM boot smoke testi ayrica yapilmalidir

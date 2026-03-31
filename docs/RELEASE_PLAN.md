# Release Plan

## Ana Build Girisi

- `build/build_cigertool_release.ps1`

Bu script tek resmi build entrypoint'idir.

## Build Modlari

Plan dogrulama:

- `build/build_cigertool_release.ps1 -PlanOnly`

Gercek artifact uretimi:

- `build/build_cigertool_release.ps1`

## Release Medya Modeli

- Standart Windows ISO yapisi kullanilir
- Ana OS imaji: `sources/install.wim`
- Boot ortami: `sources/boot.wim`
- Medya: BIOS + UEFI hibrit El Torito
- Cikti: `artifacts/CigerTool.iso`
- Yazdirma modeli: Rufus ile dogrudan USB'ye yazdirilabilir

## Davranis Hedefi

- Windows Setup ekrani gosterilmez
- OOBE gosterilmez
- `CigerTool` kullanicisi ile otomatik oturum acilir
- Dil `tr-TR`
- Masaustu dogrudan acilir
- `CigerTool` otomatik baslar

## GitHub Actions Modlari

Push validation:

- `push` -> sadece plan/staging dogrulama
- artifact: `cigertool-release-plan`
- runner: `windows-2025`

Manual release:

- `workflow_dispatch`
- `build_mode=release`
- runner tipi: `self-hosted`, `Windows`, `X64`
- kalici yerel repo koku: `C:\actions-runner\cigertool-release\repo`
- beklenen yerel girdi: `C:\actions-runner\cigertool-release\repo\inputs\workspace\install.wim`

## Artifact'ler

Birincil artifact:

- `artifacts/CigerTool.iso`

Ikincil artifact'ler:

- `artifacts/CigerTool.iso.sha256`
- `artifacts/CigerTool-debug.zip`
- `artifacts/CigerTool.release.json`

## Manuel Release Proseduru

1. Self-hosted Windows runner makinesinde kalici repo kokunu hazirla:
   `C:\actions-runner\cigertool-release\repo`
2. Ayni klasorde su yolu olustur:
   `C:\actions-runner\cigertool-release\repo\inputs\workspace`
3. `install.wim` dosyasini su tam yola koy:
   `C:\actions-runner\cigertool-release\repo\inputs\workspace\install.wim`
4. Self-hosted runner makinesinde Python 3.12+ kurulu ve `python` PATH icinde olsun.
5. Self-hosted runner servisi yerel administrator haklariyla calissin.
6. GitHub Actions icinde `Build CigerTool Release` workflow'unu calistir.
7. `build_mode=release` sec.
8. Workflow tamamlandiginda `CigerTool` artifact'ini indir.
9. Artifact icinden `CigerTool.iso` dosyasini al.

## Uretim Ozeti

1. yerel `install.wim` dogrulanir
2. uygulama paketlenir
3. `install.wim` offline servis edilir ve maksimum sikistirma ile tekrar uretilir
4. `boot.wim` auto-deploy mantigi ile ozellestirilir
5. standart BIOS + UEFI medya agaci hazirlanir
6. `CigerTool.iso` hibrit olarak uretilir

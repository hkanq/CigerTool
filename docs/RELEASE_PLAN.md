# Release Plan

## Ana Build Girisi

- `build/build_cigertool_release.ps1`

Bu script tek resmi build entrypoint'tir.

## Build Modlari

Plan dogrulama:

- `build/build_cigertool_release.ps1 -PlanOnly`

Gercek artifact uretimi:

- `build/build_cigertool_release.ps1`

## GitHub Actions Modlari

Push validation:

- `push` -> sadece plan/staging dogrulama
- artifact: `cigertool-release-plan`

Manual release:

- `workflow_dispatch`
- `build_mode=release`
- runner tipi: `self-hosted`, `Windows`, `X64`
- beklenen yerel girdi: `inputs/workspace/install.wim`

Release modu URL indirme kullanmaz. Sadece repo working tree icindeki yerel WIM dosyasini kullanir.

## Artifact'ler

Birincil artifact:

- `artifacts/CigerTool-Workspace.iso`

GitHub Actions artifact adi:

- `CigerTool-Workspace`

Ikincil artifact'ler:

- `artifacts/CigerTool-Workspace.iso.sha256`
- `artifacts/CigerTool-Workspace-debug.zip`
- `artifacts/CigerTool-Workspace.release.json`

## Manuel Release Proseduru

1. Self-hosted Windows runner makinesinde repo working tree icine `inputs/workspace/install.wim` koy
2. GitHub Actions icinde `Build CigerTool Release` workflow'unu calistir
3. `build_mode=release` sec
4. Workflow tamamlandiginda `CigerTool-Workspace` artifact'ini indir
5. Artifact icinden `CigerTool-Workspace.iso` dosyasini al

## Dagitim Modeli

Birincil artifact dagitima uygun bir USB boot ISO'sudur.

- Kullanici ISO'yu USB'ye ISO/extract mode ile yazar
- USB yazildiktan sonra `/isos/windows`, `/isos/linux` ve `/isos/tools` dizinleri kullanici tarafinda doldurulabilir
- Workspace ve ISO Library ayni USB uzerinden kullanilir

## Startup Hook

Workspace oturumu icinde otomatik baslatma hook'u:

- `workspace/startup/Start-CigerToolWorkspace.ps1`

## Uretim Ozeti

1. yerel `install.wim` dogrulanir
2. uygulama paketlenir
3. workspace VHDX hazirlanir
4. boot katmani uretilir
5. USB layout staging tamamlanir
6. `CigerTool-Workspace.iso` olusturulur

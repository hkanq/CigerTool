# Release Plan

## Ana Build Girişi

- `build/build_cigertool_release.ps1`

Bu script tek resmi build entrypoint'idir.

## Build Modları

Plan doğrulama:

- `build/build_cigertool_release.ps1 -PlanOnly`

Gerçek artifact üretimi:

- `build/build_cigertool_release.ps1`

## GitHub Actions Modları

Push validation:

- `push` -> sadece plan/staging doğrulama
- artifact: `cigertool-release-plan`
- runner: `windows-2025`

Manual release:

- `workflow_dispatch`
- `build_mode=release`
- runner tipi: `self-hosted`, `Windows`, `X64`
- kalıcı yerel repo kökü: `C:\actions-runner\cigertool-release\repo`
- beklenen yerel girdi: `C:\actions-runner\cigertool-release\repo\inputs\workspace\install.wim`

Release modu URL indirme kullanmaz. `workspace_wim_url` yoktur. Workflow, self-hosted runner üzerindeki kalıcı repo kopyasını günceller ve yalnızca yerel `install.wim` dosyasını kullanır.

## Artifact'ler

Birincil artifact:

- `artifacts/CigerTool-Workspace.iso`

GitHub Actions artifact adı:

- `CigerTool-Workspace`

İkincil artifact'ler:

- `artifacts/CigerTool-Workspace.iso.sha256`
- `artifacts/CigerTool-Workspace-debug.zip`
- `artifacts/CigerTool-Workspace.release.json`

## Manuel Release Prosedürü

1. Self-hosted Windows runner makinesinde kalıcı repo kökünü hazırla:
   `C:\actions-runner\cigertool-release\repo`
2. Aynı klasörde şu yolu oluştur:
   `C:\actions-runner\cigertool-release\repo\inputs\workspace`
3. `install.wim` dosyasını şu tam yola koy:
   `C:\actions-runner\cigertool-release\repo\inputs\workspace\install.wim`
4. Self-hosted runner makinesinde Python 3.12+ kurulu ve `python` PATH içinde olsun.
5. GitHub Actions içinde `Build CigerTool Release` workflow'unu çalıştır.
6. `build_mode=release` seç.
7. Workflow tamamlandığında `CigerTool-Workspace` artifact'ini indir.
8. Artifact içinden `CigerTool-Workspace.iso` dosyasını al.

## Dağıtım Modeli

Birincil artifact dağıtıma uygun bir USB boot ISO'sudur.

- Kullanıcı ISO'yu USB'ye ISO/extract mode ile yazar
- USB yazıldıktan sonra `/isos/windows`, `/isos/linux` ve `/isos/tools` dizinleri kullanıcı tarafında doldurulabilir
- Workspace ve ISO Library aynı USB üzerinden kullanılır

## Startup Hook

Workspace oturumu içinde otomatik başlatma hook'u:

- `workspace/startup/Start-CigerToolWorkspace.ps1`

## Üretim Özeti

1. yerel `install.wim` doğrulanır
2. uygulama paketlenir
3. workspace VHDX hazırlanır
4. boot katmanı üretilir
5. USB layout staging tamamlanır
6. `CigerTool-Workspace.iso` oluşturulur

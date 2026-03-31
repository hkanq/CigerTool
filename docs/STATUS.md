# Status

## Durum

- Tarih: 2026-03-31
- Aşama: Self-hosted release workflow hardening
- Sonuç: Release job artık GitHub-hosted araç kurulumlarına ve `_work` checkout klasörüne bağımlı değil

## Tamamlananlar

- `push` akışı validation/plan only olarak korundu
- `workflow_dispatch` + `build_mode=release` gerçek ISO build yolu olarak ayrıldı
- release workflow içindeki `workspace_wim_url` ve URL indirme mantığı kaldırıldı
- self-hosted release job, kalıcı yerel repo kökü `C:\actions-runner\cigertool-release\repo` üzerinden çalışacak şekilde güncellendi
- release job içinden `actions/setup-python` kaldırıldı; bunun yerine yerel Python 3.12+ doğrulaması eklendi
- release job için admin yetki ön kontrolü eklendi; artık `diskpart` aşamasına gelmeden önce servis hesabı net biçimde raporlanıyor
- release job için GitHub token, kalıcı repo güncellemesi yapabilmesi amacıyla açıkça job environment'ına verildi
- README ve release dokümanları kalıcı yerel repo modeliyle hizalandı

## Ana Build Girişi

- `build/build_cigertool_release.ps1`

Plan doğrulama:

- `build/build_cigertool_release.ps1 -PlanOnly`

## Release Özeti

- `push` -> `cigertool-release-plan`
- `workflow_dispatch` + `build_mode=release` -> `CigerTool-Workspace`

## Kalan Riskler

- Gerçek full build hâlâ yönetici haklı Windows ortamı ister
- Manual release akışının çalışması için self-hosted Windows runner gerekir
- `C:\actions-runner\cigertool-release\repo\inputs\workspace\install.wim` dosyası release öncesi hazır olmalıdır
- Self-hosted runner üzerinde Python 3.12+ önceden kurulmuş olmalıdır
- Self-hosted runner servisi yerel administrator haklarına sahip değilse gerçek ISO build başlamadan duracaktır
- Gerçek USB/VM boot smoke testi ayrıca yapılmalıdır

# CigerTool by hkannq

CigerTool, WinPE tabanli grafik arayuzlu disk klonlama ve kurtarma platformudur. Odak noktasi, buyuk diskten daha kucuk SSD'ye Windows tasimayi guvenli ve yonlendirmeli bir akisla gerceklestirmektir.

## One Cikarilan Ozellikler

- PySide6 ile gelistirilmis modern koyu tema arayuz
- RAW Clone, Smart Clone ve System Clone modlari
- Buyuk HDD -> kucuk SSD gecisi icin kullanilan veri bazli analiz
- USB diskleri kaynak veya hedef olarak kullanabilen planlayici
- Boot repair planlama katmani
- WinPE icinde otomatik acilan CigerTool shell akisi
- UEFI tarafinda `/isos/windows`, `/isos/linux`, `/isos/tools` klasorlerini tarayan pre-boot menu altyapisi
- Harici `tools` ve `isos` klasorleri ile genisletilebilir USB yapisi

## Klasor Yapisi

- `cigertool/`: ana Python uygulamasi
- `build/scripts/`: PyInstaller, ADK kurulumu ve WinPE ISO build scriptleri
- `winpe/files/`: WinPE image icine kopyalanan dosyalar
- `tools/`: lisans nedeniyle harici tutulabilecek portable araclar
- `isos/`: Windows, Linux ve tools ISO kutuphanesi
- `iso-library/`: legacy uyumluluk klasoru
- `.github/workflows/`: GitHub Actions uzerinden ISO artifact uretimi

## Build Ciktisi

Hedef artifact: `CigerTool-by-hkannq.iso`

GitHub Actions workflow'u Windows runner uzerinde:

1. Python bagimliliklarini kurar
2. Unit testleri kosar
3. Windows ADK + WinPE Add-on kurar
4. PyInstaller ile `CigerTool.exe` uretir
5. WinPE image'i olusturup uygulamayi shell olarak yerlestirir
6. Pre-boot menuyu olusturur ve `iso-library` icin boot entry ekler
7. ISO'yu artifact olarak yukler

Detaylar icin:

- `docs/KULLANIM.md`
- `docs/MIMARI.md`
- `docs/WINPE_BUILD.md`

## Onemli Notlar

Smart Clone motoru, Windows tarafinda dosya bazli tasima + yeniden bolumleme + `bcdboot` ile boot yenileme yaklasimini kullanir. Bu tasarim, sektor bazli kopyadan daha esnek davranir ve kucuk hedef diske sigdirma senaryosuna odaklanir.

Pre-boot ISO menu UEFI odakli bir GRUB entegrasyonudur. USB uzerine sonradan eklenen ISO dosyalari rebuild gerektirmeden menude gorulecek sekilde tasarlanmistir. Profil mantigi:

- Windows ISO: WIMBOOT tercih edilir, EFI fallback mevcuttur
- Ubuntu/Debian: kernel + initrd loopback
- Arch: archiso loopback parametreleri
- Digerleri: custom config veya fallback

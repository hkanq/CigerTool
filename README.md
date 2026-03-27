# CigerTool

CigerTool, USB'den boot edilebilen, Turkce, menulu bir disk klonlama ve Windows tasima aracidir. Amaç; teknik bilgisi dusuk kullanicilarin buyuk HDD'den daha kucuk SSD'ye gecerken guvenli, yonlendirmeli ve anlasilir bir deneyim yasamasidir.

Bu repo bir demo iskeleti degil. Gercek Linux araclarini kullanan, canli ISO ureten ve son kullanici odakli TUI arayuzu olan calisan bir proje omurgasi icerir.

## Odak Senaryosu

- 232 GB HDD -> 120 GB SSD gecisi
- Windows sistemini kaybetmeden daha kucuk diske tasima
- Tam disk klonlama
- Kullanilan veri bazli akilli klon
- Klon sonrasi boot onarma

## Ozellikler

- Tam Turkce varsayilan arayuz
- Textual tabanli tam ekran TUI
- Disk tarama ekraninda net model / seri / baglanti / kullanilan alan gosterimi
- Tam klon, akilli klon ve sadece Windows tasima modlari
- NTFS kucultme + hedefe sigdirma planlayicisi
- UEFI ve Legacy boot onarma planlari
- SMART disk sagligi okuma
- Dry-run / simulasyon modu
- Tek log dosyasi: `/var/log/cigertool.log`
- Debian live-build ile boot edilebilir `iso-hybrid` ISO cikisi
- Acilista dogrudan uygulamayi baslatan systemd servisi

## Teknik Yapi

- Ana uygulama: Python 3
- Arayuz: Textual
- Sistem araclari: `lsblk`, `sfdisk`, `smartctl`, `ntfsclone`, `ntfsresize`, `partclone`, `ddrescue`, `parted`, `grub-install`, `efibootmgr`
- ISO tabani: Debian Bookworm live-build

## Guvenlik Ilkeleri

- Kaynak ve hedef disk ayni secilemez
- Tam klon icin hedef disk daha kucukse islem engellenir
- Akilli klon icin planlanan veri hedefe sigmiyorsa islem engellenir
- Her yikici islem oncesi son onay penceresi vardir
- Hedef diskteki verinin silinecegi acikca belirtilir
- Akilli klonun kaynak NTFS bolumunu kucultebilecegi plan ozetinde yazilir

## Repo Yapisi

- `cigertool/`: ana Python uygulamasi
- `cigertool/services/`: disk tarama, planlama, SMART, boot ve log servisleri
- `cigertool/ui/`: Textual ekranlari
- `scripts/build_iso.sh`: boot edilebilir ISO uretir
- `scripts/write_usb.sh`: hibrit ISO'yu USB'ye yazar
- `iso/live-build/`: live-build konfigurasyonu ve autostart servis dosyalari
- `docs/KULLANIM.md`: adim adim kullanim rehberi
- `tests/`: planlayici ve parser testleri

## Hizli Baslangic

### 1. Python tarafini kur

```bash
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
pip install -e .
```

### 2. Uygulamayi yerelde ac

```bash
python -m cigertool
```

Not: Yerelde gercek diskleri okuyabilmek ve klon komutlarini calistirabilmek icin Linux ortaminda root yetkisi gerekir.

### 3. Testleri calistir

```bash
python -m unittest discover -s tests -p "test_*.py"
```

## ISO Indirme

Bu repoda otomatik build hattı vardir: [`.github/workflows/build-iso.yml`](/C:/Users/Radius%20Admin/Desktop/codex/CigerTool/.github/workflows/build-iso.yml)

- `main` dalina push oldugunda GitHub Actions otomatik olarak `cigertool.iso` artifact'i uretir.
- Tag ile build alinirsa ayni ISO GitHub Release asset'i olarak da yuklenir.
- Son kullanici tarafinda kaynak kod derleme ihtiyaci yoktur; dogrudan hazir ISO indirilebilir.

## ISO Build

Canli ISO uretimi icin Debian/Ubuntu tabanli bir Linux makinede su araclar kurulu olmalidir:

- `live-build`
- `rsync`
- `python3`

Build:

```bash
sudo ./scripts/build_iso.sh
```

Uretilen dosya:

```text
dist/cigertool.iso
```

ISO, `iso-hybrid` formatinda uretildigi icin BIOS ve UEFI ortamlarda USB'ye ham yazilarak kullanilabilir.

## USB Yazma

```bash
sudo ./scripts/write_usb.sh ./dist/cigertool.iso /dev/sdX
```

Betik, yazma isleminden once kullanicidan `EVET` onayi ister.

### Rufus ile Yazma

Hazir `cigertool.iso` dosyasini indirdikten sonra:

1. Rufus'u acin.
2. USB bellegi secin.
3. `Sec` diyerek `cigertool.iso` dosyasini gosterin.
4. Yazma modu sorulursa Rufus'un varsayilan onerisiyle devam edin.
5. USB hazir oldugunda BIOS/UEFI menusu uzerinden bu USB ile boot edin.

## Uretim Notlari

- Live ISO acildiginda `cigertool.service` tty1 uzerinde dogrudan uygulamayi baslatir.
- `getty@tty1.service` maskelenir; kullanici login ekrani gormez.
- Python paketi build sirasinda chroot icinde `pip` ile kurulur.

## Dikkat

Bu proje gercek disk operasyonlari icin tasarlanmistir. Yine de ilk kullanimi mutlaka test donanimi veya kopya diskler uzerinde yapin. Disk klonlama dogasi geregi yikici bir islemdir; ozellikle elektrik kesintisi, yanlis hedef disk secimi veya sagligi bozuk diskler veri kaybina yol acabilir.

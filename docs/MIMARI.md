# CigerTool Mimari Ozeti

## Katmanlar

### `cigertool/services`

- `disk_service.py`: `lsblk` ve `sfdisk` verilerini okuyup disk/bolum modellerine donusturur.
- `clone_service.py`: tam klon, akilli klon ve Windows tasima planlarini uretir.
- `resize_service.py`: NTFS kucultme planlarini hazirlar.
- `boot_service.py`: UEFI ve Legacy boot onarim planlarini uretir.
- `smart_service.py`: `smartctl` JSON ciktisini yorumlar.
- `execution_service.py`: uretilen adimlari sira ile calistirir ve UI'ye olay akisi yollar.

### `cigertool/ui`

- `scan_page.py`: disk envanteri
- `clone_page.py`: tam / akilli / Windows klon ekranlari
- `maintenance_pages.py`: resize ve boot onarim ekranlari
- `info_pages.py`: SMART ve log ekranlari

### ISO Katmani

- `iso/live-build/`: Debian live-build konfigurasyonu
- `cigertool.service`: tty1'de direkt uygulama acilisi
- `cigertool-launch`: Python uygulamasini canli sistemde baslatir

## Planlama Felsefesi

- Tam klon: `ddrescue` ile birebir kopya
- Akilli klon: gerekirse kaynak NTFS'i kucult, hedef disk icin yeni tablo yaz, bolumleri uygun aracla kopyala
- Windows tasima: EFI/MSR/OS/Recovery gibi gerekli bolumleri sec

## Neden Bu Yapi?

- UI ile yikici komutlari ayri tutmak test yazmayi kolaylastirir.
- Her operasyon once plan nesnesine donustugu icin son onay ve dry-run davranisi tek yerde korunur.
- Live ISO katmani ana uygulamadan bagimsiz tutuldugu icin farkli dagitimlara tasimak daha kolaydir.

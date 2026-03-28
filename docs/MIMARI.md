# Mimari Ozeti

## Katmanlar

- `cigertool/ui/`: PySide6 grafik arayuz
- `cigertool/services/`: disk tarama, clone planlama, SMART ozeti, multiboot tarama ve komut calistirma servisleri
- `build/scripts/`: Windows tarafli build ve clone scriptleri
- `winpe/files/`: WinPE shell override ve startup dosyalari

## Smart Clone Tasarimi

Python tarafi once kaynak diski analiz eder:

- EFI bolumu
- Windows bolumu
- Recovery bolumu
- Ek veri bolumleri

Ardindan hedef diske sigacak minimum layout tahmini uretir:

- EFI: en az 260 MB
- MSR: 16 MB
- Windows: kullanilan alan + guvenli bosluk
- Veri bolumleri: kullanilan alan + guvenli bosluk
- Recovery: en az 900 MB

PowerShell tarafinda `invoke_smart_clone.ps1`:

1. Hedef diski temizler
2. GPT layout olusturur
3. Uygun partition'lari formatlar
4. `robocopy` ile veri tasir
5. `bcdboot` ile Windows boot kaydini yeniler

## WinPE Boot Akisi

- `startnet.cmd` yalnizca `wpeinit` calistirir
- `winpeshl.ini` shell olarak `X:\CigerTool\CigerTool.exe` acilir
- Böylece kullanici terminal yerine dogrudan arayuzu gorur

## Multiboot Katmani

- `/isos/windows`: Windows ISO profili, WIMBOOT tercihli
- `/isos/linux`: Ubuntu/Debian ve Arch heuristikleri
- `/isos/tools`: utility ve rescue ISO'lari
- `*.grub.cfg`: ISO bazli ozel boot override
- `*.cigertool.json`: profil veya kernel/initrd override metadata

# Kullanim Rehberi

## Clone Wizard Akisi

1. Diskleri tara
2. Kaynak diski sec
3. Hedef diski sec
4. `Analiz Et` ile kapasite ve uyumluluk kontrolunu calistir
5. Onerilen yontemi incele
6. Dry-run ile komut planini simule et
7. Son onaydan sonra clone islemini baslat
8. Gerekirse `Boot Repair` ekranindan boot onarimi planini kullan

## Clone Modlari

- `SMART CLONE`: Kullanilan veri bazli klonlama. Kucuk SSD gecisinde varsayilan tercih.
- `RAW CLONE`: Birebir disk kopyasi. Hedef en az kaynak kadar buyuk olmali.
- `SYSTEM CLONE`: Yalnizca Windows acilisi icin gereken bolumleri tasir.

## Guvenlik

- Kaynak ve hedef ayni disk olamaz
- Hedef diskteki veri silinebilir
- Smart Clone ve System Clone kapasite analizi yapmadan ilerlemez
- Dry-run modunda hicbir yikici komut calismaz

## USB ve Harici Araclar

- Portable araclar `tools/` klasorune eklenebilir
- Kullanici ISO dosyalarini `isos/windows`, `isos/linux` veya `isos/tools` klasorlerine koyabilir
- Uygulama takili suruculeri ve USB kutuphanelerini tarayarak ISO yonetimini gosterir
- UEFI pre-boot menu, `isos/` altindaki ISO dosyalarini USB yeniden build edilmeden gormek uzere tasarlanmistir

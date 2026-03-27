# CigerTool Kullanim Rehberi

## 1. Hazirlik

- Kaynak diski ve hedef SSD'yi sisteme baglayin.
- Mumkunse eski Windows diski kapali bir sistemde, canli USB uzerinden boot edin.
- Hedef diskte veri varsa yedegini alin; klonlama sirasinda silinecektir.

## 2. USB'den Acilis

- BIOS/UEFI menusu uzerinden CigerTool USB'sini secin.
- Sistem acildiginda login ekrani gelmeden CigerTool otomatik acilir.

## 3. Ana Menu

### Diskleri Tara ve Bilgi Goster

- Bagli diskleri model, seri, baglanti tipi ve kullanilan alanla birlikte gosterir.
- Dogru kaynak/hedef secimi yapmadan once her zaman bu ekrani kontrol edin.

### Tam Disk Klonla

- Kaynak diski sektor sektor hedefe kopyalar.
- Hedef disk daha kucukse bu mod guvenlik nedeniyle calismaz.
- Eski diskle ayni boyutta veya daha buyuk disk degisimi icin uygundur.

### Akilli Klon

- Kullanim senaryosu: buyuk HDD -> daha kucuk SSD.
- NTFS bolumlerinde kullanilan veri miktarina bakarak hedefe sigacak bir plan cikarir.
- Gerekiyorsa kaynak NTFS dosya sistemini hedefe sigacak kadar kucultur.

### Sadece Windows / Sistem Tasi

- EFI, MSR, sistem ve recovery gibi gerekli Windows bolumlerini secerek tasir.
- Veri bolumlerini atlayabilir.
- SSD'ye sadece isletim sistemini almak isteyen kullanicilar icin en temiz mod budur.

### Disk Boyutlandirma ve Kucultme

- NTFS bolumunu onceden kucultmek isteyen kullanicilar icindir.
- Ozellikle klon oncesi sistem bolumunu hedef SSD'ye gore daraltmakta kullanilir.

### Boot Onarma

- Klon sonrasi Windows acilmazsa kullanilir.
- UEFI sistemlerde EFI kaydini tazeler.
- Legacy sistemlerde GRUB ile zincir yukleme yaparak kurtarma yolu olusturur.

### Disk Sagligi (SMART)

- Diskin riskli olup olmadigini, sicakligini ve bazi hata sayaclarini gosterir.
- Bozuk disklerde klonlama oncesi fikir verir.

### Loglari Goruntule

- Tum islemler `/var/log/cigertool.log` dosyasina yazilir.
- Hata oldugunda bu ekrandan son satirlari gorebilirsiniz.

## 4. Onerilen Gercek Senaryo

Ornek:

- Kaynak: 232 GB HDD
- Hedef: 120 GB SSD
- Amac: Windows'u kaybetmeden SSD'ye gecmek

Izlenecek yol:

1. `Diskleri Tara` ekraninda iki diski teyit edin.
2. `Akilli Klon` veya `Sadece Windows / Sistem Tasi` ekranina gecin.
3. Kaynak HDD'yi, hedef SSD'yi secin.
4. Ilk olarak `Dry-run` acik halde `Analiz Et` deyin.
5. Ozet ekranda planlanan boyut, uyarilar ve adimlari okuyun.
6. Her sey dogruysa son onaydan sonra islemi baslatin.
7. Islem bitince `Boot Onarma` ekranina gecip hedef diski secin.
8. BIOS/UEFI icinde yeni SSD'yi ilk boot sirasina alin.

## 5. Dry-run Nedir?

- Diskte degisiklik yapmadan hangi komutlarin calisacagini gosterir.
- Ilk denemede her zaman acik birakilmasi onerilir.

## 6. Sorun Giderme

### Hedef disk gorunmuyor

- `R` ile disk taramasini yenileyin.
- USB-SATA donusturucunuzun guc sorunu olmadigini kontrol edin.

### Tam klon engellendi

- Hedef disk kaynak diskten kucuktur.
- `Akilli Klon` ekranina gecin.

### Akilli klon da baslamiyor

- Kullanilan veri hedef SSD'ye sigmiyor olabilir.
- Once `Disk Boyutlandirma ve Kucultme` ekranindan sistem bolumunu kucultun.
- Gereksiz buyuk dosyalari silip yeniden deneyin.

### Windows acilmiyor

- `Boot Onarma` ekranini calistirin.
- Hedef diski tek basina baglayip tekrar deneyin.

## 7. Kisayollar

- `1-8`: menu gecisi
- `R`: disk verilerini yenile
- `F2`: tema degistir
- `F3`: TR / EN dil degistir
- `Q`: uygulamadan cik

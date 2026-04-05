# CigerTool Özellik Kapsamı

## Gerçekten Uygulananlar

### Klonlama

- ham kopya denetimi
- ham kopya yürütmesi
- akıllı kopya denetimi
- akıllı kopya yürütmesi
- canlı ilerleme görünümü
- iptal isteği
- denetim raporu kaydetme
- sonuç raporu kaydetme

### Yedekleme ve İmaj

- sürücüden ham `.img` alma
- sürücüden ham `.ctimg` alma
- sistem dışı sürücülerden akıllı `.ctimg` alma
- desteklenen `.img` ve `.ctimg` akışlarını geri yükleme
- `.img -> ham .ctimg` dönüştürme
- `ham .ctimg -> .img` dönüştürme
- doğrulama, yürütme, ilerleme ve sonuç raporu

### Diskler ve Sağlık

- bağlı sürücü listesi
- marka / model görünümü
- SSD / HDD / NVMe / USB sınıfı görünümü
- bağlantı tipi görünümü
- kapasite ve kullanım oranı
- Windows depolama sağlığına göre temel sağlık özeti
- bulunabilirse temel arıza öngörüsü sinyali
- sıralı ve 4K rastgele yerel benchmark
- hızlı, standart, derin ve sürdürülebilir hız testi profilleri

### USB Ortamı Oluştur

- çevrimiçi manifest kaynağı
- yerel geçersiz kılma desteği
- elle dosya seçimi
- imaj indirme
- SHA-256 doğrulaması
- USB aygıt algılama
- güvenli aygıt engelleme kuralları
- ham disk imajlarını doğrudan yazma
- hibrit önyüklenebilir ISO dosyalarını doğrudan yazma
- standart ISO kaynaklarını otomatik hazırlama
- Windows kurulum ISO'sunda büyük `install.wim` dosyasını bölme
- yazma sonrası temel doğrulama

### Ek Özellikler

- gelişmiş tanılama kartları
- isteğe bağlı yardımcı araç listesi
- çekirdek ve isteğe bağlı yetenek ayrımının kullanıcıya açık sunumu

## Kısmi Ama Dürüstçe Sunulanlar

- akıllı imaj:
  şu an sistem dışı sürücülerle ve `.ctimg` biçimiyle sınırlıdır
- masaüstünde çalışan sistem sürücüsü için ham klon ve ham imaj:
  bilinçli olarak engellenir, CigerTool OS önerilir
- standart ISO hazırlama:
  genel akış açılmıştır, ancak Rufus ile bire bir tüm özel medya ve çoklu önyükleme senaryoları henüz tamamlanmamıştır
- gelişmiş disk sağlığı:
  üreticiye özel SMART alanları ve CrystalDiskInfo düzeyi tam telemetri henüz yoktur
- gelişmiş benchmark:
  CrystalDiskMark ile bire bir tüm profil ve rapor yapısı henüz yoktur

## Planlanan Ürün Hedefleri

- tam disk akıllı imajını daha zengin yerel CigerTool biçimiyle alabilmek
- çoklu önyükleme profilleri
- canlı işletim sistemi USB profilleri
- daha güçlü SMART ve üretici telemetrisi
- daha geniş benchmark profilleri ve rapor yapısı

## Dürüstlük Kuralı

Ekranda görünen her alan şu üç durumdan birine açıkça oturmalıdır:

- uygulanmış
- kısmi veya sınırlı
- planlı

Yürütülemeyen bir işlev kullanıcıya tamamlanmış gibi sunulmaz.

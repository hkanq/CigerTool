# CigerTool

CigerTool, Windows için geliştirilen yerel bir disk işlemleri ürün ailesidir.

Ürün ailesi iki teslimattan oluşur:

- `CigerTool App`: Normal Windows 10/11 üzerinde çalışan masaüstü uygulaması
- `CigerTool OS`: Kullanıcının dışarıda hazırladığı Windows 10 PE tabanı içinde çalışan servis ortamı

## Sınır

Bu depo:

- WinPE üretmez
- Windows ADK kurmaz
- `boot.wim` oluşturmaz
- işletim sistemi imajı üretmez

Bu depo şunlardan sorumludur:

- CigerTool uygulaması
- disk klonlama, imaj alma, geri yükleme ve dönüştürme akışları
- USB ortamı oluşturma akışı
- yayın kaynağı ve manifest sistemi
- WinPE içine yerleştirme ve başlatma sözleşmesi

## Ana Modüller

- `Ana Sayfa`
- `Klonlama`
- `Yedekleme ve İmaj`
- `Diskler ve Sağlık`
- `USB Ortamı Oluştur`
- `Ek Özellikler`
- `Günlükler`
- `Ayarlar`

## Gerçekten Çalışan Çekirdek İşlemler

- ham kopya ile sürücüden sürücüye gerçek bayt kopyalama
- akıllı kopya ile dosya temelli sürücü eşleme
- sürücüden ham `.img` imaj alma
- sürücüden ham `.ctimg` imaj alma
- sistem dışı sürücülerden akıllı `.ctimg` imaj alma
- desteklenen `.img` ve `.ctimg` akışlarını geri yükleme
- `.img` ile ham `.ctimg` arasında dönüştürme
- USB için yayın bilgisi sorgulama, indirme, SHA-256 doğrulama, yazma ve yazma sonrası doğrulama
- disk listesinde marka/model, bağlantı tipi, SSD/HDD/NVMe sınıfı, sağlık özeti ve temel arıza öngörüsü sinyali
- sıralı, 4K rastgele ve sürdürülebilir hız odaklı yerel benchmark

## USB Yazma Kapsamı

Bugün gerçekten uygulanabilen USB akışları:

- ham disk imajlarını (`.img`, `.bin`, `.raw`) doğrudan yazma
- hibrit önyüklenebilir ISO kalıplarını doğrudan yazma
- standart ISO kalıpları için USB hazırlama, dosya kopyalama ve temel önyükleme akışı
- Windows kurulum ISO'ları için büyük `install.wim` dosyasını FAT32 uyumluluğu adına bölme

USB kaynak analizi bugün şu senaryoları ayırt eder:

- Windows kurulum ISO'su
- Linux canlı / kurulum ISO'su
- WinPE ISO'su
- araç / test ISO'su
- CigerTool OS ISO veya disk imajı

## Bilinen Kısmi Alanlar

- Rufus ile bire bir tüm medya ve çoklu önyükleme senaryoları
- üreticiye özel gelişmiş SMART telemetrisi
- CrystalDiskInfo ve CrystalDiskMark ile yüzde yüz eşdeğer ayrıntı ve rapor yapısı
- tam fiziksel disk bölüm tablosu yeniden kurma ve otomatik önyükleme onarımı
- BitLocker odaklı gelişmiş geçiş iş akışları

## Doğrulanan Güncel Build Çıktıları

Şu turda gerçekten doğrulanmış yeni build yolları:

- standart masaüstü yapı: [CigerTool.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/final/app/CigerTool.exe)
- WinPE odaklı yapı: [CigerTool.WinPE.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/final/winpe/CigerTool.WinPE.exe)

Not:

- `artifacts/app` ve `artifacts/winpe` altında açık olan eski süreçler varsa yeni doğrulanmış çıktılar `artifacts/final` altında bırakılır
- her iki çıktı da yönetici yetkisi ister
- yazılabilir uygulama verileri uygulama klasörüne değil işletim sistemi konumlarına gider

## Doğrulama

Son başarılı doğrulamalar:

- `dotnet build CigerTool.sln -c Release`
- `dotnet test CigerTool.sln -c Release --no-build`
- iki ayrı publish ile yeni final çıktı üretimi

## Ana Dokümanlar

- [docs/CLONE_MODEL.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/CLONE_MODEL.md)
- [docs/IMAGE_WORKFLOW.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/IMAGE_WORKFLOW.md)
- [docs/DISK_HEALTH_MODEL.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/DISK_HEALTH_MODEL.md)
- [docs/FEATURE_SCOPE.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/FEATURE_SCOPE.md)
- [docs/STATUS.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/STATUS.md)

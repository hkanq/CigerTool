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
- `Günlükler`
- `Ayarlar`

## Bugün Gerçekten Çalışan Çekirdek İşlemler

- ham kopya ile sürücüden sürücüye gerçek bayt kopyalama
- akıllı kopya ile dosya temelli sürücü eşleme
- sürücüden ham `.img` imaj alma
- sürücüden ham `.ctimg` imaj alma
- sistem dışı sürücülerden akıllı `.ctimg` imaj alma
- desteklenen `.img` ve `.ctimg` akışlarını geri yükleme
- `.img` ile ham `.ctimg` arasında dönüştürme
- USB için yayın bilgisi sorgulama, indirme, SHA-256 doğrulama, yazma ve yazma sonrası geri okuma doğrulaması
- disk listesinde marka/model, bağlantı tipi, SSD/HDD/NVMe sınıfı, sağlık özeti ve temel arıza öngörüsü sinyali
- sıralı ve 4K rastgele yerel performans testi

## USB Yazma Kapsamı

Bugün gerçekten doğrudan yazılabilen kaynaklar:

- ham disk imajları (`.img`, `.bin`, `.raw`)
- hibrit önyüklenebilir ISO kalıpları
- CigerTool OS için doğrudan disk imajı olarak sunulan yayınlar

Bugün henüz tam kapsama ulaşmayan USB senaryoları:

- standart ISO dosyaları için Rufus benzeri bölüm hazırlama ve dosya çıkarma akışı
- çoklu önyükleme profilleri
- canlı işletim sistemi USB profillerinin tamamı
- Rufus ile bire bir tam eşdeğer tüm medya senaryoları

Önemli ayrım:

- tam disk akıllı imaj alma, disk yedekleme alanıdır
- ISO yazma ise önyüklenebilir medya hazırlama alanıdır

Bu iki alan ürün içinde birlikte bulunur, ancak aynı şey değildir.

## Bilinen Kısmi Alanlar

- tam fiziksel disk bölüm tablosu yeniden kurma
- önyükleme onarımı
- BitLocker iş akışları
- üreticiye özel gelişmiş SMART telemetrisi
- CrystalDiskInfo ve CrystalDiskMark ile yüzde yüz eşdeğer yerel kapsama ulaşan sağlık ve benchmark katmanı
- tam kapsamlı Rufus eşdeğeri USB hazırlama profilleri

## Doğrulanan Final Build Çıktıları

Bu tur sonunda gerçekten doğrulanan yeni build yolları:

- standart masaüstü yapı: [CigerTool.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/final/app/CigerTool.exe)
- WinPE odaklı yapı: [CigerTool.WinPE.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/final/winpe/CigerTool.WinPE.exe)

Not:

- eski [artifacts/winpe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/winpe) altındaki yayın dosyası açık olduğu için bu turdaki doğrulanmış yeni çıktılar `artifacts/final` altında üretildi
- her iki çıktı da yönetici yetkisi ister
- yazılabilir uygulama verileri uygulama klasörüne değil işletim sistemi konumlarına gider

## Doğrulama

Bu turda doğrulananlar:

- `dotnet build CigerTool.sln -c Release`
- `dotnet test CigerTool.sln -c Release --no-build`
- iki ayrı publish profili ile yeni final çıktı üretimi

## Ana Dokümanlar

- [docs/CLONE_MODEL.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/CLONE_MODEL.md)
- [docs/IMAGE_WORKFLOW.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/IMAGE_WORKFLOW.md)
- [docs/DISK_HEALTH_MODEL.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/DISK_HEALTH_MODEL.md)
- [docs/FEATURE_SCOPE.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/FEATURE_SCOPE.md)
- [docs/STATUS.md](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/docs/STATUS.md)

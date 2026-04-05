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

## Ana Modüller

- `Ana Sayfa`
- `Klonlama`
- `Yedekleme ve İmaj`
- `Diskler ve Sağlık`
- `CigerTool OS USB`
- `Kurulum Medyası`
- `Günlükler`
- `Ayarlar`

## Bugün Gerçekten Çalışan Ana Alanlar

- ham ve akıllı klonlama akışları
- ham ve akıllı imaj alma / geri yükleme akışları
- disk sağlık özeti, marka/model, seri, firmware, sektör boyutu ve SMART ayrıntıları
- SEQ1M ve RND4K odaklı benchmark
- CigerTool OS USB hazırlama
- Windows, Linux, WinPE ve araç ISO'larını USB'ye hazırlama

## Dürüst Kalan Sınırlar

- Rufus ile bire bir tüm özel medya senaryoları tamamlanmış değildir
- CrystalDiskInfo ve CrystalDiskMark ile yüzde yüz aynı telemetri ve rapor yapısı yoktur
- üreticiye özel SMART alanları sürücü desteğine göre değişir

## Doğrulanan Çıktılar

- standart masaüstü yapı: [CigerTool.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/app/CigerTool.exe)
- WinPE odaklı yapı: [CigerTool.WinPE.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/winpe/CigerTool.WinPE.exe)

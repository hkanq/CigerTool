# CigerTool Durum

## Son Tur Özeti

Bu turda şu başlıklar tamamlandı:

- pencere kabuğu standart Windows başlık çubuğu davranışına geri alındı
- `USB Ortamı Oluştur` akışı kaynak sorgulama, hedef denetimi, indirme, yazma, doğrulama ve iptal yönetimiyle sadeleştirildi
- USB indirme ve yazma katmanına gerçek ilerleme bildirimi eklendi
- test projelerindeki Windows platform uyarıları kapatıldı
- çözüm `0 uyarı / 0 hata` ile derlendi, testlerden geçti ve iki yayın çıktısı yeniden üretildi

## Güncel Aşama

Bu turda ürün yüzeyi ve temel kullanım güvenilirliği birlikte güçlendirildi:

- tüm uygulama koyu temaya taşındı
- seçim kutuları ve kaydırma çubukları daha modern hale getirildi
- uygulama her zaman yönetici yetkisi isteyecek şekilde sabitlendi
- USB aygıt algılama tarafı daha toleranslı hale getirildi
- yedekleme denetiminde başarısızlık nedenleri kullanıcıya daha açık gösterilmeye başlandı

## Bu Turda Yapılanlar

- sol menü ve genel yüzey için koyu tema fırçaları tamamlandı
- kaba görünen kalın kaydırma çubukları inceltildi ve yuvarlatıldı
- seçim kutuları daha modern, koyu ve açıklamalı hale getirildi
- sürücü seçim kutularında model, kapasite ve sağlık özeti görünümü korundu
- USB aygıtı seçimi açılır kutusu daha zengin özet göstermeye başladı
- uygulama manifesti `requireAdministrator` düzeyine çekildi
- USB ekranı açılır açılmaz aygıt taraması başlatılmaya başladı
- USB aygıt algılama servisi modern depolama sorgusu ile eski WMI bilgisini birleştiren daha güçlü bir taramaya geçirildi
- varsayılan yayın manifest adresi GitHub `raw` yolu üzerinden sabitlendi
- GitHub `blob` adresi verilirse onu `raw` adresine dönüştüren davranış korundu
- yedekleme denetimi başarısızsa ilk kritik neden özet metnine de taşındı

## Yayın Çıktıları

- [artifacts/app/CigerTool.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/app/CigerTool.exe)
- [artifacts/winpe/CigerTool.WinPE.exe](C:/Users/Radius%20Admin/Desktop/codex/CigerTool/artifacts/winpe/CigerTool.WinPE.exe)

## Davranış Notları

- her iki yayın çıktısı da açılış için zorunlu yan dosya gerektirmez
- her iki yayın da yönetici yetkisi ister
- masaüstü yapısı yazılabilir verileri `%LocalAppData%\CigerTool` altında tutar
- WinPE odaklı yapı yazılabilir verileri `%TEMP%\CigerTool` altında tutar
- uygulama klasörü içine günlük veya veri klasörü açma önceliği yoktur

## Ürün Yönü Notu

Kullanıcı talebine göre ürün hedefi genişletildi:

- tam disk akıllı imaj
- ISO yazma
- Rufus benzeri USB hazırlama kapsamı
- USB üzerinden çalışan servis sistemi akışları
- daha zengin disk sağlık ve performans araçları

Bu noktada önemli ayrım şudur:

- tam disk akıllı imaj, disk imajı alanıdır ve yerel imaj biçimiyle ele alınacaktır
- ISO yazma, önyüklenebilir medya hazırlama alanıdır

Bu nedenle `akıllı disk yedeği` ile `ISO yazma` aynı başlık altında karıştırılmayacaktır.

## Doğrulama

Bu tur sonunda yeniden çalıştırılması gereken doğrulamalar:

- `dotnet build CigerTool.sln -c Release`
- `dotnet test CigerTool.sln -c Release --no-build`
- `powershell -ExecutionPolicy Bypass -File build/scripts/Publish-CigerTool.ps1`

## Bilinen Kalan Riskler

- USB köprü denetleyicileri ve farklı harici disk kutuları üzerinde daha geniş fiziksel test gerekir
- tam kapsamlı ISO yazma ve gelişmiş önyükleme profilleri henüz açılmadı
- tam disk akıllı imaj kapsamı henüz tüm bölüm düzeni senaryolarını taşımıyor
- gelişmiş SMART telemetrisi ve yerel benchmark katmanı henüz temel seviyenin üzerinde değil

## Bilinçli Ürün Sınırı

Bu depo:

- WinPE üretmez
- Windows ADK kurmaz
- `boot.wim` oluşturmaz
- işletim sistemi imajı üretmez

Bu depo yalnızca:

- uygulamayı
- yayın kaynağı modelini
- USB oluşturma akışını
- WinPE içine yerleştirme sözleşmesini

taşır.

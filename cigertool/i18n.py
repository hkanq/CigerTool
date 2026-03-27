from __future__ import annotations


TRANSLATIONS = {
    "app_title": {"tr": "CigerTool", "en": "CigerTool"},
    "app_subtitle": {
        "tr": "Guvenli disk klonlama ve Windows tasima araci",
        "en": "Safe disk cloning and Windows migration tool",
    },
    "menu_scan": {"tr": "1. Diskleri Tara ve Bilgi Goster", "en": "1. Scan Disks"},
    "menu_full": {"tr": "2. Tam Disk Klonla", "en": "2. Full Disk Clone"},
    "menu_smart": {"tr": "3. Akilli Klon", "en": "3. Smart Clone"},
    "menu_windows": {"tr": "4. Sadece Windows / Sistem Tasi", "en": "4. Windows Migration"},
    "menu_resize": {"tr": "5. Disk Boyutlandirma ve Kucultme", "en": "5. Resize and Shrink"},
    "menu_boot": {"tr": "6. Boot Onarma", "en": "6. Boot Repair"},
    "menu_health": {"tr": "7. Disk Sagligi (SMART)", "en": "7. Disk Health"},
    "menu_logs": {"tr": "8. Loglari Goruntule", "en": "8. Logs"},
    "menu_exit": {"tr": "9. Cikis", "en": "9. Exit"},
    "source_disk": {"tr": "Kaynak Disk", "en": "Source Disk"},
    "target_disk": {"tr": "Hedef Disk", "en": "Target Disk"},
    "partition": {"tr": "Bolum", "en": "Partition"},
    "target_size": {"tr": "Hedef Boyut", "en": "Target Size"},
    "dry_run": {"tr": "Dry-run / Simulasyon", "en": "Dry run"},
    "analyze": {"tr": "Analiz Et", "en": "Analyze"},
    "run": {"tr": "Islemi Baslat", "en": "Run"},
    "refresh": {"tr": "Yenile", "en": "Refresh"},
    "confirm": {"tr": "Son Onay", "en": "Final Confirmation"},
    "cancel": {"tr": "Iptal", "en": "Cancel"},
    "summary": {"tr": "Ozet", "en": "Summary"},
    "warnings": {"tr": "Uyarilar", "en": "Warnings"},
    "steps": {"tr": "Adimlar", "en": "Steps"},
    "recommendation": {"tr": "Oneri", "en": "Recommendation"},
    "completed": {"tr": "Tamamlandi", "en": "Completed"},
    "failed": {"tr": "Basarisiz", "en": "Failed"},
    "no_disks": {
        "tr": "Disk bulunamadi. Canli ISO icinde root yetkisiyle calistirdiginizdan emin olun.",
        "en": "No disks found. Make sure the app runs with root privileges in the live ISO.",
    },
    "scan_help": {
        "tr": "Bu ekran bagli diskleri tarar, model/seri/baglanti ve kullanilan alan bilgilerini acikca gosterir.",
        "en": "This view scans attached disks and shows model, serial, transport and used space.",
    },
    "full_help": {
        "tr": "Sektor bazli birebir kopya alir. Hedef disk daha kucukse guvenlik icin engellenir.",
        "en": "Makes a sector-by-sector clone. Blocked if the target is smaller.",
    },
    "smart_help": {
        "tr": "Sadece kullanilan veriyi tasimaya calisir. Kucuk SSD'ye geciste ilk onerilen yontemdir.",
        "en": "Copies only used data. Best default for moving to a smaller SSD.",
    },
    "windows_help": {
        "tr": "Yalnizca Windows icin gereken sistem bolumlerini tasir ve sonrasinda boot onarma onerir.",
        "en": "Moves only the system partitions required by Windows and suggests boot repair.",
    },
    "resize_help": {
        "tr": "NTFS bolumlerini hedef diske sigacak sekilde kucultmek icin guvenli bir plan uretir.",
        "en": "Builds a safe NTFS shrink plan so the destination can fit.",
    },
    "boot_help": {
        "tr": "UEFI ve Legacy kurulumlar icin boot kaydini toparlamaya yardim eder.",
        "en": "Helps repair boot records for UEFI and Legacy installs.",
    },
    "health_help": {
        "tr": "SMART verilerini okuyup riskli diskleri erken fark etmenize yardim eder.",
        "en": "Reads SMART data to spot risky disks early.",
    },
    "log_help": {
        "tr": "Tum operasyonlar /var/log/cigertool.log dosyasina yazilir ve bu ekrandan izlenebilir.",
        "en": "All operations are written to /var/log/cigertool.log and can be inspected here.",
    },
    "execution_ready": {
        "tr": "Plan hazir. Son onaydan sonra calistirilabilir.",
        "en": "Plan is ready and can run after final confirmation.",
    },
    "execution_required": {
        "tr": "Once analiz yapin, sonra son onay ile islemi baslatin.",
        "en": "Analyze first, then confirm and run the operation.",
    },
    "theme": {"tr": "Tema", "en": "Theme"},
    "language": {"tr": "Dil", "en": "Language"},
    "footer_quit": {"tr": "Q Cikis", "en": "Q Quit"},
    "footer_refresh": {"tr": "R Yenile", "en": "R Refresh"},
    "footer_theme": {"tr": "F2 Tema", "en": "F2 Theme"},
    "footer_language": {"tr": "F3 Dil", "en": "F3 Language"},
}


def t(key: str, language: str = "tr", **kwargs) -> str:
    translations = TRANSLATIONS.get(key, {})
    template = translations.get(language) or translations.get("tr") or key
    return template.format(**kwargs)

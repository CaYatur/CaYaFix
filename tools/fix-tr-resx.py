# -*- coding: utf-8 -*-
"""Merge missing EN localization keys into Strings.tr.resx with proper Turkish UTF-8."""
from __future__ import annotations

import re
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
EN_PATH = ROOT / "CaYaFix.App" / "Properties" / "Strings.resx"
TR_PATH = ROOT / "CaYaFix.App" / "Properties" / "Strings.tr.resx"

# Full Turkish translations for keys missing after restoring the last clean TR file.
TRANSLATIONS: dict[str, str] = {
    "Check_Display_Modes": "Ekran çözünürlüğü ve kullanılabilir modlar",
    "Check_Display_Monitors": "Bağlı monitörler",
    "Check_Performance_CoreServices": "Temel Windows servisleri sağlığı",
    "Dialog_SupportPackage_SaveFilter": "ZIP arşivi (*.zip)|*.zip",
    "Dialog_SupportPackage_SaveTitle": "Destek paketini kaydet",
    "Finding_Display_BasicAdapter": (
        "Windows Microsoft Temel Görüntü Bağdaştırıcısı kullanıyor; "
        "bu normal çözünürlük seçeneklerini kilitler veya soluklaştırır."
    ),
    "Finding_Display_GenericMonitors": (
        "Monitörler yalnızca genel/bilinmeyen olarak tanınıyor — EDID/mod listeleri eksik olabilir."
    ),
    "Finding_Display_ModesLocked": (
        "Çok az ekran modu listeleniyor — donanım daha fazlasını desteklese bile "
        "çözünürlük takılı veya gri kalabilir."
    ),
    "Finding_Display_MonitorError": (
        "Bağlı bir monitör aygıtı devre dışı veya hatalı; bu çözünürlük seçeneklerini kilitleyebilir."
    ),
    "Finding_Display_NoMonitor": "Mod listesi için mevcut monitör aygıtı bulunamadı.",
    "Finding_Display_PolicyLocked": (
        "Bir ilke ekran ayarlarını değiştirmeyi engelliyor gibi görünüyor "
        "(çözünürlük kasıtlı olarak gri olabilir)."
    ),
    "Finding_Display_SubNativeResolution": (
        "Geçerli çözünürlük, sürücünün listelediği en yüksek modun çok altında (tek veya çoklu ekran)."
    ),
    "Finding_Integrity_DismFailed": "DISM güvenilir bir bileşen deposu sağlık denetimi tamamlayamadı.",
    "Finding_Integrity_NotRepairable": (
        "Windows bileşen deposu bozuk ve DISM çevrimiçi onarılamayacağını bildiriyor."
    ),
    "Finding_Performance_CoreServices": "Bir veya daha fazla temel Windows servisi devre dışı veya durmuş",
    "Fix_Audio_RepairAllIo": "Gelişmiş onarım: tüm ses girişi ve çıkışı",
    "Fix_Audio_RepairInput": "Gelişmiş onarım: yalnızca ses girişi",
    "Fix_Audio_RepairOutput": "Gelişmiş onarım: yalnızca ses çıkışı",
    "Fix_Bluetooth_EnableAdapters": "Devre dışı Bluetooth bağdaştırıcılarını etkinleştir",
    "Fix_Bluetooth_RestartRadios": "Bluetooth radyolarını yeniden başlat",
    "Fix_Bluetooth_RestartStack": "Bluetooth destek servis yığınını yeniden başlat",
    "Fix_Bluetooth_ScanDevices": "Bluetooth aygıtlarını yeniden tara",
    "Fix_Bluetooth_StartSupportServices": "Bluetooth destek servislerini başlat",
    "Fix_Boot_BootStatusPolicyDisplay": "Önyükleme durum ilkesini DisplayAllFailures yap",
    "Fix_Boot_BootStatusPolicyIgnore": "Önyükleme durum ilkesini IgnoreAllFailures yap",
    "Fix_Boot_DisableRecoveryEnabled": "Geçerli önyükleme girdisinde recoveryenabled kapat",
    "Fix_Boot_EnumCurrent": "Geçerli BCD girdisini numaralandır",
    "Fix_Boot_ReagentcInfo": "Windows RE durumunu yenile (reagentc /info)",
    "Fix_Camera_AllowDesktopApps": "Masaüstü uygulamaları için kameraya izin ver",
    "Fix_Camera_CycleCaptureServices": "Kamera yakalama servislerini döngüle",
    "Fix_Camera_EnableDisabled": "Devre dışı kamera aygıtlarını etkinleştir",
    "Fix_Camera_RestartFrameServer": "Kamera Frame Server servislerini yeniden başlat",
    "Fix_Camera_ScanDevices": "Kamera aygıtlarını yeniden tara",
    "Fix_Disk_CleanUserTemp": "Kullanıcı temp klasöründeki eski dosyaları temizle",
    "Fix_Disk_CleanWindowsTemp": "Windows\\Temp içindeki eski dosyaları temizle",
    "Fix_Disk_ClearVolumeHints": "Birim boş alan ve dirty bit sorgula",
    "Fix_Disk_FlushVolume": "Sistem birimi yazma önbelleğini boşalt",
    "Fix_Disk_SpotFixOnly": "Yalnızca chkdsk /spotfix çalıştır",
    "Fix_Display_RepairResolution": (
        "Takılı çözünürlüğü onar (bağdaştırıcı, monitör, soft-reset, yeniden tara)"
    ),
    "Fix_Display_RestoreRecommended": "Her ekranda desteklenen en yüksek çözünürlüğü uygula",
    "Fix_Integrity_CheckHealthRefresh": "DISM CheckHealth durumunu yenile",
    "Fix_Integrity_ComponentCleanupResetBase": "DISM StartComponentCleanup /ResetBase",
    "Fix_Integrity_ScanHealthOnly": "Yalnızca DISM ScanHealth",
    "Fix_Integrity_SfcThenDism": "SFC ardından DISM RestoreHealth zinciri",
    "Fix_Integrity_SfcVerifyOnly": "SFC /verifyonly (onarım olmadan tarama)",
    "Fix_Network_ProfileRepairActive": "Aktif ağ profilini onar (DHCP, DNS, güvenlik duvarı)",
    "Fix_Network_ProfileRepairAll": "Tüm ağ profillerini onar (DHCP, DNS, güvenlik duvarı)",
    "Fix_Network_RenewDhcpAll": "Tüm ağ bağdaştırıcılarında DHCP adresini yenile",
    "Fix_Performance_HibernateOff": "Hazırda bekletmeyi kapat (hiberfil alanı)",
    "Fix_Performance_HighPerformancePlan": "Yüksek performans güç planına geç",
    "Fix_Performance_QueryEnergy": "Kısa powercfg enerji raporu çalıştır",
    "Fix_Performance_RepairCoreServices": (
        "Temel Windows servislerini onar (durmuşları başlat / devre dışını aç)"
    ),
    "Fix_Performance_RestartCoreServices": "Temel Windows servislerini yeniden başlat",
    "Fix_Performance_RestartSchedule": "Görev Zamanlayıcıyı yeniden başlat",
    "Fix_Performance_RestartSysMain": "SysMain (Superfetch) yeniden başlat",
    "Fix_Performance_RestoreDefaultSchemes": "Varsayılan Windows güç planlarını geri yükle",
    "Fix_Printer_CancelErrorJobs": "Hatalı/engelli yazdırma işlerini iptal et",
    "Fix_Printer_EnsureSpoolFolder": "spool PRINTERS klasörünün varlığını doğrula",
    "Fix_Printer_PurgeSpool": "Yazdırma kuyruk klasörünü temizle",
    "Fix_Printer_RestartPrintPipeline": "Yazdırma hattı servislerini yeniden başlat",
    "Fix_Printer_ScAutoSpooler": "Yazdırma Biriktiricisini Otomatik yap ve başlat",
    "Fix_Search_ClearTemp": "Windows Arama geçici dizin verilerini temizle",
    "Fix_Search_RebuildIndex": "Windows Arama dizinini yeniden oluştur (yeniden başlatma gerekir)",
    "Fix_Search_RestartAndRebuild": "Arama'yı yeniden başlat ve dizini yeniden oluştur",
    "Fix_Search_SetAutomatic": "Windows Arama'yı Otomatik yap ve başlat",
    "Fix_Store_ClearLocalCache": "Microsoft Store yerel paket önbelleğini temizle",
    "Fix_Store_RegisterManifest": "Microsoft Store AppxManifest'i yeniden kaydet",
    "Fix_Store_ResetAppxPackage": "Microsoft Store uygulama paketini sıfırla",
    "Fix_Store_RestartServices": "Microsoft Store AppX servislerini yeniden başlat",
    "Fix_Store_StartAppxServices": "AppX / Store ile ilgili servisleri başlat",
    "Fix_Time_ConfigUpdate": "Windows Zaman yapılandırmasını uygula ve yeniden eşitle",
    "Fix_Time_ReRegister": "Windows Zaman servisini varsayılanlarla yeniden kaydet",
    "Fix_Time_RestartService": "Windows Zaman servisini yeniden başlat",
    "Fix_Time_ResyncRediscover": "Zamanı yeniden eşitle ve NTP eşlerini yeniden bul",
    "Fix_Time_SetManualNtp": "time.windows.com'u manuel NTP eşi yap",
    "Fix_Update_CleanDownload": "Windows Update indirme önbelleğini temizle",
    "Fix_Update_RestartBits": "BITS ve Windows Update'i yeniden başlat",
    "Fix_Update_RestartCryptSvc": "Şifreleme Hizmetleri ve Update'i yeniden başlat",
    "Fix_Update_RestartUso": "Update Orchestrator yığınını yeniden başlat",
    "Fix_Update_ScDefaults": "Windows Update servis başlatma kiplerini varsayılana al",
    "Fix_Usb_CycleRootHubs": "USB root hub'ları yeniden başlat ve tara",
    "Fix_Usb_EnableDisabled": "Devre dışı USB aygıtlarını etkinleştir",
    "Fix_Usb_RestartDiscoveryServices": "USB keşif servislerini yeniden başlat",
    "Fix_Usb_RestartHubs": "USB hub'ları yeniden başlat",
    "Fix_Usb_ScanDevices": "USB aygıtlarını yeniden tara",
    "Symptom_Display_ResolutionStuck": (
        "Çözünürlük değiştirilemiyor (gri/takılı) oysa ekran daha fazlasını destekliyor"
    ),
    "Symptom_Integrity_Cleanup": "Windows bileşen deposu büyük / temizlik öneriliyor",
    "Symptom_Performance_Services": "Windows servisleri bozuk veya başlamıyor",
    "Symptom_Search_SlowIndex": "Arama dizini yavaş veya bitmiyor",
    "Symptom_Store_InstallFail": "Microsoft Store uygulamaları yüklenmiyor veya güncellenmiyor",
    "Symptom_Time_NoSource": "Geçerli zaman kaynağı / NTP eşi yok",
}

# Prefer updated wording for keys that already exist in the clean base.
UPDATES: dict[str, str] = {
    "Finding_Integrity_Repairable": (
        "Windows bileşen deposu onarılabilir (DISM çevrimiçi düzeltilebilecek bozulma bildirdi)."
    ),
    "Finding_Integrity_ScanHealthFailed": (
        "DISM ScanHealth bileşen deposu bozulması buldu; onarılmalı (temiz tarama değil)."
    ),
    "Finding_Integrity_CleanupRecommended": (
        "DISM WinSxS bileşen deposu temizliği öneriyor (alan kazanımı — dosya bozulması değil)."
    ),
    "Fix_Network_RenewDhcp": "Aktif ağ profilinde DHCP adresini yenile",
    "Fix_Network_RestartServices": "Ağ servislerini yeniden başlat / onar",
    "Module_Display_Description": (
        "Grafik bağdaştırıcılar, çözünürlük/mod sağlığı, monitörler ve ekran sürücüsü sıfırlamaları."
    ),
}


def resx_keys(text: str) -> set[str]:
    skip = {"resmimetype", "version", "reader", "writer"}
    return {m.group(1) for m in re.finditer(r'name="([^"]+)"', text)} - skip


def replace_value(text: str, key: str, value: str) -> str:
    pattern = re.compile(
        rf'(<data name="{re.escape(key)}" xml:space="preserve"><value>)(.*?)(</value></data>)',
        re.DOTALL,
    )
    return pattern.sub(rf"\g<1>{escape(value)}\g<3>", text, count=1)


def main() -> None:
    en = EN_PATH.read_text(encoding="utf-8")
    tr = TR_PATH.read_text(encoding="utf-8")
    en_keys = resx_keys(en)
    tr_keys = resx_keys(tr)

    for key, value in UPDATES.items():
        if key in tr_keys:
            tr = replace_value(tr, key, value)

    missing = sorted(en_keys - tr_keys)
    unknown = [k for k in missing if k not in TRANSLATIONS]
    if unknown:
        raise SystemExit(f"Missing Turkish translations for: {', '.join(unknown)}")

    block = []
    for key in missing:
        block.append(
            f'  <data name="{key}" xml:space="preserve"><value>{escape(TRANSLATIONS[key])}</value></data>'
        )
    if block:
        if "</root>" not in tr:
            raise SystemExit("TR resx missing </root>")
        tr = tr.replace("</root>", "\n".join(block) + "\n</root>")

    # Normalize to UTF-8 with BOM for Visual Studio / ResX tooling friendliness.
    TR_PATH.write_text(tr, encoding="utf-8-sig")

    final = TR_PATH.read_text(encoding="utf-8-sig")
    tr_keys2 = resx_keys(final)
    tr_chars = len(re.findall(r"[ğüşıöçĞÜŞİÖÇ]", final))
    bad = bool(re.search(r"Ã.|Â.|ÄŸ|ÅŸ|�", final))
    print(f"EN={len(en_keys)} TR={len(tr_keys2)} missing={len(en_keys - tr_keys2)}")
    print(f"TR_chars={tr_chars} corruption={bad} added={len(missing)}")
    if en_keys != tr_keys2:
        raise SystemExit(f"Key mismatch remains: {sorted(en_keys - tr_keys2)}")


if __name__ == "__main__":
    main()

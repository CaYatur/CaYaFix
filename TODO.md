<!-- Copyright (c) 2026 CaYaDev (https://cayadev.com) | GitHub: CaYatur (https://github.com/CaYatur) | Licensed under the MIT License. -->

# CaYaFix — Windows Sorun Giderme ve Onarım Aracı — TODO

> **Bu dosya, kodu yazacak AI için tam talimattır.** Kararlar verilmiştir; sorgulamadan sırayla uygula.
> Fazları sırayla bitir, her fazın "Kabul Kriteri"ni sağlamadan sonrakine geçme.

---

## 0. Ürün Özeti

Windows 10/11 için, yaygın ama çözümü karmaşık sorunları **otomatik teşhis eden**, **kullanıcının gözü önünde test yapan**, **kademeli (güvenli → agresif) onaran** ve **her değişikliği geri alabilen** masaüstü araç.

Temel prensipler:
1. **Önce teşhis, sonra onarım** — kör komut çalıştırma yok.
2. **Her onarım öncesi yedek** — yedeği alınamayan hiçbir değişiklik uygulanmaz.
3. **Her onarım sonrası doğrulama** — fix uygulandıktan sonra ilgili test tekrar koşulur.
4. **Şeffaflık** — çalıştırılan her komut ve çıktısı canlı "İşlem Konsolu"nda kullanıcıya akar.

---

## 1. Mimari (kesin kararlar — değiştirme)

- **Dil/Platform:** C# / .NET 8, **WPF** (MVVM, `CommunityToolkit.Mvvm` paketi).
- **Yükseltilmiş yetki:** `app.manifest` → `requireAdministrator`.
- **Dağıtım:** tek dosya self-contained publish (`win-x64`), portable exe.
- **Paketler:** `CommunityToolkit.Mvvm`, `NAudio` (ses cihazları + test sesi + mikrofon ölçümü), `Serilog` + `Serilog.Sinks.File`, `System.Management` (WMI).
- **Sistem verisi toplama yöntemi:** Yapısal veri gerekiyorsa PowerShell komutu `| ConvertTo-Json -Depth 4` ile çalıştırılıp JSON parse edilir (örn. `Get-NetAdapter`). Basit işlerde doğrudan exe (`netsh`, `ipconfig`, `ping`).
- **Dil (UI):** Türkçe varsayılan. Tüm stringler tek `Strings.tr.resx` içinde (ileride `en` eklenebilir).

### Proje yapısı
```
CaYaFix.sln
├─ CaYaFix.Core/        // motor: aksiyon modeli, runner, yedek/geri alma, log, rapor
├─ CaYaFix.Modules/     // her modül ayrı klasör: Network/, Audio/, WindowsUpdate/ ...
├─ CaYaFix.App/         // WPF UI
└─ CaYaFix.Tests/       // xUnit birim testleri (Core motoru mock'lanmış ICommandRunner ile)
```

### Çekirdek soyutlamalar (CaYaFix.Core)
```csharp
interface ICommandRunner {            // TÜM dış komutlar buradan geçer
    Task<CmdResult> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct);
    Task<T?> RunPsJsonAsync<T>(string psCommand, CancellationToken ct); // PS + ConvertTo-Json
}
// CmdResult: ExitCode, StdOut, StdErr, Duration. Her çağrı Serilog'a ve İşlem Konsolu'na yazılır.

enum Severity { Info, Warning, Critical }
enum RiskTier { Safe = 1, Moderate = 2, Aggressive = 3 }

abstract class DiagnosticCheck {
    string Id; string Title; string ModuleId;
    Task<Finding?> RunAsync(DiagContext ctx, CancellationToken ct); // null = sorun yok
}
// Finding: CheckId, Severity, UserMessage (Türkçe, teknik olmayan açıklama),
//          TechnicalDetail, RecommendedFixIds (sıralı)

abstract class FixAction {
    string Id; string Title; RiskTier Tier; bool RequiresReboot;
    Task<BackupEntry?> BackupAsync(FixContext ctx);   // ÖNCE çağrılır; başarısızsa Apply iptal
    Task<FixResult>   ApplyAsync(FixContext ctx, CancellationToken ct);
    Task<bool>        VerifyAsync(FixContext ctx, CancellationToken ct); // ilgili check'i tekrar koşar
    Task<bool>        UndoAsync(BackupEntry backup, CancellationToken ct);
}

abstract class LiveTest {  // kullanıcı önünde canlı çalışan testler (ping, hoparlör testi vb.)
    string Id; string Title;
    IAsyncEnumerable<TestProgress> RunAsync(CancellationToken ct); // UI'ya canlı akar
}
```

### Orkestrasyon (Engine)
- `DiagnosticEngine`: seçili modüllerin tüm check'lerini paralel-güvenli sırayla koşar, `Finding` listesi üretir.
- `FixEngine`: seçilen fix'leri **Tier sırasına göre** (önce Safe) uygular: `Backup → Apply → Verify`. Verify başarısızsa finding "çözülemedi" işaretlenir, bir üst tier önerilir.
- `SessionManager`: her onarım oturumu için `%ProgramData%\CaYaFix\Sessions\{yyyyMMdd-HHmmss}\` klasörü + `manifest.json` (uygulanan aksiyonlar, yedek dosya yolları, sonuçlar).
- Tüm işlemler iptal edilebilir (CancellationToken), UI hiçbir zaman bloklanmaz.

---

## 2. Yedekleme & Geri Alma Sistemi (P0)

- [ ] Oturum başında **Sistem Geri Yükleme Noktası** oluştur (`Checkpoint-Computer`). Sistem Koruması kapalıysa kullanıcıyı uyar, onayla devam et.
- [ ] `BackupEntry` türleri ve implementasyonu:
  - **RegistryBackup:** `reg export "<key>" "<file>.reg" /y`; undo = `reg import`.
  - **FileBackup:** dosya kopyası (örn. `hosts`); undo = geri kopyala.
  - **CommandStateBackup:** durumu dump eden komut + geri yükleyen komut çifti (örn. `netsh -c interface dump > net.txt` ↔ `netsh -f net.txt`; `netsh advfirewall export fw.wfw` ↔ `import`).
  - **ServiceStateBackup:** servislerin StartType + durumu JSON'a; undo = eski değerlere döndür.
  - **DriverBackup:** `pnputil /export-driver <oem#.inf> <dir>`; undo = `pnputil /add-driver ... /install`.
  - **ValueBackup:** tek değer (örn. varsayılan ses cihazı ID'si) JSON'a; undo = değeri geri set et.
- [ ] **Geri Alma Merkezi (UI):** oturumları ve içindeki aksiyonları listele; tek aksiyon veya tüm oturum geri alınabilir. Oturum geri alınırken aksiyonlar **ters sırayla** undo edilir.
- [ ] Yedeği alınamayan aksiyon uygulanmaz; kullanıcıya "yedeksiz devam et" seçeneği YALNIZCA Tier 3'te ve açık uyarıyla sunulur.

---

## 3. Uygulama Akışı (UX — kesin karar)

### Ana ekran
- Büyük **"Otomatik Tara"** butonu + modül kartları ızgarası (manuel giriş için).
- Alt tarafta kalıcı, açılır-kapanır **İşlem Konsolu** (çalıştırılan komut + çıktı canlı akar).

### Akış A — Otomatik
1. Tüm modüllerin hızlı check'leri koşar (canlı ilerleme: modül adı + check adı + ✓/⚠/✖).
2. **Bulgular ekranı:** her bulgu = kullanıcı dilinde açıklama + önem derecesi + önerilen düzeltmeler. Safe tier düzeltmeler önceden işaretli gelir.
3. Kullanıcı "Onar" der → FixEngine Tier 1'leri uygular, her fix sonrası Verify.
4. **Doğrulama ekranı:** çözülen/çözülemeyen listesi.
5. Çözülemeyen varsa → **Kademe yükseltme paneli:** "Sorun devam ediyor. Orta seviye onarımlar denensin mi?" → Tier 2 → tekrar Verify → hâlâ çözülmediyse → **"Zorla Onarım Modu" (Tier 3):** büyük uyarı metni + zorunlu tam yedek + geri yükleme noktası şartı + açık onay kutusu ("Riskleri anladım"). İlgili modülün TÜM tier 3 adımları sırayla uygulanır.
6. Hâlâ çözülmediyse: **Teslim ekranı** — "Sorun yazılımsal olarak çözülemedi" + olası donanım nedenleri listesi + **Destek Paketi** üret (bkz. §8).

### Akış B — Manuel
- Kullanıcı modül seçer → **belirti listesi**nden seçer (örn. "İnternet yok", "Ses gelmiyor", "Mikrofonum çalışmıyor", "İnternet var ama yavaş"). Her belirti bir **Playbook**'a eşlenir: `belirti → koşulacak check'ler → önerilen fix zinciri`. Sonrası Akış A adım 2'den aynı.
- Manuel modda kullanıcı tek tek fix seçip de çalıştırabilir (uzman modu listesi: tüm fix'ler tier etiketiyle).

### Genel kurallar
- `RequiresReboot` fix'ler sona kuyruklanır; oturum sonunda tek yeniden başlatma istenir. Uygulama açılışta yarım kalmış oturum görürse "Doğrulamayı tamamla" önerir (basit: manifest'te `pendingVerify` bayrağı).
- **Önizleme (dry-run) modu:** ayarlardan açılırsa Apply çağrılmaz, çalıştırılacak komutlar konsola yazılır.

---

## 4. Modül: Ağ (P0 — en kapsamlı modül)

### 4.1 Teşhis check'leri
- [ ] **Adaptör envanteri:** `Get-NetAdapter` — durum, LinkSpeed, sürücü sürümü/tarihi (>3 yıl eskiyse Warning), MediaType. Devre dışı ama fiziksel adaptör → Finding.
- [ ] **IP yapılandırması:** APIPA (169.254.x.x) tespiti, boş gateway, DHCP başarısızlığı (`Get-NetIPConfiguration`).
- [ ] **Gateway erişimi:** her aktif adaptör için gateway'e ping.
- [ ] **DNS sağlığı:** mevcut DNS ile + `1.1.1.1` ve `8.8.8.8` ile `Resolve-DnsName` karşılaştırması → "DNS sunucun çalışmıyor" ayrımı yapılabilsin.
- [ ] **İnternet erişimi:** `http://www.msftconnecttest.com/connecttest.txt` HTTP isteği; captive portal tespiti (beklenmeyen içerik/redirect).
- [ ] **Proxy/WinHTTP:** `netsh winhttp show proxy` + kullanıcı proxy'si (registry `...\Internet Settings`); şüpheli/ölü proxy → Finding.
- [ ] **VPN/sanal adaptör çakışması:** TAP/Wintun/WireGuard/OpenVPN adaptörleri; bağlı olmayan VPN'in default route veya DNS bırakması → Finding.
- [ ] **Güvenlik duvarı:** profil durumları (`Get-NetFirewallProfile`), varsayılan Outbound=Block gibi anormal politika, tüm trafiği kesen aktif kural taraması.
- [ ] **Hosts dosyası:** varsayılan dışı kayıtlar (özellikle bilinen domainleri localhost'a basanlar).
- [ ] **Winsock/LSP:** `netsh winsock show catalog` — bilinen olmayan üçüncü parti LSP girişleri → Warning.
- [ ] **MTU:** `ping -f -l` ile path MTU tespiti; 1500 altı fragmentasyon sorunu → Finding.
- [ ] **NIC güç yönetimi:** "Allow the computer to turn off this device" açık mı (WMI `MSPower_DeviceEnable`).
- [ ] **Servisler:** `Dnscache, Dhcp, WlanSvc, NlaSvc, netprofm, WinHttpAutoProxySvc` çalışıyor mu + StartType doğru mu.
- [ ] **"Çalışıyor ama yavaş" tespiti (kullanıcının özel isteği):**
  - LinkSpeed anormalliği: 1 Gbps kart 100 Mbps'te anlaşmışsa → kablo/duplex Finding.
  - `netsh interface tcp show global`: Auto-Tuning ≠ normal, RSS kapalı, ECN/heuristik anormallikleri → Finding ("İşletim sistemi ağ ayarı hızını düşürüyor").
  - Wi-Fi: `netsh wlan show interfaces` — sinyal %, band (2.4 vs 5 GHz), PHY hızı; zayıf sinyal/2.4GHz'e takılma → Finding.
  - Throughput örnekleme: internet varsa ~10 MB test indirmesi, ölçülen hız LinkSpeed'in %10'unun altındaysa → Finding (eşikler `thresholds.json`'da).

### 4.2 Canlı testler (kullanıcı önünde)
- [ ] **Adaptör başına ping testi:** her aktif adaptörün kaynak IP'siyle `ping -n 10 -S <sourceIP> <hedef>` → hedefler: gateway, 1.1.1.1, 8.8.8.8. UI'da adaptör başına canlı tablo: gönderilen/gelen, kayıp %, min/ort/maks gecikme, jitter. Kayıp >%5 veya jitter >30ms sarı, kayıp >%20 kırmızı.
- [ ] **DNS çözümleme yarışı:** aynı isim 4 farklı DNS'ten çözülür, süreler canlı gösterilir.
- [ ] **İndirme hız testi** (yalnız internet varken, kullanıcı başlatır).

### 4.3 Fix'ler
- [ ] **Tier 1:** DNS flush (`ipconfig /flushdns`), DHCP renew (`/release`+`/renew`), adaptör disable/enable (`Disable-/Enable-NetAdapter`), ağ servislerini yeniden başlat, ARP temizle (`arp -d *` / `netsh interface ip delete arpcache`).
- [ ] **Tier 2:** Winsock reset (`netsh winsock reset`), IP stack reset (`netsh int ip reset` + `int ipv6 reset`), TCP global ayarları normale çek (autotuning=normal, RSS=enabled), NIC güç tasarrufunu kapat, ölü proxy'yi temizle, hosts dosyasını varsayılana döndür (FileBackup sonrası), DNS'i 1.1.1.1/8.8.8.8'e ayarla (kullanıcı onaylı, ValueBackup ile).
- [ ] **Tier 3 (Zorla):** güvenlik duvarını varsayılana sıfırla (ÖNCE `advfirewall export`), sürücüyü kaldır+yeniden tara (`pnputil /remove-device` sonrası `pnputil /scan-devices`; ÖNCE DriverBackup), bağlantısız/hayalet VPN adaptörlerini kaldır, tam ağ sıfırlama (Ayarlar'daki "Network reset" eşdeğeri: tüm adaptör kaldır + winsock + ip reset + reboot bayrağı), yönlendirme tablosunu sıfırla (`route -f`, ÖNCE `route print` dump).
- [ ] Zorunlu yedekler: `netsh -c interface dump`, `advfirewall export`, `Tcpip\Parameters` + `Interfaces` reg export, hosts kopyası, sürücü export, mevcut DNS/IP değerleri JSON.

### 4.4 Belirti playbook'ları
- [ ] "İnternet yok" / "Sınırlı bağlantı" / "İnternet var ama yavaş" / "Belirli siteler açılmıyor" (→ DNS+hosts+MTU odaklı) / "VPN sonrası internet bozuldu" / "Wi-Fi sürekli kopuyor" (→ güç yönetimi+sürücü+sinyal odaklı).

---

## 5. Modül: Ses (P0)

Giriş (mikrofon) ve çıkış (hoparlör) **ayrı belirti grupları**, ortak altyapı.

### 5.1 Teşhis check'leri
- [ ] **Cihaz envanteri (NAudio `MMDeviceEnumerator`):** render+capture; disabled/unplugged/not present dahil. Hiç aktif cihaz yoksa Critical.
- [ ] **Varsayılan cihaz mantığı:** varsayılan cihaz fiziksel mi, sanal mı (VB-Cable, Voicemeeter, Steam/Discord/NVIDIA Broadcast sanal cihazları isim kalıbıyla tanınır)? Sanal cihaz varsayılansa ve karşılığı uygulama çalışmıyorsa → Finding ("Sesin sanal bir cihaza gidiyor").
- [ ] **Servisler:** `Audiosrv`, `AudioEndpointBuilder` + bağımlılıkları çalışıyor/otomatik mi.
- [ ] **Ses seviyeleri:** master mute, %0 seviye, per-app mixer'da hedef uygulama muted → Finding.
- [ ] **Sürücü:** ses denetleyicisi sürücü tarihi/sağlayıcısı; Device Manager hata kodu (WMI `Win32_PnPEntity.ConfigManagerErrorCode ≠ 0`) → Critical.
- [ ] **Format/örnekleme çakışması:** cihaz varsayılan formatı okunur; 44.1↔48 kHz uyumsuzluk uyarısı.
- [ ] **Ses iyileştirmeleri (APO):** enhancement DLL'leri etkin mi (registry `MMDevices\...\FxProperties`) — çökme/kesilme şüphesinde kapatma önerisi.
- [ ] **Mikrofon gizlilik ayarı:** `HKCU\...\CapabilityAccessManager\ConsentStore\microphone` → Deny ise Critical ("Windows gizlilik ayarı mikrofonu engelliyor").
- [ ] **Bluetooth ses:** A2DP/HFP profil sorunu (BT kulaklık "Hands-Free" moduna düşmüş → kötü kalite) → Finding.
- [ ] **HDMI/DP ses çakışması:** monitör sesi varsayılan olmuş, kullanıcının hoparlörü devre dışı → Finding.
- [ ] **Communications ducking** anormal ayar kontrolü.

### 5.2 Canlı testler
- [ ] **Hoparlör testi:** seçilen (veya sırayla her) çıkış cihazında önce SOL sonra SAĞ kanaldan sinüs ton (NAudio `SignalGenerator`); kullanıcıya "Duydunuz mu? Evet/Hayır" sorulur, cevap teşhise işlenir.
- [ ] **Mikrofon testi:** canlı seviye çubuğu (peak meter); 5 sn kayıt + geri dinletme; hiç sinyal yoksa Finding.
- [ ] **Gecikme/kesinti gözlemi:** 10 sn oynatmada underrun sayısı.

### 5.3 Fix'ler
- [ ] **Tier 1:** ses servislerini yeniden başlat, doğru varsayılan cihazı ayarla (ValueBackup; `IPolicyConfig` COM arayüzü ile — bilinen GUID'li undocumented COM, implementasyonu ekle), mute/level düzelt, devre dışı cihazı etkinleştir, enhancement'ları kapat, format'ı 24-bit/48kHz'e sabitle.
- [ ] **Tier 2:** mikrofon gizlilik iznini aç (RegistryBackup), per-app volume store sıfırla (`HKCU\...\Multimedia\Audio` ilgili anahtar, RegistryBackup), hayalet/kullanılmayan ses uç noktalarını kaldır, Bluetooth cihazını ses profilinde sıfırla (AVRCP/HFP toggle), exclusive mode kapat.
- [ ] **Tier 3 (Zorla):** ses sürücüsünü kaldır + yeniden tara (DriverBackup şart), sürücü rollback, `MMDevices` registry tam sıfırlama (ÖNCE tam reg export; sonrası endpoint'ler yeniden oluşur), sanal ses sürücülerini kaldırmayı öner (kullanıcı onayı olmadan kaldırma).
- [ ] Zorunlu yedekler: `HKLM\...\MMDevices` reg export, sürücü export, varsayılan cihaz ID'leri + seviyeler JSON.

### 5.4 Belirti playbook'ları
- [ ] "Hiç ses yok" / "Ses cızırtılı-kesik" / "Mikrofon çalışmıyor" / "Mikrofon çok kısık" / "Bluetooth kulaklıkta ses kötü" / "Yanlış cihazdan ses geliyor".

---

## 6. Diğer Modüller (P1 — Network+Audio bittikten sonra)

Her biri aynı kalıpta: check'ler → tier'lı fix'ler → verify. Kısa spesifikasyon:

- [ ] **Windows Update:** servis kontrolleri (`wuauserv`, `BITS`, `cryptsvc`), `SoftwareDistribution`+`catroot2` sıfırlama (klasörleri yeniden adlandır — bu yedektir), `DISM /Online /Cleanup-Image /RestoreHealth` + `sfc /scannow` zinciri (çıktı % ilerleme parse edilip UI'da gösterilir), yaygın hata kodu → açıklama sözlüğü (0x80070002, 0x8007000E, 0x80244022...).
- [ ] **Yazıcı:** takılı kuyruk tespiti, spooler reset (`spoolsv` durdur → `PRINTERS` klasörünü boşalt [dosyalar yedek klasörüne taşınır] → başlat), çevrimdışı yazıcı, varsayılan yazıcı karmaşası, sürücü izolasyonu.
- [ ] **Bluetooth:** `bthserv` servis, sürücü hata kodu, takılı cihaz için yeniden eşleştirme sihirbazı (kaldır → keşif moduna al talimatı → ekle).
- [ ] **Disk & Depolama:** SMART durumu (`Get-PhysicalDisk` HealthStatus → kötüyse BÜYÜK uyarı + "önce verini yedekle"), %100 disk kullanımı teşhisi (SysMain/Windows Search/aşırı sayfalama tespit edilip seçici devre dışı bırakma), güvenli temp temizliği (yalnız `%TEMP%`, `Windows\Temp`, tarayıcı önbelleği değil), `chkdsk` planlama (yalnız kullanıcı onayıyla, reboot kuyruğuna).
- [ ] **Sistem Dosyası Bütünlüğü:** `sfc /scannow` + gerekirse `DISM RestoreHealth` otomatik zincir; CBS log'undan onarılamayan dosyaları rapora yaz.
- [ ] **Mağaza & UWP Uygulamaları:** `wsreset`, bozuk paket tespiti, `Add-AppxPackage -Register ...AppXManifest.xml` ile yeniden kayıt (tek uygulama veya tümü).
- [ ] **Saat/Zaman Senkronu:** saat kayması tespiti (TLS hatalarının gizli sebebi) → `w32tm /resync`, saat dilimi/NTP sunucu düzeltme.
- [ ] **Başlangıç & Performans:** başlangıç öğeleri + ölçülü etki listesi, güç planı anormalliği (ör. "Power saver"da takılı masaüstü), görsel efekt önerisi. (Yalnız öneri + tek tık devre dışı; agresif "optimizasyon" YOK.)

---

## 7. Raporlama, Log ve Destek Paketi

- [ ] Serilog → `%ProgramData%\CaYaFix\Logs\` günlük dosya; İşlem Konsolu aynı akışı gösterir.
- [ ] Oturum sonunda **HTML rapor**: bulgular, uygulanan fix'ler, öncesi/sonrası test sonuçları, geri alma talimatı.
- [ ] **Destek Paketi (zip):** rapor + loglar + `ipconfig /all`, `netsh wlan show all`, sürücü listesi, ses cihaz dökümü. Kişisel veri uyarısı göster.

---

## 8. Güvenlik ve Kalite Kuralları (kod yazan AI için zorunlu)

1. Dış komutlar YALNIZ `ICommandRunner` üzerinden; her çağrıda timeout (varsayılan 60 sn, DISM/SFC için 30 dk) ve CancellationToken.
2. Komut argümanları asla kullanıcı girdisiyle string-concat edilmez; `ProcessStartInfo.ArgumentList` kullan.
3. `BackupAsync` null dönerse (yedek alınamadı) Apply ÇALIŞTIRILMAZ (tek istisna: §2'deki Tier 3 açık onayı).
4. Her `FixAction.VerifyAsync` gerçek bir yeniden ölçüm yapar; "true dön geç" yasak.
5. UI thread'de hiçbir sistem çağrısı yok; her uzun iş `IProgress<T>` ile raporlar.
6. Tüm kullanıcı metinleri resx'te; kod içine Türkçe string gömme.
7. xUnit testleri: Engine akışı (backup-fail → apply-yok), Undo ters sıra, tier eskalasyon mantığı, ping/`netsh` çıktı parser'ları (örnek çıktılar `Tests/Fixtures/`e konur).
8. Uygulama internet olmadan da tüm yerel teşhis/onarımları yapabilmeli (ağ onarım aracının internete muhtaç olmaması kritik).

---

## 9. Uygulama Sırası (Fazlar)

- [ ] **Faz 0 — İskelet:** solution + 4 proje, elevation manifest'i, Serilog, `ICommandRunner` gerçek implementasyonu + İşlem Konsolu'na akış. *Kabul: exe admin isteyerek açılıyor, konsolda test komutu çıktısı akıyor.*
- [ ] **Faz 1 — Motor:** DiagnosticCheck/FixAction/LiveTest modelleri, DiagnosticEngine, FixEngine (backup→apply→verify), SessionManager, tüm BackupEntry türleri, geri yükleme noktası. *Kabul: sahte (dummy) modülle uçtan uca teşhis→onar→doğrula→geri al akışı çalışıyor + birim testleri yeşil.*
- [ ] **Faz 2 — UI kabuğu:** ana ekran, bulgular ekranı, onarım ilerleme ekranı, kademe yükseltme paneli, Zorla Onarım onay ekranı, Geri Alma Merkezi, ayarlar (dry-run). *Kabul: dummy modülle tüm akış UI'dan yürüyor.*
- [ ] **Faz 3 — Ağ modülü:** §4'ün tamamı. *Kabul: kablo çekme, DNS bozma (manuel 0.0.0.0 verme), proxy bozma senaryolarında doğru bulgu + Tier 1-2 onarım + canlı ping testi çalışıyor.*
- [ ] **Faz 4 — Ses modülü:** §5'in tamamı. *Kabul: varsayılan cihaz bozma, servis durdurma, mute senaryoları tespit+onarım; hoparlör L/R ve mikrofon canlı testi çalışıyor.*
- [ ] **Faz 5 — Eskalasyon + Destek Paketi:** Tier 3 akışları, teslim ekranı, HTML rapor, zip. *Kabul: çözülmeyen senaryoda akış teslim ekranına ulaşıyor, rapor üretiliyor, tüm oturum geri alınabiliyor.*
- [ ] **Faz 6 — Diğer modüller:** §6 sırayla (Windows Update → Yazıcı → Bluetooth → Disk → SFC → Mağaza → Saat → Başlangıç).
- [ ] **Faz 7 — Cila:** resx tamamlama, tek dosya publish, sürüm bilgisi, README, son uçtan-uca manuel test listesi.

---

## 10. Kapsam Dışı (yapma)

- Antivirüs/zararlı yazılım temizliği, overclock/BIOS ayarları, "RAM/registry optimizer" tarzı plasebo özellikler, telemetri/istatistik toplama, otomatik güncelleme sistemi (v1'de yok).

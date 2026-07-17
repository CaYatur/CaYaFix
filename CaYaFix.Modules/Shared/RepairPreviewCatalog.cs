// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

namespace CaYaFix.Modules.Shared;

internal static class RepairPreviewCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Steps =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["net.flush-dns"] = ["ipconfig.exe /flushdns"],
            ["net.renew-dhcp"] =
            [
                "Ensure Dhcp / Dnscache / NlaSvc are running",
                "ipconfig.exe /release <active DHCP adapter>",
                "Always run ipconfig.exe /renew on the same adapter (never skip after release)",
                "Safety net: ipconfig.exe /renew (global)"
            ],
            ["net.renew-dhcp-all"] =
            [
                "Ensure Dhcp / Dnscache / NlaSvc are running",
                "ipconfig.exe /release + /renew for every DHCP-capable interface",
                "Safety net: ipconfig.exe /renew (global)"
            ],
            ["net.profile-repair-active"] =
            [
                "Repair only connected network connection profiles",
                "Re-enable DHCP and reset DNS to automatic on those interfaces",
                "Release/renew DHCP for active profile adapters",
                "Ensure matching firewall profile(s) stay enabled"
            ],
            ["net.profile-repair-all"] =
            [
                "Repair all Windows network connection profiles",
                "Re-enable DHCP and reset DNS to automatic on each profile interface",
                "Release/renew DHCP per profile adapter",
                "Enable Domain / Private / Public firewall profiles"
            ],
            ["net.ipconfig-suite"] =
            [
                "ipconfig.exe /flushdns",
                "Resilient per-adapter release â†’ renew (never aborts before renew)",
                "ipconfig.exe /registerdns",
                "ipconfig.exe /flushdns"
            ],
            ["net.cycle-adapters"] = ["Enable disabled physical adapters", "Restart active physical adapters"],
            ["net.restart-services"] =
            [
                "Restore network service start modes (Disabled â†’ Automatic/Manual)",
                "Restart Dnscache, Dhcp, NlaSvc, netprofm, WlanSvc when running; start if stopped"
            ],
            ["net.clear-arp"] = ["arp.exe -d *", "netsh.exe interface ip delete arpcache"],
            ["net.soft-heal"] =
            [
                "ipconfig.exe /flushdns",
                "arp.exe -d *",
                "netsh.exe interface ip delete arpcache",
                "Start stopped core network services (DnsCache, DHCP, NlaSvc, netprofm, WlanSvc)",
                "ipconfig.exe /registerdns"
            ],
            ["net.rescan-devices"] = ["pnputil.exe /scan-devices"],
            ["net.reset-stack"] = ["netsh.exe winsock reset", "netsh.exe int ip reset", "netsh.exe int ipv6 reset", "Queue one restart"],
            ["net.normalize-tcp"] = ["netsh.exe interface tcp set global autotuninglevel=normal", "netsh.exe interface tcp set global rss=enabled", "netsh.exe interface tcp set heuristics disabled"],
            ["net.disable-power-saving"] = ["Disable MSPower_DeviceEnable only for physical network adapters"],
            ["net.clear-proxy"] = ["netsh.exe winhttp reset proxy", "Disable the current-user proxy", "Remove the current-user PAC URL"],
            ["net.restore-hosts"] = ["Replace the hosts file with localhost-only defaults"],
            ["net.public-dns"] = ["Set active IPv4 adapters to 1.1.1.1 and 8.8.8.8"],
            ["net.firewall-reset"] = ["netsh.exe advfirewall reset"],
            ["net.driver-reset"] = ["pnputil.exe /delete-driver <diagnosed-oem.inf> /uninstall /force", "pnputil.exe /scan-devices", "Queue one restart"],
            ["net.ghost-vpn-remove"] = ["pnputil.exe /remove-device <verified-ghost-instance>", "pnputil.exe /scan-devices", "Queue one restart"],
            ["net.full-reset"] = ["netsh.exe winsock reset", "netsh.exe int ip reset", "netsh.exe int ipv6 reset", "ipconfig.exe /flushdns", "pnputil.exe /scan-devices", "Queue one restart"],
            ["net.route-reset"] = ["Delete only the diagnosed invalid or conflicting persistent IPv4 routes", "Leave unrelated active and persistent routes unchanged"],

            ["audio.restart-services"] = ["Set AudioEndpointBuilder and Audiosrv to Automatic", "Restart AudioEndpointBuilder and Audiosrv"],
            ["audio.set-default"] = ["IPolicyConfig.SetDefaultEndpoint(<diagnosed-endpoint>) for Console, Multimedia, and Communications roles"],
            ["audio.unmute"] = ["Unmute default render and capture endpoints", "Raise endpoints below 50% to 65%"],
            ["audio.repair-all-io"] =
            [
                "Unmute and normalize levels on all active input and output endpoints",
                "Allow microphone privacy for the current user",
                "Enable disabled MEDIA/AudioEndpoint devices",
                "Soft-reset Render+Capture format overrides",
                "Restart AudioEndpointBuilder and Audiosrv"
            ],
            ["audio.repair-output"] =
            [
                "Unmute and normalize levels on active output (render) endpoints only",
                "Enable disabled speaker/headphone/output devices",
                "Soft-reset Render format overrides",
                "Restart AudioEndpointBuilder and Audiosrv"
            ],
            ["audio.repair-input"] =
            [
                "Unmute and normalize levels on active input (capture) endpoints only",
                "Allow microphone privacy for the current user",
                "Enable disabled microphone/input devices",
                "Soft-reset Capture format overrides",
                "Restart AudioEndpointBuilder and Audiosrv"
            ],
            ["audio.disable-enhancements"] = ["Disable endpoint enhancement processing in MMDevices", "Restart Audiosrv"],
            ["audio.format-reset"] = ["Stop Audiosrv", "Remove invalid endpoint format overrides", "Start Audiosrv"],
            ["audio.microphone-privacy"] = ["Set current-user microphone consent to Allow"],
            ["audio.mixer-reset"] = ["Reset the current-user per-application audio property store", "Restart Audiosrv"],
            ["audio.bluetooth-restart"] = ["pnputil.exe /restart-device <diagnosed-bluetooth-instance>"],
            ["audio.enable-device"] = ["pnputil.exe /enable-device <diagnosed-audio-instance>"],
            ["audio.enable-all-disabled"] =
            [
                "pnputil.exe /enable-device for disabled or error MEDIA/AudioEndpoint devices",
                "Restart AudioEndpointBuilder and Audiosrv"
            ],
            ["audio.rescan-devices"] =
            [
                "pnputil.exe /scan-devices",
                "Restart AudioEndpointBuilder and Audiosrv"
            ],
            ["audio.driver-reset"] = ["pnputil.exe /delete-driver <diagnosed-oem.inf> /uninstall /force", "pnputil.exe /scan-devices", "Queue one restart"],
            ["audio.mmdevices-reset"] = ["Stop Windows audio services", "Rebuild MMDevices render and capture endpoint state", "Start Windows audio services", "pnputil.exe /scan-devices", "Queue one restart"],

            ["update.restart-services"] = ["Restore Windows Update service start modes", "Start wuauserv, BITS, and cryptsvc"],
            ["update.reset-cache"] = ["Stop Windows Update services", "Rebuild SoftwareDistribution and catroot2 from verified copies", "Start Windows Update services"],
            ["printer.restart-spooler"] = ["Set Spooler to Automatic", "Restart Spooler"],
            ["printer.reset-queue"] = ["Stop Spooler", "Clear only queued spool files", "Start Spooler"],
            ["printer.set-online"] = ["Set-Printer -WorkOffline:$false for offline printers"],
            ["printer.set-default"] = ["Set-Printer -Default for the diagnosed printer"],
            ["bluetooth.restart-service"] = ["Set bthserv to Manual", "Start bthserv"],
            ["bluetooth.restart-device"] = ["pnputil.exe /restart-device <diagnosed-bluetooth-instance>"],
            ["disk.clean-temp"] = ["Move eligible files from user and Windows temporary roots into the session quarantine"],
            ["disk.online-scan-fix"] =
            [
                "chkdsk.exe <system-volume> /scan",
                "chkdsk.exe <system-volume> /spotfix"
            ],
            ["disk.schedule-chkdsk"] = ["chkntfs.exe /c <system-volume>", "Queue one restart"],
            ["integrity.sfc-scan"] = ["sfc.exe /scannow", "Queue one restart"],
            ["integrity.dism-restore"] = ["dism.exe /Online /Cleanup-Image /RestoreHealth", "Queue one restart"],
            ["integrity.component-cleanup"] = ["dism.exe /Online /Cleanup-Image /StartComponentCleanup"],
            ["integrity.dism-sfc"] =
            [
                "dism.exe /Online /Cleanup-Image /RestoreHealth",
                "sfc.exe /scannow",
                "Queue one restart"
            ],
            ["boot.export-bcd"] = ["bcdedit.exe /export <session>\\bcd-export-*.bcd"],
            ["boot.recoveryenabled"] =
            [
                "bcdedit.exe /set {current} recoveryenabled Yes",
                "bcdedit.exe /set {current} bootstatuspolicy DisplayAllFailures"
            ],
            ["boot.enable-winre"] = ["reagentc.exe /enable"],
            ["boot.rebuild-bcdboot"] =
            [
                "bcdedit.exe /export <session backup>",
                "bcdboot.exe %SystemRoot% /f ALL",
                "Queue one restart"
            ],
            ["store.reset-cache"] = ["wsreset.exe"],
            ["time.resync"] = ["Set W32Time to Manual only if disabled", "Start W32Time", "w32tm.exe /resync /force"],
            ["performance.balanced-plan"] = ["powercfg.exe /setactive SCHEME_BALANCED"],
            ["performance.repair-core-services"] =
            [
                "Restore Disabled core Windows services to Automatic/Manual",
                "Start stopped safe services (network, audio, event log, Plug and Play, WMI, â€¦)",
                "Does not touch RpcSs / DcomLaunch / LSM critical SCM services"
            ],
            ["performance.restart-core-services"] =
            [
                "Restore Disabled core Windows services to Automatic/Manual",
                "Restart already-running safe services and start stopped ones",
                "Does not touch RpcSs / DcomLaunch / LSM critical SCM services"
            ],
            ["camera.allow-access"] = ["Set current-user camera consent to Allow"],
            ["camera.restart-device"] = ["pnputil.exe /restart-device <diagnosed-camera-instance>"],
            ["usb.start-services"] = ["Set DsmSvc and DeviceInstall to Manual", "Start PlugPlay, DsmSvc, and DeviceInstall"],
            ["usb.restart-device"] = ["pnputil.exe /restart-device <diagnosed-usb-instance>"],
            ["search.restart-service"] = ["Set WSearch to Automatic", "Restart WSearch"],
            ["display.soft-reset"] =
            [
                "Trigger Win+Ctrl+Shift+B graphics driver reset (brief black screen)",
                "pnputil.exe /scan-devices"
            ],
            ["display.repair-resolution"] =
            [
                "Enable disabled Display and Monitor devices",
                "pnputil.exe /restart-device for graphics adapters and present monitors",
                "Trigger Win+Ctrl+Shift+B graphics soft-reset (brief black screen)",
                "pnputil.exe /scan-devices to rediscover EDID/modes"
            ],
            ["display.restore-recommended"] =
            [
                "Enumerate active display paths via EnumDisplaySettings",
                "Apply the largest supported mode per path with ChangeDisplaySettingsEx (CDS_UPDATEREGISTRY|CDS_RESET)",
                "Does not invent modes the driver does not advertise"
            ],
            ["display.scan-devices"] = ["pnputil.exe /scan-devices"],
            ["display.restart-all"] =
            [
                "pnputil.exe /restart-device for each present Display-class adapter",
                "Screen may flicker or go blank briefly"
            ],
            ["display.restart-device"] = ["pnputil.exe /restart-device <diagnosed-display-instance>"],

            ["bluetooth.enable-adapters"] =
            [
                "pnputil.exe /enable-device for disabled Bluetooth adapters"
            ],

            ["bluetooth.restart-radios"] =
            [
                "pnputil.exe /restart-device for Bluetooth class devices"
            ],

            ["bluetooth.restart-stack"] =
            [
                "Restart bthserv, BthAvctpSvc, BTAGService, DeviceAssociationService"
            ],

            ["bluetooth.scan-devices"] =
            [
                "pnputil.exe /scan-devices"
            ],

            ["bluetooth.start-support-services"] =
            [
                "Start bthserv and related Bluetooth support services if stopped"
            ],

            ["boot.disable-recoveryenabled"] =
            [
                "bcdedit.exe /set {current} recoveryenabled No"
            ],

            ["boot.enum-current"] =
            [
                "bcdedit.exe /enum {current}"
            ],

            ["boot.reagentc-info"] =
            [
                "reagentc.exe /info"
            ],

            ["boot.set-bootstatuspolicy-display"] =
            [
                "bcdedit.exe /set {current} bootstatuspolicy DisplayAllFailures"
            ],

            ["boot.set-bootstatuspolicy-ignore"] =
            [
                "bcdedit.exe /set {current} bootstatuspolicy IgnoreAllFailures"
            ],

            ["camera.allow-desktop-apps"] =
            [
                "Allow webcam ConsentStore for packaged and NonPackaged desktop apps"
            ],

            ["camera.cycle-capture-services"] =
            [
                "Restart FrameServer, FrameServerMonitor, and DeviceInstall"
            ],

            ["camera.enable-disabled"] =
            [
                "pnputil.exe /enable-device for disabled Camera/Image class devices"
            ],

            ["camera.restart-frameserver"] =
            [
                "Restart FrameServer and FrameServerMonitor services"
            ],

            ["camera.scan-devices"] =
            [
                "pnputil.exe /scan-devices"
            ],

            ["disk.clean-user-temp"] =
            [
                "Delete files older than 24h from the user temp folder"
            ],

            ["disk.clean-windows-temp"] =
            [
                "Delete files older than 24h from %WINDIR%\\\\Temp"
            ],

            ["disk.flush-volume"] =
            [
                "Write-VolumeCache for the system volume"
            ],

            ["disk.fsutil-dirty-query"] =
            [
                "fsutil.exe volume diskfree",
                "fsutil.exe dirty query"
            ],

            ["disk.spotfix-only"] =
            [
                "chkdsk.exe <system-volume> /spotfix"
            ],

            ["integrity.checkhealth-refresh"] =
            [
                "dism.exe /Online /Cleanup-Image /CheckHealth"
            ],

            ["integrity.component-cleanup-resetbase"] =
            [
                "dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase",
                "Queue one restart"
            ],

            ["integrity.scanhealth-only"] =
            [
                "dism.exe /Online /Cleanup-Image /ScanHealth"
            ],

            ["integrity.sfc-dism"] =
            [
                "sfc.exe /scannow",
                "dism.exe /Online /Cleanup-Image /RestoreHealth",
                "Queue one restart"
            ],

            ["integrity.sfc-verifyonly"] =
            [
                "sfc.exe /verifyonly"
            ],

            ["performance.hibernate-off"] =
            [
                "powercfg.exe /hibernate off"
            ],

            ["performance.high-performance-plan"] =
            [
                "powercfg.exe /setactive SCHEME_MIN (High performance GUID)"
            ],

            ["performance.query-energy"] =
            [
                "powercfg.exe /energy /duration 5"
            ],

            ["performance.restart-schedule"] =
            [
                "Restart Task Scheduler (Schedule)"
            ],

            ["performance.restart-sysmain"] =
            [
                "Restart SysMain (Superfetch)"
            ],

            ["performance.restore-default-schemes"] =
            [
                "powercfg.exe -restoredefaultschemes"
            ],

            ["printer.cancel-error-jobs"] =
            [
                "Remove-PrintJob for jobs in Error/Blocked/Offline states"
            ],

            ["printer.purge-spool"] =
            [
                "Stop Spooler",
                "Clear spool PRINTERS queue files",
                "Start Spooler"
            ],

            ["printer.restart-print-pipeline"] =
            [
                "Restart Spooler and related print pipeline services"
            ],

            ["printer.sc-auto-spooler"] =
            [
                "sc.exe config Spooler start= auto",
                "sc.exe start Spooler"
            ],

            ["printer.test-spool-folder"] =
            [
                "Ensure %WINDIR%\\\\System32\\\\spool\\\\PRINTERS exists"
            ],

            ["search.clear-temp"] =
            [
                "Stop WSearch",
                "Clear ProgramData\\\\Microsoft\\\\Search\\\\Data\\\\Temp",
                "Start WSearch"
            ],

            ["search.rebuild-index"] =
            [
                "Set HKLM\\\\SOFTWARE\\\\Microsoft\\\\Windows Search\\\\SetupCompletedSuccessfully = 0",
                "Restart WSearch",
                "Queue one restart"
            ],

            ["search.restart-and-rebuild"] =
            [
                "sc.exe config WSearch start= auto",
                "SetupCompletedSuccessfully = 0",
                "Restart WSearch",
                "Queue one restart"
            ],

            ["search.set-automatic"] =
            [
                "sc.exe config WSearch start= auto",
                "sc.exe start WSearch"
            ],

            ["store.clear-local-cache"] =
            [
                "Clear Microsoft.WindowsStore LocalCache under LocalAppData\\\\Packages"
            ],

            ["store.register-manifest"] =
            [
                "Add-AppxPackage -Register AppxManifest.xml for Microsoft.WindowsStore"
            ],

            ["store.reset-appx-package"] =
            [
                "Reset-AppxPackage Microsoft.WindowsStore when available (Windows 11+)"
            ],

            ["store.restart-services"] =
            [
                "Restart AppXSvc, ClipSVC, InstallService, StateRepository, TokenBroker"
            ],

            ["store.start-appx-services"] =
            [
                "Start AppX deployment and related services if stopped"
            ],

            ["time.config-update"] =
            [
                "w32tm.exe /config /update",
                "w32tm.exe /resync /force"
            ],

            ["time.re-register"] =
            [
                "w32tm.exe /unregister",
                "w32tm.exe /register",
                "Configure time.windows.com peer",
                "w32tm.exe /resync /force"
            ],

            ["time.restart-service"] =
            [
                "Restart W32Time"
            ],

            ["time.resync-rediscover"] =
            [
                "w32tm.exe /resync /rediscover /force"
            ],

            ["time.set-manual-ntp"] =
            [
                "w32tm.exe /config /manualpeerlist:time.windows.com,0x8 /syncfromflags:manual /update",
                "Restart W32Time",
                "w32tm.exe /resync /force"
            ],

            ["update.clean-download"] =
            [
                "Stop wuauserv/BITS",
                "Clear SoftwareDistribution\\\\Download",
                "Start update services"
            ],

            ["update.restart-bits"] =
            [
                "Restart BITS and wuauserv"
            ],

            ["update.restart-cryptsvc"] =
            [
                "Restart cryptsvc, BITS, and wuauserv"
            ],

            ["update.restart-uso"] =
            [
                "Restart UsoSvc, wuauserv, and BITS"
            ],

            ["update.sc-defaults"] =
            [
                "sc.exe config BITS/wuauserv demand and cryptsvc auto",
                "Start update services"
            ],

            ["usb.cycle-root-hubs"] =
            [
                "Restart USB Root Hub devices",
                "pnputil.exe /scan-devices"
            ],

            ["usb.enable-disabled"] =
            [
                "pnputil.exe /enable-device for disabled USB devices"
            ],

            ["usb.restart-discovery-services"] =
            [
                "Restart PlugPlay, DsmSvc, and DeviceInstall"
            ],

            ["usb.restart-hubs"] =
            [
                "pnputil.exe /restart-device for present USB-class devices"
            ],

            ["usb.scan-devices"] =
            [
                "pnputil.exe /scan-devices"
            ]
        };

    public static IReadOnlyList<string> Get(string fixId) =>
        Steps.TryGetValue(fixId, out var steps) ? steps : [];
}


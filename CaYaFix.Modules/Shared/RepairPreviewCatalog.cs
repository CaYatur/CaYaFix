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
            ["net.renew-dhcp"] = ["ipconfig.exe /release", "ipconfig.exe /renew"],
            ["net.cycle-adapters"] = ["Enable disabled physical adapters", "Restart active physical adapters"],
            ["net.restart-services"] = ["Restore required network services from Disabled to Manual", "Start stopped network services"],
            ["net.clear-arp"] = ["arp.exe -d *", "netsh.exe interface ip delete arpcache"],
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
            ["audio.disable-enhancements"] = ["Disable endpoint enhancement processing in MMDevices", "Restart Audiosrv"],
            ["audio.format-reset"] = ["Stop Audiosrv", "Remove invalid endpoint format overrides", "Start Audiosrv"],
            ["audio.microphone-privacy"] = ["Set current-user microphone consent to Allow"],
            ["audio.mixer-reset"] = ["Reset the current-user per-application audio property store", "Restart Audiosrv"],
            ["audio.bluetooth-restart"] = ["pnputil.exe /restart-device <diagnosed-bluetooth-instance>"],
            ["audio.enable-device"] = ["pnputil.exe /enable-device <diagnosed-audio-instance>"],
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
            ["disk.schedule-chkdsk"] = ["chkntfs.exe /c <system-volume>", "Queue one restart"],
            ["integrity.dism-sfc"] = ["dism.exe /Online /Cleanup-Image /RestoreHealth", "sfc.exe /scannow", "Queue one restart"],
            ["store.reset-cache"] = ["wsreset.exe"],
            ["time.resync"] = ["Set W32Time to Manual only if disabled", "Start W32Time", "w32tm.exe /resync /force"],
            ["performance.balanced-plan"] = ["powercfg.exe /setactive SCHEME_BALANCED"],
            ["camera.allow-access"] = ["Set current-user camera consent to Allow"],
            ["camera.restart-device"] = ["pnputil.exe /restart-device <diagnosed-camera-instance>"],
            ["usb.start-services"] = ["Set DsmSvc and DeviceInstall to Manual", "Start PlugPlay, DsmSvc, and DeviceInstall"],
            ["usb.restart-device"] = ["pnputil.exe /restart-device <diagnosed-usb-instance>"],
            ["search.restart-service"] = ["Set WSearch to Automatic", "Restart WSearch"],
            ["display.restart-device"] = ["pnputil.exe /restart-device <diagnosed-display-instance>"]
        };

    public static IReadOnlyList<string> Get(string fixId) =>
        Steps.TryGetValue(fixId, out var steps) ? steps : [];
}

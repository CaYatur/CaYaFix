// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CaYaFix.Core;
using CaYaFix.Modules.Shared;

namespace CaYaFix.Modules.Network;

public sealed class NetworkModule : IModuleDefinition
{
    public const string ModuleId = "network";
    private static readonly string[] NetworkServices =
        ["Dnscache", "Dhcp", "WlanSvc", "NlaSvc", "netprofm", "WinHttpAutoProxySvc"];
    private static readonly byte[] ExpectedNcsiContent = Encoding.ASCII.GetBytes("Microsoft Connect Test");

    public NetworkModule()
    {
        Checks = CreateChecks();
        Fixes = CreateFixes();
        LiveTests = CreateLiveTests();
        Playbooks = CreatePlaybooks();
    }

    public ModuleInfo Info { get; } = new(
        ModuleId,
        "Module_Network_Name",
        "Module_Network_Description",
        "network.svg",
        0);

    public IReadOnlyList<DiagnosticCheck> Checks { get; }
    public IReadOnlyList<FixAction> Fixes { get; }
    public IReadOnlyList<LiveTest> LiveTests { get; }
    public IReadOnlyList<Playbook> Playbooks { get; }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("net.adapters", "Check_Network_Adapters", ModuleId, CheckAdaptersAsync),
        new DelegateDiagnosticCheck("net.ip", "Check_Network_Ip", ModuleId, CheckIpAsync),
        new DelegateDiagnosticCheck("net.gateway", "Check_Network_Gateway", ModuleId, CheckGatewayAsync),
        new DelegateDiagnosticCheck("net.dns", "Check_Network_Dns", ModuleId, CheckDnsAsync),
        new DelegateDiagnosticCheck("net.internet", "Check_Network_Internet", ModuleId, CheckInternetAsync),
        new DelegateDiagnosticCheck("net.proxy", "Check_Network_Proxy", ModuleId, CheckProxyAsync),
        new DelegateDiagnosticCheck("net.vpn", "Check_Network_Vpn", ModuleId, CheckVpnAsync),
        new DelegateDiagnosticCheck("net.routes", "Check_Network_Routes", ModuleId, CheckRoutesAsync),
        new DelegateDiagnosticCheck("net.firewall", "Check_Network_Firewall", ModuleId, CheckFirewallAsync),
        new DelegateDiagnosticCheck("net.hosts", "Check_Network_Hosts", ModuleId, CheckHostsAsync),
        new DelegateDiagnosticCheck("net.winsock", "Check_Network_Winsock", ModuleId, CheckWinsockAsync),
        new DelegateDiagnosticCheck("net.mtu", "Check_Network_Mtu", ModuleId, CheckMtuAsync),
        new DelegateDiagnosticCheck("net.power", "Check_Network_Power", ModuleId, CheckPowerAsync, quick: false),
        new DelegateDiagnosticCheck("net.services", "Check_Network_Services", ModuleId, CheckServicesAsync),
        new DelegateDiagnosticCheck("net.performance", "Check_Network_Performance", ModuleId, CheckPerformanceAsync),
        new DelegateDiagnosticCheck("net.throughput", "Check_Network_Throughput", ModuleId, CheckThroughputAsync, quick: false),
        new DelegateDiagnosticCheck("net.eventlog", "Check_Network_EventLog", ModuleId, CheckEventLogAsync, quick: false, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("net.bindings", "Check_Network_Bindings", ModuleId, CheckBindingsAsync)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        CreateFlushDnsFix(),
        CreateRenewDhcpActiveFix(),
        CreateRenewDhcpAllFix(),
        CreateProfileRepairActiveFix(),
        CreateProfileRepairAllFix(),
        CreateIpconfigSuiteFix(),
        CreateCycleAdaptersFix(),
        CreateRestartServicesFix(),
        CreateClearArpFix(),
        CreateSoftHealFix(),
        CreateRescanDevicesFix(),
        CreateResetStackFix(),
        CreateNormalizeTcpFix(),
        CreateDisablePowerSavingFix(),
        CreateClearProxyFix(),
        CreateRestoreHostsFix(),
        CreatePublicDnsFix(),
        CreateFirewallResetFix(),
        CreateTargetedDriverResetFix(),
        CreateTargetedGhostVpnFix(),
        CreateFullNetworkResetFix(),
        CreateRouteResetFix()
    ];

    private static IReadOnlyList<LiveTest> CreateLiveTests() =>
    [
        new DelegateLiveTest("net.live.ping", "LiveTest_Network_Ping", ModuleId, RunPingTestAsync),
        new DelegateLiveTest("net.live.dns", "LiveTest_Network_DnsRace", ModuleId, RunDnsRaceAsync),
        new DelegateLiveTest("net.live.http", "LiveTest_Network_Http", ModuleId, RunHttpTestAsync),
        new DelegateLiveTest("net.live.mtu", "LiveTest_Network_Mtu", ModuleId, RunMtuTestAsync),
        new DelegateLiveTest("net.live.speed", "LiveTest_Network_Speed", ModuleId, RunSpeedTestAsync)
    ];

    private static IReadOnlyList<Playbook> CreatePlaybooks() =>
    [
        new("network.no-internet", ModuleId, "Symptom_Network_NoInternet",
            ["net.adapters", "net.ip", "net.gateway", "net.dns", "net.internet", "net.proxy", "net.routes", "net.services", "net.eventlog", "net.bindings"],
            ["net.ipconfig-suite", "net.soft-heal", "net.flush-dns", "net.renew-dhcp", "net.profile-repair-active", "net.restart-services", "net.reset-stack"]),
        new("network.limited", ModuleId, "Symptom_Network_Limited",
            ["net.ip", "net.gateway", "net.dns", "net.firewall", "net.bindings"],
            ["net.ipconfig-suite", "net.soft-heal", "net.renew-dhcp", "net.profile-repair-active", "net.clear-arp", "net.reset-stack"]),
        new("network.slow", ModuleId, "Symptom_Network_Slow",
            ["net.adapters", "net.performance", "net.mtu", "net.power", "net.throughput", "net.eventlog"],
            ["net.soft-heal", "net.normalize-tcp", "net.disable-power-saving"]),
        new("network.sites", ModuleId, "Symptom_Network_Sites",
            ["net.dns", "net.hosts", "net.mtu", "net.proxy"],
            ["net.flush-dns", "net.soft-heal", "net.restore-hosts", "net.clear-proxy", "net.public-dns"]),
        new("network.vpn", ModuleId, "Symptom_Network_Vpn",
            ["net.vpn", "net.proxy", "net.dns", "net.ip", "net.routes", "net.eventlog"],
            ["net.clear-proxy", "net.soft-heal", "net.renew-dhcp", "net.profile-repair-active", "net.reset-stack"]),
        new("network.wifi-drops", ModuleId, "Symptom_Network_WifiDrops",
            ["net.adapters", "net.power", "net.performance", "net.services", "net.eventlog"],
            ["net.disable-power-saving", "net.soft-heal", "net.restart-services", "net.cycle-adapters", "net.rescan-devices"])
    ];

    private static async Task<Finding?> CheckAdaptersAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "Get-NetAdapter -IncludeHidden -ErrorAction Stop | ForEach-Object {$a=$_; $inf=(Get-PnpDeviceProperty -InstanceId $a.PnPDeviceID -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data; [pscustomobject]@{Name=$a.Name;InterfaceDescription=$a.InterfaceDescription;Status=[string]$a.Status;LinkSpeed=[string]$a.LinkSpeed;MediaType=[string]$a.MediaType;HardwareInterface=[bool]$a.HardwareInterface;DriverDate=if($a.DriverDate){$a.DriverDate.ToString('o')}else{$null};DriverVersion=$a.DriverVersion;PnPDeviceID=$a.PnPDeviceID;InfName=[string]$inf}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var adapters = ModuleHelpers.Array(root).ToArray();
        if (adapters.Length == 0)
        {
            return ModuleHelpers.Finding("net.adapters", ModuleId, Severity.Critical,
                "Finding_Network_NoAdapters", "Get-NetAdapter returned no adapters.", "net.full-reset");
        }

        var hardware = adapters.Where(adapter => IsTrue(adapter, "HardwareInterface")).ToArray();
        var active = hardware.Where(adapter => IsStatus(adapter, "Up")).ToArray();
        if (hardware.Length > 0 && active.Length == 0)
        {
            if (!hardware.All(adapter => IsStatus(adapter, "Disabled")))
            {
                return ModuleHelpers.Finding("net.adapters", ModuleId, Severity.Info,
                    "Finding_Network_NoConnectedAdapter", DescribeAdapters(hardware));
            }

            var finding = ModuleHelpers.Finding("net.adapters", ModuleId, Severity.Critical,
                "Finding_Network_AllAdaptersDisabled", DescribeAdapters(hardware), "net.cycle-adapters", "net.driver-reset", "net.full-reset");
            AttachDriverTarget(finding, hardware);
            return finding;
        }

        var oldCutoff = DateTimeOffset.Now.AddYears(-(int)context.Thresholds.Get("network.driverAgeYears", 3));
        var stale = hardware.Where(adapter =>
        {
            var text = ModuleHelpers.GetString(adapter, "DriverDate");
            return DateTimeOffset.TryParse(text, out var date) && date < oldCutoff;
        }).ToArray();
        if (stale.Length > 0)
        {
            var finding = ModuleHelpers.Finding("net.adapters", ModuleId, Severity.Warning,
                "Finding_Network_OldDriver", DescribeAdapters(stale), "net.driver-reset");
            AttachDriverTarget(finding, stale);
            return finding;
        }

        var slowEthernet = active.Where(adapter =>
        {
            var media = ModuleHelpers.GetString(adapter, "MediaType") ?? string.Empty;
            var speed = ModuleHelpers.GetString(adapter, "LinkSpeed") ?? string.Empty;
            return media.Contains("802.3", StringComparison.OrdinalIgnoreCase) && speed.Contains("100 Mbps", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        return slowEthernet.Length > 0
            ? ModuleHelpers.Finding("net.adapters", ModuleId, Severity.Warning,
                "Finding_Network_LinkAt100Mbps", DescribeAdapters(slowEthernet), "net.cycle-adapters")
            : null;
    }

    private static async Task<Finding?> CheckIpAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "Get-NetIPConfiguration -ErrorAction Stop | Where-Object {$_.NetAdapter.Status -eq 'Up'} | ForEach-Object {[pscustomobject]@{Alias=$_.InterfaceAlias;IPv4=@($_.IPv4Address.IPAddress);Gateway=@($_.IPv4DefaultGateway.NextHop);Dns=@($_.DNSServer.ServerAddresses)}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        if (rows.Length == 0)
        {
            return ModuleHelpers.Finding("net.ip", ModuleId, Severity.Info,
                "Finding_Network_NoConnectedAdapter", "No active IP configurations.");
        }

        var apipa = rows.Where(row => GetArrayStrings(row, "IPv4").Any(ip => ip.StartsWith("169.254.", StringComparison.Ordinal))).ToArray();
        if (apipa.Length > 0)
        {
            return ModuleHelpers.Finding("net.ip", ModuleId, Severity.Critical,
                "Finding_Network_Apipa", DescribeRows(apipa), "net.renew-dhcp", "net.clear-arp", "net.reset-stack", "net.full-reset");
        }

        var noGateway = rows.Where(row => GetArrayStrings(row, "IPv4").Any() && !GetArrayStrings(row, "Gateway").Any()).ToArray();
        return noGateway.Length > 0
            ? ModuleHelpers.Finding("net.ip", ModuleId, Severity.Warning,
                "Finding_Network_MissingGateway", DescribeRows(noGateway), "net.renew-dhcp", "net.reset-stack")
            : null;
    }

    private static async Task<Finding?> CheckGatewayAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$items=@(); Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq 'Up'} | ForEach-Object {$alias=$_.InterfaceAlias; foreach($gw in @($_.IPv4DefaultGateway.NextHop)){if($gw){$ok=Test-Connection -ComputerName $gw -Count 2 -Quiet -ErrorAction SilentlyContinue; $items += [pscustomobject]@{Alias=$alias;Gateway=$gw;Reachable=$ok}}}}; $items";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var failed = ModuleHelpers.Array(root).Where(row => !IsTrue(row, "Reachable")).ToArray();
        return failed.Length > 0
            ? ModuleHelpers.Finding("net.gateway", ModuleId, Severity.Warning,
                "Finding_Network_GatewayUnreachable", DescribeRows(failed), "net.clear-arp", "net.renew-dhcp", "net.cycle-adapters")
            : null;
    }

    private static async Task<Finding?> CheckDnsAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$current=@(Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object {$_.ServerAddresses} | Where-Object {$_} | Select-Object -Unique); $all=@($current + '1.1.1.1' + '8.8.8.8' | Select-Object -Unique); foreach($dns in $all){$sw=[Diagnostics.Stopwatch]::StartNew(); try {Resolve-DnsName 'www.microsoft.com' -Server $dns -DnsOnly -QuickTimeout -ErrorAction Stop | Out-Null; $ok=$true}catch{$ok=$false}; $sw.Stop(); [pscustomobject]@{Server=$dns;Current=($dns -in $current);Success=$ok;Ms=$sw.ElapsedMilliseconds}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        var current = rows.Where(row => IsTrue(row, "Current")).ToArray();
        var publicRows = rows.Where(row => !IsTrue(row, "Current")).ToArray();
        if (current.Length == 0)
        {
            return ModuleHelpers.Finding("net.dns", ModuleId, Severity.Critical,
                "Finding_Network_NoDnsServer", DescribeRows(rows), "net.public-dns", "net.renew-dhcp");
        }

        if (current.All(row => !IsTrue(row, "Success")) && publicRows.Any(row => IsTrue(row, "Success")))
        {
            return ModuleHelpers.Finding("net.dns", ModuleId, Severity.Critical,
                "Finding_Network_CurrentDnsBroken", DescribeRows(rows), "net.flush-dns", "net.public-dns");
        }

        return rows.All(row => !IsTrue(row, "Success"))
            ? ModuleHelpers.Finding("net.dns", ModuleId, Severity.Warning,
                "Finding_Network_DnsResolutionFailed", DescribeRows(rows), "net.flush-dns", "net.reset-stack")
            : null;
    }

    private static async Task<Finding?> CheckInternetAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "try {$r=Invoke-WebRequest 'http://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 3 -ErrorAction Stop; [pscustomobject]@{Success=$true;Status=[int]$r.StatusCode;Content=$r.Content.Trim();Uri=$r.BaseResponse.ResponseUri.AbsoluteUri}} catch {[pscustomobject]@{Success=$false;Error=$_.Exception.Message}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var row = ModuleHelpers.Array(root).FirstOrDefault();
        if (row.ValueKind != JsonValueKind.Object || !IsTrue(row, "Success"))
        {
            return ModuleHelpers.Finding("net.internet", ModuleId, Severity.Critical,
                "Finding_Network_InternetUnavailable", row.ToString(), "net.renew-dhcp", "net.reset-stack");
        }

        var content = ModuleHelpers.GetString(row, "Content") ?? string.Empty;
        return !content.Equals("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase)
            ? ModuleHelpers.Finding("net.internet", ModuleId, Severity.Warning,
                "Finding_Network_CaptivePortal", row.ToString())
            : null;
    }

    private static async Task<Finding?> CheckProxyAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$u=Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings' -ErrorAction SilentlyContinue; $w=(netsh winhttp dump | Out-String); [pscustomobject]@{UserEnabled=([int]$u.ProxyEnable -eq 1);UserServer=[string]$u.ProxyServer;AutoConfig=[string]$u.AutoConfigURL;WinHttpConfigured=($w -match '(?im)^\\s*set\\s+proxy\\b');WinHttp=$w}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var row = ModuleHelpers.Array(root).FirstOrDefault();
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var userEnabled = IsTrue(row, "UserEnabled");
        var server = ModuleHelpers.GetString(row, "UserServer") ?? string.Empty;
        var auto = ModuleHelpers.GetString(row, "AutoConfig") ?? string.Empty;
        var winHttp = ModuleHelpers.GetString(row, "WinHttp") ?? string.Empty;
        var winHttpCustom = IsTrue(row, "WinHttpConfigured");
        return userEnabled || !string.IsNullOrWhiteSpace(auto) || winHttpCustom
            ? ModuleHelpers.Finding("net.proxy", ModuleId, Severity.Warning,
                "Finding_Network_ProxyConfigured", $"User={server}; PAC={auto}; WinHTTP={winHttp}", "net.clear-proxy")
            : null;
    }

    private static async Task<Finding?> CheckVpnAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$pattern='TAP|Wintun|WireGuard|OpenVPN|Tailscale|ZeroTier|Hamachi|NordLynx|VPN'; $codes=@{}; Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue|ForEach-Object {$codes[$_.PNPDeviceID]=[int]$_.ConfigManagerErrorCode}; foreach($a in Get-NetAdapter -IncludeHidden | Where-Object {$_.InterfaceDescription -match $pattern -or $_.Name -match $pattern}) {$routes=@(Get-NetRoute -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0'}); $inf=(Get-PnpDeviceProperty -InstanceId $a.PnPDeviceID -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data; [pscustomobject]@{Name=$a.Name;Description=$a.InterfaceDescription;Status=[string]$a.Status;DefaultRoutes=$routes.Count;InstanceId=$a.PnPDeviceID;InfName=[string]$inf;ErrorCode=[int]$codes[$a.PnPDeviceID]}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var conflicts = ModuleHelpers.Array(root).Where(row =>
            !IsStatus(row, "Up") && GetInt(row, "DefaultRoutes") > 0).ToArray();
        if (conflicts.Length == 0) return null;

        var target = ModuleHelpers.GetString(conflicts[0], "InstanceId");
        var inf = ModuleHelpers.GetString(conflicts[0], "InfName");
        var canRemove = GetInt(conflicts[0], "ErrorCode") == 24 &&
                        !string.IsNullOrWhiteSpace(target) && IsPublishedInf(inf);
        string[] fixes = canRemove ? ["net.ghost-vpn-remove"] : [];
        var finding = ModuleHelpers.Finding(
            "net.vpn", ModuleId, Severity.Warning,
            "Finding_Network_VpnResidue", DescribeRows(conflicts), fixes);
        if (canRemove)
        {
            finding.RepairParameters["net.ghost-vpn-remove.target"] = target!;
            finding.RepairParameters["net.ghost-vpn-remove.inf"] = inf!;
        }
        return finding;
    }

    private static async Task<Finding?> CheckRoutesAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "Get-NetRoute -PolicyStore PersistentStore -AddressFamily IPv4 -ErrorAction Stop | Select-Object DestinationPrefix,NextHop,InterfaceAlias,InterfaceIndex,RouteMetric,State";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var routes = ModuleHelpers.Array(root).ToArray();
        var invalid = routes.Where(route =>
        {
            var state = ModuleHelpers.GetString(route, "State") ?? string.Empty;
            return state.Equals("Invalid", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("Unreachable", StringComparison.OrdinalIgnoreCase);
        });
        var conflictingDefaults = routes
            .Where(route => string.Equals(
                ModuleHelpers.GetString(route, "DestinationPrefix"),
                "0.0.0.0/0",
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(route => ModuleHelpers.GetString(route, "InterfaceIndex") ?? string.Empty)
            .Where(group => group.Key.Length > 0 && group.Select(route => ModuleHelpers.GetString(route, "NextHop")).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .SelectMany(group => group);
        var suspicious = invalid.Concat(conflictingDefaults).DistinctBy(route => route.ToString()).ToArray();
        if (suspicious.Length == 0) return null;

        var finding = ModuleHelpers.Finding(
                "net.routes",
                ModuleId,
                Severity.Warning,
                "Finding_Network_PersistentRouteConflict",
                DescribeRows(suspicious),
                "net.route-reset");
        var targets = NetworkParsers.EncodeRouteTargets(suspicious.Select(route => new PersistentRouteTarget(
            ModuleHelpers.GetString(route, "DestinationPrefix") ?? string.Empty,
            ModuleHelpers.GetString(route, "NextHop") ?? string.Empty,
            GetInt(route, "InterfaceIndex"))));
        if (!string.IsNullOrWhiteSpace(targets))
        {
            finding.RepairParameters["net.route-reset.target"] = targets;
        }
        return finding;
    }

    private static async Task<Finding?> CheckFirewallAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$profiles=Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultInboundAction,DefaultOutboundAction; $broad=@(Get-NetFirewallRule -Enabled True -Direction Outbound -Action Block -ErrorAction SilentlyContinue | Where-Object {(@(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $_ -ErrorAction SilentlyContinue).RemoteAddress) -contains 'Any'}); [pscustomobject]@{Profiles=@($profiles);BroadOutboundBlocks=$broad.Count}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var row = ModuleHelpers.Array(root).FirstOrDefault();
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasProfiles = ModuleHelpers.TryGetPropertyIgnoreCase(row, "Profiles", out var profiles) &&
                          profiles.ValueKind == JsonValueKind.Array;
        var abnormal = hasProfiles &&
                       profiles.EnumerateArray().Any(profile =>
                           string.Equals(ModuleHelpers.GetString(profile, "DefaultOutboundAction"), "Block", StringComparison.OrdinalIgnoreCase));
        var disabled = hasProfiles && profiles.EnumerateArray().Any(profile => !IsTrue(profile, "Enabled"));
        var broadBlocks = GetInt(row, "BroadOutboundBlocks");
        return abnormal || broadBlocks > 0
            ? ModuleHelpers.Finding("net.firewall", ModuleId, Severity.Critical,
                "Finding_Network_FirewallBlocking", row.ToString(), "net.firewall-reset")
            : disabled
                ? ModuleHelpers.Finding("net.firewall", ModuleId, Severity.Warning,
                    "Finding_Network_FirewallDisabled", row.ToString())
                : null;
    }

    private static Task<Finding?> CheckHostsAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        if (!File.Exists(hosts))
        {
            return Task.FromResult<Finding?>(ModuleHelpers.Finding("net.hosts", ModuleId, Severity.Warning,
                "Finding_Network_HostsMissing", hosts, "net.restore-hosts"));
        }

        var attributes = File.GetAttributes(hosts);
        var size = new FileInfo(hosts).Length;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || size > 1024 * 1024)
        {
            return Task.FromResult<Finding?>(ModuleHelpers.Finding(
                "net.hosts", ModuleId, Severity.Warning,
                "Finding_Network_CustomHosts", $"size={size}; reparse={attributes.HasFlag(FileAttributes.ReparsePoint)}", "net.restore-hosts"));
        }

        var custom = File.ReadLines(hosts)
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.Equals("127.0.0.1 localhost", StringComparison.OrdinalIgnoreCase) &&
                           !line.Equals("::1 localhost", StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToArray();
        return Task.FromResult<Finding?>(custom.Length > 0
            ? ModuleHelpers.Finding("net.hosts", ModuleId, Severity.Warning,
                "Finding_Network_CustomHosts", string.Join(Environment.NewLine, custom), "net.restore-hosts")
            : null);
    }

    private static async Task<Finding?> CheckWinsockAsync(DiagnosticContext context, CancellationToken ct)
    {
        var result = await context.Commands.RunAsync(
            "netsh.exe", ["winsock", "show", "catalog"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return ModuleHelpers.Finding("net.winsock", ModuleId, Severity.Warning,
                "Finding_Network_WinsockUnreadable", result.StdErr, "net.reset-stack");
        }

        var thirdParty = ExtractThirdPartyLspEntries(result.StdOut);
        return thirdParty.Count > 0
            ? ModuleHelpers.Finding("net.winsock", ModuleId, Severity.Warning,
                "Finding_Network_ThirdPartyLsp", string.Join(Environment.NewLine, thirdParty.Take(20)), "net.reset-stack")
            : null;
    }

    private static async Task<Finding?> CheckMtuAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "Get-NetIPInterface -AddressFamily IPv4 -ConnectionState Connected -ErrorAction Stop | Select-Object InterfaceAlias,NlMtuBytes,ConnectionState";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var low = ModuleHelpers.Array(root).Where(row => GetInt(row, "NlMtuBytes") is > 0 and < 1_280).ToArray();
        if (low.Length > 0)
        {
            return ModuleHelpers.Finding("net.mtu", ModuleId, Severity.Warning,
                "Finding_Network_MtuTooLow", DescribeRows(low), "net.reset-stack");
        }

        // Path-MTU probe (Do-Not-Fragment) for fragmentation issues below 1500.
        var probe = await context.Commands.RunAsync(
            "ping.exe",
            ["-n", "1", "-f", "-l", "1472", "1.1.1.1"],
            TimeSpan.FromSeconds(20),
            ct).ConfigureAwait(false);
        var output = probe.StdOut + probe.StdErr;
        if (output.Contains("fragment", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("paket parçalanmalı", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("could not find host", StringComparison.OrdinalIgnoreCase))
        {
            // Offline or blocked ICMP is not treated as MTU failure.
            if (output.Contains("fragment", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("paket parçalanmalı", StringComparison.OrdinalIgnoreCase))
            {
                return ModuleHelpers.Finding("net.mtu", ModuleId, Severity.Warning,
                    "Finding_Network_MtuTooLow", output.Trim(), "net.reset-stack");
            }
        }

        return null;
    }

    private static async Task<Finding?> CheckPowerAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$ids=@(Get-NetAdapter -Physical -ErrorAction SilentlyContinue).PnPDeviceID; Get-CimInstance -Namespace root/wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | Where-Object {$enabled=$_.Enable; $name=$_.InstanceName; $enabled -and ($ids | Where-Object {$name -like \"$_*\"})} | Select-Object InstanceName,Enable";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        return rows.Length > 0
            ? ModuleHelpers.Finding("net.power", ModuleId, Severity.Warning,
                "Finding_Network_PowerSavingEnabled", DescribeRows(rows), "net.disable-power-saving")
            : null;
    }

    private static async Task<Finding?> CheckEventLogAsync(DiagnosticContext context, CancellationToken ct)
    {
        // Correlate recent network adapter / TCPIP / NDIS failures from the System log.
        const string script =
            "$since=(Get-Date).AddDays(-3); " +
            "$providers=@('Tcpip','Tcpip6','NDIS','Netwtw04','Netwtw06','Netwtw08','Netwtw10','e1dexpress','rt640x64','rtwlane','vwifimp','BTHUSB','RasMan','Dhcp-Client','DNS Client Events'); " +
            "$events=@(); " +
            "foreach($p in $providers){ " +
            "  $events += @(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName=$p;StartTime=$since;Level=1,2,3} -MaxEvents 10 -ErrorAction SilentlyContinue) " +
            "}; " +
            "$events | Sort-Object TimeCreated -Descending | Select-Object -First 14 TimeCreated,Id,ProviderName,LevelDisplayName,Message";
        try
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length >= 3
                ? ModuleHelpers.Finding(
                    "net.eventlog",
                    ModuleId,
                    Severity.Warning,
                    "Finding_Network_EventLogErrors",
                    DescribeRows(rows),
                    "net.soft-heal",
                    "net.restart-services",
                    "net.rescan-devices",
                    "net.reset-stack")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Finding?> CheckBindingsAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script =
            "Get-NetAdapterBinding -ComponentID ms_tcpip,ms_tcpip6 -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Enabled -eq $false -and $_.Name -notmatch 'Loopback|vEthernet|Virtual|Hyper-V|WSL|Docker|VMware|VirtualBox' } | " +
            "Select-Object Name,DisplayName,ComponentID,Enabled";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        return rows.Length > 0
            ? ModuleHelpers.Finding(
                "net.bindings",
                ModuleId,
                Severity.Warning,
                "Finding_Network_ProtocolDisabled",
                DescribeRows(rows),
                "net.soft-heal",
                "net.reset-stack",
                "net.full-reset")
            : null;
    }

    private static async Task<Finding?> CheckServicesAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('Dnscache','Dhcp','WlanSvc','NlaSvc','netprofm','WinHttpAutoProxySvc')} | Select-Object Name,State,StartMode";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var bad = ModuleHelpers.Array(root).Where(row =>
        {
            var name = ModuleHelpers.GetString(row, "Name") ?? string.Empty;
            var state = ModuleHelpers.GetString(row, "State") ?? string.Empty;
            var start = ModuleHelpers.GetString(row, "StartMode") ?? string.Empty;
            var mayBeStopped = name.Equals("WinHttpAutoProxySvc", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("WlanSvc", StringComparison.OrdinalIgnoreCase);
            return start.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
                   (!mayBeStopped && !state.Equals("Running", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        return bad.Length > 0
            ? ModuleHelpers.Finding("net.services", ModuleId, Severity.Warning,
                "Finding_Network_ServiceStopped", DescribeRows(bad), "net.restart-services")
            : null;
    }

    private static async Task<Finding?> CheckPerformanceAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$tcp=Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue | Select-Object AutoTuningLevelLocal,EcnCapability,ScalingHeuristics; $rss=@(Get-NetAdapterRss -ErrorAction SilentlyContinue | Where-Object {$_.Enabled -eq $false}).Name; [pscustomobject]@{Tcp=$tcp;RssDisabled=@($rss)}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var row = ModuleHelpers.Array(root).FirstOrDefault();
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var abnormal = false;
        if (ModuleHelpers.TryGetPropertyIgnoreCase(row, "Tcp", out var tcp) && tcp.ValueKind == JsonValueKind.Object)
        {
            var auto = ModuleHelpers.GetString(tcp, "AutoTuningLevelLocal") ?? string.Empty;
            var heuristics = ModuleHelpers.GetString(tcp, "ScalingHeuristics") ?? string.Empty;
            abnormal = !auto.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
                       heuristics.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }

        var rssDisabled = GetArrayStrings(row, "RssDisabled").Any();
        var wlan = await context.Commands.RunAsync("netsh.exe", ["wlan", "show", "interfaces"], TimeSpan.FromMinutes(1), ct)
            .ConfigureAwait(false);
        var values = NetworkParsers.ParseColonTable(wlan.StdOut);
        var signal = NetworkParsers.ParsePercent(values, "Signal", "Sinyal");
        if (abnormal || rssDisabled)
        {
            return ModuleHelpers.Finding("net.performance", ModuleId, Severity.Warning,
                "Finding_Network_TcpSettingsAbnormal", row.ToString(), "net.normalize-tcp");
        }

        if (signal.HasValue && signal.Value < context.Thresholds.Get("network.wifiSignalWarningPercent", 45))
        {
            return ModuleHelpers.Finding("net.performance", ModuleId, Severity.Warning,
                "Finding_Network_WeakWifi", $"Signal={signal:0}%", "net.cycle-adapters");
        }

        var band = values.GetValueOrDefault("Radio type") ??
                   values.GetValueOrDefault("Radyo türü") ??
                   values.GetValueOrDefault("Band") ??
                   string.Empty;
        var receiveRate = values.GetValueOrDefault("Receive rate (Mbps)") ??
                          values.GetValueOrDefault("Alma hızı (Mbps)") ??
                          values.GetValueOrDefault("Receive rate") ??
                          string.Empty;
        var looks24Ghz = band.Contains("802.11b", StringComparison.OrdinalIgnoreCase) ||
                         band.Contains("802.11g", StringComparison.OrdinalIgnoreCase) ||
                         band.Contains("2.4", StringComparison.OrdinalIgnoreCase);
        if (looks24Ghz &&
            double.TryParse(receiveRate.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var phyMbps) &&
            phyMbps is > 0 and < 100)
        {
            return ModuleHelpers.Finding("net.performance", ModuleId, Severity.Warning,
                "Finding_Network_WifiBand", $"Band={band}; ReceiveRate={receiveRate}", "net.cycle-adapters");
        }

        return null;
    }

    private static async Task<Finding?> CheckThroughputAsync(DiagnosticContext context, CancellationToken ct)
    {
        var ratio = context.Thresholds.Get("network.throughputToLinkWarningRatio", 0.10);
        double? bestLinkMbps = null;
        try
        {
            const string script = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Select-Object Name,LinkSpeed";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            foreach (var adapter in ModuleHelpers.Array(root))
            {
                var linkText = ModuleHelpers.GetString(adapter, "LinkSpeed") ?? string.Empty;
                if (TryParseLinkSpeedMbps(linkText, out var linkMbps))
                {
                    bestLinkMbps = bestLinkMbps is null ? linkMbps : Math.Max(bestLinkMbps.Value, linkMbps);
                }
            }
        }
        catch
        {
            // LinkSpeed probe is best-effort; absolute threshold remains as fallback.
        }

        await foreach (var progress in RunSpeedTestAsync(context, ct).ConfigureAwait(false))
        {
            if (progress.Metrics is null || !progress.Metrics.TryGetValue("Mbps", out var mbps))
            {
                continue;
            }

            var threshold = bestLinkMbps is > 0
                ? Math.Max(1, bestLinkMbps.Value * ratio)
                : 1;
            if (mbps < threshold)
            {
                return ModuleHelpers.Finding("net.throughput", ModuleId, Severity.Warning,
                    "Finding_Network_ThroughputLow",
                    $"Measured={mbps:0.0} Mbps; Link≈{bestLinkMbps:0.#} Mbps; threshold={threshold:0.0} Mbps",
                    "net.normalize-tcp");
            }
        }

        return null;
    }

    private static DelegateFixAction CreateFlushDnsFix() => new(
        "net.flush-dns", "Fix_Network_FlushDns", ModuleId, RiskTier.Safe,
        (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "dns-cache", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("ipconfig.exe", ["/flushdns"])], ct),
        async (context, ct) => (await context.Commands.RunAsync("ipconfig.exe", ["/displaydns"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).Success);

    private static DelegateFixAction CreateIpconfigSuiteFix() => new(
        "net.ipconfig-suite", "Fix_Network_IpconfigSuite", ModuleId, RiskTier.Safe,
        (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "ipconfig-suite", ct),
        // Same resilient release→renew pipeline so a partial /release failure never skips /renew.
        (context, ct) => ApplyDhcpRenewAsync(context, activeOnly: false, includeFlushAndRegister: true, ct),
        async (context, ct) =>
        {
            var result = await context.Commands.RunAsync(
                "ipconfig.exe",
                ["/all"],
                TimeSpan.FromMinutes(1),
                ct).ConfigureAwait(false);
            return result.Success && !string.IsNullOrWhiteSpace(result.StdOut);
        });

    private static DelegateFixAction CreateRenewDhcpActiveFix() => new(
        "net.renew-dhcp", "Fix_Network_RenewDhcp", ModuleId, RiskTier.Safe,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "network-interface", "netsh.exe", ["-c", "interface", "dump"],
            "netsh.exe", ["-f", "{backup}"], ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ApplyDhcpRenewAsync(context, activeOnly: true, includeFlushAndRegister: false, ct),
        VerifyDhcpRenewAsync);

    private static DelegateFixAction CreateRenewDhcpAllFix() => new(
        "net.renew-dhcp-all", "Fix_Network_RenewDhcpAll", ModuleId, RiskTier.Safe,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "network-interface", "netsh.exe", ["-c", "interface", "dump"],
            "netsh.exe", ["-f", "{backup}"], ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ApplyDhcpRenewAsync(context, activeOnly: false, includeFlushAndRegister: false, ct),
        VerifyDhcpRenewAsync);

    private static async Task<bool> VerifyDhcpRenewAsync(FixContext context, CancellationToken ct)
    {
        // After release/renew, wait briefly then require at least one non-APIPA address when possible.
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            "Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address.IPAddress -notlike '169.254.*'} | Select-Object InterfaceAlias",
            ct).ConfigureAwait(false);
        if (ModuleHelpers.Array(root).Any()) return true;

        // Soft pass: adapters may still be associating; avoid false-fail after a successful renew attempt.
        var anyUp = await context.Commands.RunPsJsonAsync<JsonElement>(
            "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Select-Object Name",
            ct).ConfigureAwait(false);
        return ModuleHelpers.Array(anyUp).Any();
    }

    private static DelegateFixAction CreateProfileRepairActiveFix() => new(
        "net.profile-repair-active", "Fix_Network_ProfileRepairActive", ModuleId, RiskTier.Safe,
        (context, ct) => CaptureProfileRepairBackupAsync(context, "network-profile-active", ct),
        (context, ct) => ApplyNetworkProfileRepairAsync(context, activeOnly: true, ct),
        (context, ct) => VerifyProfileRepairAsync(context, activeOnly: true, ct));

    private static DelegateFixAction CreateProfileRepairAllFix() => new(
        "net.profile-repair-all", "Fix_Network_ProfileRepairAll", ModuleId, RiskTier.Safe,
        (context, ct) => CaptureProfileRepairBackupAsync(context, "network-profile-all", ct),
        (context, ct) => ApplyNetworkProfileRepairAsync(context, activeOnly: false, ct),
        (context, ct) => VerifyProfileRepairAsync(context, activeOnly: false, ct));

    private static async Task<BackupEntry?> CaptureProfileRepairBackupAsync(
        FixContext context,
        string label,
        CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var state = await context.Backups.CaptureCommandStateAsync(
            "network-interface", "netsh.exe", ["-c", "interface", "dump"],
            "netsh.exe", ["-f", "{backup}"], dir, ct).ConfigureAwait(false);
        var services = await context.Backups.CaptureServicesAsync(NetworkServices, dir, ct).ConfigureAwait(false);
        return state is not null && services is not null
            ? await context.Backups.CaptureBundleAsync(label, [state, services], dir, ct).ConfigureAwait(false)
            : state ?? services;
    }

    private static async Task<bool> VerifyProfileRepairAsync(FixContext context, bool activeOnly, CancellationToken ct)
    {
        var script = activeOnly
            ? "Get-NetConnectionProfile | Where-Object {$_.IPv4Connectivity -ne 'Disconnected' -or $_.IPv6Connectivity -ne 'Disconnected'} | Select-Object Name,InterfaceAlias,NetworkCategory"
            : "Get-NetConnectionProfile | Select-Object Name,InterfaceAlias,NetworkCategory";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        return ModuleHelpers.Array(root).Any() ||
               (await context.Commands.RunAsync("sc.exe", ["query", "Dhcp"], TimeSpan.FromMinutes(1), ct)
                   .ConfigureAwait(false)).Success;
    }

    /// <summary>
    /// Ensures the DHCP client service is up, then release→renew per adapter without aborting on
    /// non-zero ipconfig exit codes (the previous failure mode left the machine without a lease).
    /// </summary>
    private static Task<FixResult> ApplyDhcpRenewAsync(
        FixContext context,
        bool activeOnly,
        bool includeFlushAndRegister,
        CancellationToken ct)
    {
        var scope = activeOnly ? "$true" : "$false";
        var flush = includeFlushAndRegister ? "$true" : "$false";
        var script =
            "$ErrorActionPreference='Continue'; " +
            "$activeOnly=" + scope + "; $includeFlush=" + flush + "; " +
            "$ipconfig=Join-Path $env:windir 'System32\\ipconfig.exe'; " +
            "function Ensure-Svc([string]$n,[string]$start='Manual'){ " +
            "  try{ $s=Get-Service -Name $n -ErrorAction Stop; " +
            "    if($s.StartType -eq 'Disabled'){ Set-Service -Name $n -StartupType $start -ErrorAction SilentlyContinue }; " +
            "    if($s.Status -ne 'Running'){ Start-Service -Name $n -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 } " +
            "  }catch{} " +
            "}; " +
            "Ensure-Svc 'Dhcp' 'Automatic'; Ensure-Svc 'Dnscache' 'Automatic'; " +
            "Ensure-Svc 'NlaSvc' 'Automatic'; Ensure-Svc 'netprofm' 'Manual'; Ensure-Svc 'WlanSvc' 'Manual'; " +
            "if($includeFlush){ try{ & $ipconfig /flushdns | Out-Null }catch{} }; " +
            "$names=@(); " +
            "try{ " +
            "  $ifaces=@(Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { " +
            "    $_.Dhcp -eq 'Enabled' -and (-not $activeOnly -or $_.ConnectionState -eq 'Connected') " +
            "  }); " +
            "  foreach($i in $ifaces){ " +
            "    try{ $a=Get-NetAdapter -InterfaceIndex $i.InterfaceIndex -ErrorAction SilentlyContinue; " +
            "      if($null -ne $a -and $a.Name -and [string]$a.Status -ne 'Disabled'){ $names += [string]$a.Name } " +
            "    }catch{} " +
            "  } " +
            "}catch{}; " +
            "$names=@($names | Select-Object -Unique); " +
            "if($names.Count -eq 0){ " +
            "  try{ & $ipconfig /release | Out-Null }catch{}; Start-Sleep -Seconds 2; " +
            "  try{ & $ipconfig /renew | Out-Null }catch{}; Start-Sleep -Seconds 3; " +
            "} else { " +
            "  foreach($name in $names){ " +
            "    # Never abort after release: always renew the same adapter. " +
            "    try{ & $ipconfig /release $name | Out-Null }catch{}; " +
            "    Start-Sleep -Seconds 2; " +
            "    try{ & $ipconfig /renew $name | Out-Null }catch{}; " +
            "    Start-Sleep -Seconds 2; " +
            "  }; " +
            "  # Safety net: global renew covers any adapter the per-name loop missed. " +
            "  try{ & $ipconfig /renew | Out-Null }catch{}; Start-Sleep -Seconds 2; " +
            "}; " +
            "if($includeFlush){ try{ & $ipconfig /registerdns | Out-Null }catch{}; try{ & $ipconfig /flushdns | Out-Null }catch{} }; " +
            "exit 0";

        return ModuleHelpers.RunSequenceAsync(context,
        [
            new CommandStep(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromMinutes(8))
        ], ct);
    }

    private static Task<FixResult> ApplyNetworkProfileRepairAsync(
        FixContext context,
        bool activeOnly,
        CancellationToken ct)
    {
        var scope = activeOnly ? "$true" : "$false";
        var script =
            "$ErrorActionPreference='Continue'; $activeOnly=" + scope + "; " +
            "$ipconfig=Join-Path $env:windir 'System32\\ipconfig.exe'; " +
            "function Ensure-Svc([string]$n,[string]$start='Manual'){ " +
            "  try{ $s=Get-Service -Name $n -ErrorAction Stop; " +
            "    if($s.StartType -eq 'Disabled'){ Set-Service -Name $n -StartupType $start -ErrorAction SilentlyContinue }; " +
            "    if($s.Status -ne 'Running'){ Start-Service -Name $n -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 } " +
            "  }catch{} " +
            "}; " +
            "Ensure-Svc 'Dhcp' 'Automatic'; Ensure-Svc 'Dnscache' 'Automatic'; Ensure-Svc 'NlaSvc' 'Automatic'; " +
            "Ensure-Svc 'netprofm' 'Manual'; Ensure-Svc 'WlanSvc' 'Manual'; Ensure-Svc 'WinHttpAutoProxySvc' 'Manual'; " +
            "$profiles=@(); " +
            "try{ $profiles=@(Get-NetConnectionProfile -ErrorAction SilentlyContinue) }catch{}; " +
            "if($activeOnly){ $profiles=@($profiles | Where-Object { " +
            "  [string]$_.IPv4Connectivity -ne 'Disconnected' -or [string]$_.IPv6Connectivity -ne 'Disconnected' }) }; " +
            "$cats=New-Object 'System.Collections.Generic.HashSet[string]'; " +
            "foreach($p in $profiles){ " +
            "  try{ [void]$cats.Add([string]$p.NetworkCategory) }catch{}; " +
            "  $idx=[int]$p.InterfaceIndex; $alias=[string]$p.InterfaceAlias; " +
            "  try{ Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled -ErrorAction SilentlyContinue }catch{}; " +
            "  try{ Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction SilentlyContinue }catch{}; " +
            "  if(-not [string]::IsNullOrWhiteSpace($alias)){ " +
            "    try{ & $ipconfig /release $alias | Out-Null }catch{}; Start-Sleep -Seconds 2; " +
            "    try{ & $ipconfig /renew $alias | Out-Null }catch{}; Start-Sleep -Seconds 2; " +
            "  } " +
            "}; " +
            "if(-not $activeOnly -or $profiles.Count -eq 0){ " +
            "  try{ & $ipconfig /renew | Out-Null }catch{}; " +
            "}; " +
            "$firewallTargets=@(); " +
            "if(-not $activeOnly){ $firewallTargets=@('Domain','Private','Public') } " +
            "else { " +
            "  foreach($c in $cats){ " +
            "    if($c -eq 'DomainAuthenticated' -or $c -eq 'Domain'){ $firewallTargets += 'Domain' } " +
            "    elseif($c -eq 'Private'){ $firewallTargets += 'Private' } " +
            "    elseif($c -eq 'Public'){ $firewallTargets += 'Public' } " +
            "  } " +
            "}; " +
            "foreach($fp in @($firewallTargets | Select-Object -Unique)){ " +
            "  try{ Set-NetFirewallProfile -Profile $fp -Enabled True -ErrorAction SilentlyContinue }catch{} " +
            "}; " +
            "try{ & $ipconfig /registerdns | Out-Null }catch{}; " +
            "exit 0";

        return ModuleHelpers.RunSequenceAsync(context,
        [
            new CommandStep(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromMinutes(8))
        ], ct);
    }

    private static DelegateFixAction CreateCycleAdaptersFix() => new(
        "net.cycle-adapters", "Fix_Network_CycleAdapters", ModuleId, RiskTier.Safe,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "adapter-state", "powershell.exe",
            ["-NoProfile", "-Command", "Get-NetAdapter -Physical | Select-Object Name,Status,AdminStatus | ConvertTo-Json -Compress"],
            "powershell.exe",
            ["-NoProfile", "-Command", "$s=Get-Content -Raw '{backup}'|ConvertFrom-Json; @($s)|ForEach-Object {$enabled=([string]$_.AdminStatus -eq 'Up') -or (-not $_.PSObject.Properties['AdminStatus'] -and [string]$_.Status -ne 'Disabled'); if($enabled){Enable-NetAdapter -Name $_.Name -Confirm:$false}else{Disable-NetAdapter -Name $_.Name -Confirm:$false}}"],
            ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "$a=@(Get-NetAdapter -Physical -ErrorAction Stop); $a|Where-Object Status -eq 'Disabled'|Enable-NetAdapter -Confirm:$false -ErrorAction Stop; Start-Sleep -Milliseconds 500; Get-NetAdapter -Physical|Where-Object Status -eq 'Up'|Restart-NetAdapter -Confirm:$false -ErrorAction Stop"], TimeSpan.FromMinutes(3))], ct),
        async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Select-Object Name", ct).ConfigureAwait(false);
            return ModuleHelpers.Array(root).Any();
        });

    private static DelegateFixAction CreateSoftHealFix() => new(
        "net.soft-heal", "Fix_Network_SoftHeal", ModuleId, RiskTier.Safe,
        async (context, ct) =>
        {
            var dir = ModuleHelpers.BackupDirectory(context);
            var services = await context.Backups.CaptureServicesAsync(NetworkServices, dir, ct).ConfigureAwait(false);
            var marker = await ModuleHelpers.TransientMarkerAsync(context, "network-soft-heal", ct).ConfigureAwait(false);
            return services is not null && marker is not null
                ? await context.Backups.CaptureBundleAsync("network-soft-heal", [services, marker], dir, ct).ConfigureAwait(false)
                : services ?? marker;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
        [
            new CommandStep("ipconfig.exe", ["/flushdns"]),
            new CommandStep("arp.exe", ["-d", "*"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
            new CommandStep("netsh.exe", ["interface", "ip", "delete", "arpcache"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
            new CommandStep(
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "$names=@('Dnscache','Dhcp','NlaSvc','netprofm','WlanSvc'); " +
                    "foreach($n in $names){ " +
                    "  try{ $s=Get-CimInstance Win32_Service -Filter \"Name='$n'\" -ErrorAction Stop; " +
                    "    if($s.StartMode -eq 'Disabled'){ Set-Service -Name $n -StartupType Manual -ErrorAction SilentlyContinue }; " +
                    "    if((Get-Service -Name $n -ErrorAction SilentlyContinue).Status -ne 'Running'){ Start-Service -Name $n -ErrorAction SilentlyContinue } " +
                    "  } catch {} " +
                    "}; exit 0"
                ],
                TimeSpan.FromMinutes(3)),
            new CommandStep("ipconfig.exe", ["/registerdns"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } }
        ], ct),
        async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                "Get-Service Dnscache,Dhcp,NlaSvc | Select-Object Name,Status",
                ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length >= 2 && rows.All(row => IsStatus(row, "Running"));
        });

    private static DelegateFixAction CreateRescanDevicesFix() => new(
        "net.rescan-devices", "Fix_Network_RescanDevices", ModuleId, RiskTier.Safe,
        (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "network-rescan-devices", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) =>
            (await context.Commands.RunAsync(
                "pnputil.exe",
                ["/enum-devices", "/class", "Net"],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false)).Success);

    private static DelegateFixAction CreateRestartServicesFix() => new(
        "net.restart-services", "Fix_Network_RestartServices", ModuleId, RiskTier.Safe,
        (context, ct) => context.Backups.CaptureServicesAsync(NetworkServices, ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
        [
            new CommandStep(
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "$ErrorActionPreference='Continue'; " +
                    "$map=@{" +
                    "  'Dnscache'='Automatic'; 'Dhcp'='Automatic'; 'NlaSvc'='Automatic'; " +
                    "  'netprofm'='Manual'; 'WlanSvc'='Manual'; 'WinHttpAutoProxySvc'='Manual' " +
                    "}; " +
                    "foreach($n in $map.Keys){ " +
                    "  try{ " +
                    "    $svc=Get-Service -Name $n -ErrorAction Stop; " +
                    "    if($svc.StartType -eq 'Disabled'){ Set-Service -Name $n -StartupType $map[$n] -ErrorAction SilentlyContinue }; " +
                    "    # Full restart when already running — recovers hung DHCP/DNS/NLA clients. " +
                    "    if($svc.Status -eq 'Running'){ Restart-Service -Name $n -Force -ErrorAction SilentlyContinue } " +
                    "    else { Start-Service -Name $n -ErrorAction SilentlyContinue }; " +
                    "    Start-Sleep -Milliseconds 400 " +
                    "  }catch{} " +
                    "}; exit 0"
                ],
                TimeSpan.FromMinutes(4))
        ], ct),
        async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-Service Dnscache,Dhcp,NlaSvc,netprofm | Select-Object Name,Status", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length == 4 && rows.All(row => IsStatus(row, "Running"));
        });

    private static DelegateFixAction CreateClearArpFix() => new(
        "net.clear-arp", "Fix_Network_ClearArp", ModuleId, RiskTier.Safe,
        (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "arp-cache", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("arp.exe", ["-d", "*"]), new CommandStep("netsh.exe", ["interface", "ip", "delete", "arpcache"])], ct),
        async (context, ct) => (await context.Commands.RunAsync("arp.exe", ["-a"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).Success);

    private static DelegateFixAction CreateResetStackFix() => new(
        "net.reset-stack", "Fix_Network_ResetStack", ModuleId, RiskTier.Moderate,
        async (context, ct) =>
        {
            var dir = ModuleHelpers.BackupDirectory(context);
            var entries = new List<BackupEntry>();
            var state = await context.Backups.CaptureCommandStateAsync("network-interface", "netsh.exe", ["-c", "interface", "dump"], "netsh.exe", ["-f", "{backup}"], dir, ct).ConfigureAwait(false);
            if (state is not null) entries.Add(state);
            var registry = await context.Backups.CaptureRegistryAsync(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", dir, ct).ConfigureAwait(false);
            if (registry is not null) entries.Add(registry);
            return entries.Count == 2 ? await context.Backups.CaptureBundleAsync("network-stack", entries, dir, ct).ConfigureAwait(false) : null;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("netsh.exe", ["winsock", "reset"]), new CommandStep("netsh.exe", ["int", "ip", "reset"]), new CommandStep("netsh.exe", ["int", "ipv6", "reset"])], ct),
        async (context, ct) => (await context.Commands.RunAsync("netsh.exe", ["winsock", "show", "catalog"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success,
        requiresReboot: true);

    private static DelegateFixAction CreateNormalizeTcpFix() => new(
        "net.normalize-tcp", "Fix_Network_NormalizeTcp", ModuleId, RiskTier.Moderate,
        (context, ct) => context.Backups.CaptureRegistryAsync(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("netsh.exe", ["interface", "tcp", "set", "global", "autotuninglevel=normal"]), new CommandStep("netsh.exe", ["interface", "tcp", "set", "global", "rss=enabled"]), new CommandStep("netsh.exe", ["interface", "tcp", "set", "heuristics", "disabled"])], ct),
        async (context, ct) =>
        {
            var result = await context.Commands.RunAsync(
                "netsh.exe",
                ["interface", "tcp", "show", "global"],
                TimeSpan.FromMinutes(1),
                ct).ConfigureAwait(false);
            // Require Auto-Tuning Level = normal (not bare "enabled"/"normal" anywhere).
            return result.Success &&
                   SystemCommandResultParsers.IsTcpAutotuningNormal(result.StdOut, result.StdErr);
        });

    private static DelegateFixAction CreateDisablePowerSavingFix() => new(
        "net.disable-power-saving", "Fix_Network_DisablePowerSaving", ModuleId, RiskTier.Moderate,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "nic-power", "powershell.exe",
            ["-NoProfile", "-Command", "$ids=@(Get-NetAdapter -Physical -ErrorAction Stop).PnPDeviceID; Get-CimInstance -Namespace root/wmi -Class MSPower_DeviceEnable | Where-Object {$n=$_.InstanceName; $ids|Where-Object {$n -like \"$_*\"}} | Select-Object InstanceName,Enable | ConvertTo-Json -Compress"],
            "powershell.exe",
            ["-NoProfile", "-Command", "$old=Get-Content -Raw '{backup}'|ConvertFrom-Json; $all=Get-CimInstance -Namespace root/wmi -Class MSPower_DeviceEnable; @($old)|ForEach-Object {$o=$_;$m=$all|Where-Object InstanceName -eq $o.InstanceName; if($m){$m.Enable=[bool]$o.Enable; Set-CimInstance $m}}"],
            ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "$ids=@(Get-NetAdapter -Physical).PnPDeviceID; Get-CimInstance -Namespace root/wmi -Class MSPower_DeviceEnable | Where-Object {$n=$_.InstanceName; $_.Enable -and ($ids|Where-Object {$n -like \"$_*\"})} | ForEach-Object {$_.Enable=$false; Set-CimInstance $_ -ErrorAction Stop}"], TimeSpan.FromMinutes(2))], ct),
        async (context, ct) => (await CheckPowerAsync(new DiagnosticContext { Commands = context.Commands, Text = context.Text, Thresholds = new Thresholds(), SessionDirectory = context.Session.DirectoryPath }, ct).ConfigureAwait(false)) is null);

    private static DelegateFixAction CreateClearProxyFix() => new(
        "net.clear-proxy", "Fix_Network_ClearProxy", ModuleId, RiskTier.Moderate,
        async (context, ct) =>
        {
            var dir = ModuleHelpers.BackupDirectory(context);
            var entries = new List<BackupEntry>();
            var reg = await context.Backups.CaptureRegistryAsync(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", dir, ct).ConfigureAwait(false);
            if (reg is not null) entries.Add(reg);
            var winhttp = await context.Backups.CaptureCommandStateAsync("winhttp-proxy", "netsh.exe", ["winhttp", "dump"], "netsh.exe", ["exec", "{backup}"], dir, ct).ConfigureAwait(false);
            if (winhttp is not null) entries.Add(winhttp);
            return entries.Count == 2 ? await context.Backups.CaptureBundleAsync("proxy", entries, dir, ct).ConfigureAwait(false) : null;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("netsh.exe", ["winhttp", "reset", "proxy"]), new CommandStep("reg.exe", ["add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "/v", "ProxyEnable", "/t", "REG_DWORD", "/d", "0", "/f"]), new CommandStep("reg.exe", ["delete", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "/v", "AutoConfigURL", "/f"]) { AcceptedExitCodes = new HashSet<int> { 1 } }], ct),
        async (context, ct) => (await CheckProxyAsync(new DiagnosticContext { Commands = context.Commands, Text = context.Text, Thresholds = new Thresholds(), SessionDirectory = context.Session.DirectoryPath }, ct).ConfigureAwait(false)) is null);

    private static DelegateFixAction CreateRestoreHostsFix() => new(
        "net.restore-hosts", "Fix_Network_RestoreHosts", ModuleId, RiskTier.Moderate,
        CaptureHostsBackupAsync,
        async (context, ct) =>
        {
            await File.WriteAllTextAsync(HostsPath(), "# CaYaFix default hosts file\r\n127.0.0.1 localhost\r\n::1 localhost\r\n", ct).ConfigureAwait(false);
            return FixResult.Ok("FixResult_Applied");
        },
        async (context, ct) => await CheckHostsAsync(new DiagnosticContext { Commands = context.Commands, Text = context.Text, Thresholds = new Thresholds(), SessionDirectory = context.Session.DirectoryPath }, ct).ConfigureAwait(false) is null);

    private static DelegateFixAction CreatePublicDnsFix() => new(
        "net.public-dns", "Fix_Network_PublicDns", ModuleId, RiskTier.Moderate,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "dns-settings", "powershell.exe",
            ["-NoProfile", "-Command", "Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceAlias,ServerAddresses | ConvertTo-Json -Compress"],
            "powershell.exe",
            ["-NoProfile", "-Command", "$old=Get-Content -Raw '{backup}'|ConvertFrom-Json; @($old)|ForEach-Object {if($_.ServerAddresses){Set-DnsClientServerAddress -InterfaceAlias $_.InterfaceAlias -ServerAddresses $_.ServerAddresses}else{Set-DnsClientServerAddress -InterfaceAlias $_.InterfaceAlias -ResetServerAddresses}}"],
            ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses @('1.1.1.1','8.8.8.8') -ErrorAction Stop}"])], ct),
        async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object {$_.ServerAddresses -contains '1.1.1.1'} | Select-Object InterfaceAlias", ct).ConfigureAwait(false);
            return ModuleHelpers.Array(root).Any();
        });

    private static DelegateFixAction CreateFirewallResetFix() => new(
        "net.firewall-reset", "Fix_Network_FirewallReset", ModuleId, RiskTier.Aggressive,
        async (context, ct) =>
        {
            var file = Path.Combine(ModuleHelpers.BackupDirectory(context), $"firewall-{Guid.NewGuid():N}.wfw");
            var result = await context.Commands.RunAsync("netsh.exe", ["advfirewall", "export", file], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            return result.Success && File.Exists(file)
                ? await context.Backups.CaptureExistingCommandStateAsync(
                    "Windows Firewall", file, "netsh.exe", ["advfirewall", "import", "{backup}"], ct).ConfigureAwait(false)
                : null;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("netsh.exe", ["advfirewall", "reset"])], ct),
        async (context, ct) => (await context.Commands.RunAsync("netsh.exe", ["advfirewall", "show", "allprofiles"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).Success);

    private static DelegateFixAction CreateTargetedDriverResetFix() => new(
        "net.driver-reset", "Fix_Network_DriverReset", ModuleId, RiskTier.Aggressive,
        async (context, ct) =>
        {
            var directory = ModuleHelpers.BackupDirectory(context);
            var state = await context.Backups.CaptureCommandStateAsync(
                "network-interface", "netsh.exe", ["-c", "interface", "dump"],
                "netsh.exe", ["-f", "{backup}"], directory, ct).ConfigureAwait(false);
            var driver = await context.Backups.CaptureDriverAsync(
                context.Parameters["net.driver-reset.target"], directory, ct).ConfigureAwait(false);
            return state is not null && driver is not null
                ? await context.Backups.CaptureBundleAsync("network-driver", [state, driver], directory, ct).ConfigureAwait(false)
                : null;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/delete-driver", context.Parameters["net.driver-reset.target"], "/uninstall", "/force"], TimeSpan.FromMinutes(10)), new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false)).Success,
        requiresReboot: true,
        requiresTarget: true);

    private static DelegateFixAction CreateTargetedGhostVpnFix() => new(
        "net.ghost-vpn-remove", "Fix_Network_RemoveGhostVpn", ModuleId, RiskTier.Aggressive,
        async (context, ct) =>
        {
            if (!context.Parameters.TryGetValue("net.ghost-vpn-remove.inf", out var inf)) return null;
            var directory = ModuleHelpers.BackupDirectory(context);
            var state = await context.Backups.CaptureCommandStateAsync(
                "network-interface", "netsh.exe", ["-c", "interface", "dump"],
                "netsh.exe", ["-f", "{backup}"], directory, ct).ConfigureAwait(false);
            var driver = await context.Backups.CaptureDriverAsync(inf, directory, ct).ConfigureAwait(false);
            return state is not null && driver is not null
                ? await context.Backups.CaptureBundleAsync("ghost-vpn", [state, driver], directory, ct).ConfigureAwait(false)
                : null;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/remove-device", context.Parameters["net.ghost-vpn-remove.target"]], TimeSpan.FromMinutes(5)), new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) =>
        {
            var target = context.Parameters["net.ghost-vpn-remove.target"];
            var result = await context.Commands.RunAsync("pnputil.exe", ["/enum-devices", "/instanceid", target], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            return !result.StdOut.Contains(target, StringComparison.OrdinalIgnoreCase);
        },
        requiresReboot: true,
        requiresTarget: true);

    private static DelegateFixAction CreateFullNetworkResetFix() => new(
        "net.full-reset", "Fix_Network_FullReset", ModuleId, RiskTier.Aggressive,
        async (context, ct) =>
        {
            var dir = ModuleHelpers.BackupDirectory(context);
            var entries = new List<BackupEntry?>
            {
                await context.Backups.CaptureCommandStateAsync("network-interface", "netsh.exe", ["-c", "interface", "dump"], "netsh.exe", ["-f", "{backup}"], dir, ct).ConfigureAwait(false),
                await context.Backups.CaptureRegistryAsync(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", dir, ct).ConfigureAwait(false),
                await CaptureHostsBackupAsync(context, ct).ConfigureAwait(false)
            };
            if (entries.Any(entry => entry is null)) return null;
            return await context.Backups.CaptureBundleAsync("full-network-reset", entries.OfType<BackupEntry>().ToArray(), dir, ct).ConfigureAwait(false);
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("netsh.exe", ["winsock", "reset"]), new CommandStep("netsh.exe", ["int", "ip", "reset"]), new CommandStep("netsh.exe", ["int", "ipv6", "reset"]), new CommandStep("ipconfig.exe", ["/flushdns"]), new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/enum-devices", "/class", "Net"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success,
        requiresReboot: true);

    private static DelegateFixAction CreateRouteResetFix() => new(
        "net.route-reset", "Fix_Network_RouteReset", ModuleId, RiskTier.Moderate,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "persistent-routes", "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; @(Get-NetRoute -PolicyStore PersistentStore -AddressFamily IPv4 | Select-Object DestinationPrefix,NextHop,InterfaceAlias,InterfaceIndex,RouteMetric) | ConvertTo-Json -Compress"],
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; $raw=Get-Content -Raw -LiteralPath '{backup}'; $routes=if([string]::IsNullOrWhiteSpace($raw)){@()}else{@($raw|ConvertFrom-Json)}; $current=@(Get-NetRoute -PolicyStore PersistentStore -AddressFamily IPv4 -ErrorAction SilentlyContinue); @($routes)|Where-Object {$null -ne $_}|ForEach-Object {$old=$_; $match=@($current|Where-Object {$_.DestinationPrefix -eq [string]$old.DestinationPrefix -and $_.NextHop -eq [string]$old.NextHop -and $_.InterfaceIndex -eq [uint32]$old.InterfaceIndex}); if($match.Count -gt 0){$match|Set-NetRoute -RouteMetric ([uint16]$old.RouteMetric) -Confirm:$false -ErrorAction Stop}else{$parameters=@{PolicyStore='PersistentStore';DestinationPrefix=[string]$old.DestinationPrefix;NextHop=[string]$old.NextHop;RouteMetric=[uint16]$old.RouteMetric;InterfaceIndex=[uint32]$old.InterfaceIndex;ErrorAction='Stop'}; New-NetRoute @parameters|Out-Null}}"],
            ModuleHelpers.BackupDirectory(context), ct),
        async (context, ct) =>
        {
            var routes = NetworkParsers.ParseRouteTargets(context.Parameters["net.route-reset.target"]);
            if (routes.Count == 0) return FixResult.Fail("FixResult_TargetRequired");
            foreach (var route in routes)
            {
                var result = await context.Commands.RunAsync(
                    "netsh.exe",
                    [
                        "interface", "ipv4", "delete", "route",
                        $"prefix={route.DestinationPrefix}",
                        $"interface={route.InterfaceIndex}",
                        $"nexthop={route.NextHop}",
                        "store=persistent"
                    ],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    return FixResult.Fail("FixResult_ApplyFailed", $"{result.StdOut}{Environment.NewLine}{result.StdErr}");
                }
            }
            return FixResult.Ok("FixResult_Applied");
        },
        async (context, ct) => await CheckRoutesAsync(new DiagnosticContext { Commands = context.Commands, Text = context.Text, Thresholds = new Thresholds(), SessionDirectory = context.Session.DirectoryPath }, ct).ConfigureAwait(false) is null,
        requiresTarget: true);

    private static async IAsyncEnumerable<TestProgress> RunPingTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        const string script = "Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address} | ForEach-Object {[pscustomobject]@{Alias=$_.InterfaceAlias;Source=$_.IPv4Address[0].IPAddress;Gateway=$_.IPv4DefaultGateway.NextHop}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var adapters = ModuleHelpers.Array(root).ToArray();
        var tests = adapters.SelectMany(adapter =>
        {
            var source = ModuleHelpers.GetString(adapter, "Source") ?? string.Empty;
            var gateway = GetArrayStrings(adapter, "Gateway").FirstOrDefault();
            return new[] { gateway, "1.1.1.1", "8.8.8.8" }
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Select(target => (Alias: ModuleHelpers.GetString(adapter, "Alias") ?? "?", Source: source, Target: target!));
        }).ToArray();

        if (tests.Length == 0)
        {
            yield return new TestProgress("net.live.ping", "TestStage_NotAvailable", 1, context.Text.Get("TestResult_NoActiveAdapter"));
            yield break;
        }

        for (var index = 0; index < tests.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var test = tests[index];
            yield return new TestProgress("net.live.ping", "TestStage_Running", (double)index / Math.Max(1, tests.Length), $"{test.Alias} to {test.Target}");
            var result = await context.Commands.RunAsync("ping.exe", ["-n", "10", "-S", test.Source, test.Target], TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            var stats = NetworkParsers.ParsePing(result.StdOut);
            yield return new TestProgress(
                "net.live.ping",
                "TestStage_Result",
                (double)(index + 1) / Math.Max(1, tests.Length),
                $"{test.Alias} to {test.Target}: {stats.LossPercent:0}% / {stats.AverageMs:0.0} ms / jitter {stats.JitterMs:0.0} ms",
                new Dictionary<string, double>
                {
                    ["Sent"] = stats.Sent,
                    ["Received"] = stats.Received,
                    ["LossPercent"] = stats.LossPercent,
                    ["MinMs"] = stats.MinimumMs,
                    ["AverageMs"] = stats.AverageMs,
                    ["MaxMs"] = stats.MaximumMs,
                    ["JitterMs"] = stats.JitterMs
                });
        }
    }

    private static async IAsyncEnumerable<TestProgress> RunDnsRaceAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            "Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object {$_.ServerAddresses} | Where-Object {$_} | Select-Object -Unique",
            ct).ConfigureAwait(false);
        var servers = ModuleHelpers.Array(root)
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Concat(["1.1.1.1", "8.8.8.8"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        for (var index = 0; index < servers.Length; index++)
        {
            var server = servers[index]!;
            var result = await context.Commands.RunAsync(
                "nslookup.exe",
                ["www.microsoft.com", server],
                TimeSpan.FromSeconds(12),
                ct).ConfigureAwait(false);
            yield return new TestProgress(
                "net.live.dns",
                result.Success ? "TestStage_Result" : "TestStage_Failed",
                (double)(index + 1) / Math.Max(1, servers.Length),
                $"{server}: {(result.Success ? $"{result.Duration.TotalMilliseconds:0} ms" : context.Text.Get("TestResult_Failed"))}",
                new Dictionary<string, double> { ["Milliseconds"] = result.Duration.TotalMilliseconds, ["Success"] = result.Success ? 1 : 0 });
        }
    }

    private static async IAsyncEnumerable<TestProgress> RunSpeedTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        const int bytes = 10 * 1024 * 1024;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        yield return new TestProgress("net.live.speed", "TestStage_Running", 0, "10 MB");

        using var response = await client.GetAsync(
            $"https://speed.cloudflare.com/__down?bytes={bytes}",
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        var lastReported = 0d;
        try
        {
            while (received < bytes)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes - received)),
                    ct).ConfigureAwait(false);
                if (read == 0) break;
                received += read;
                var progress = Math.Min(1, (double)received / bytes);
                if (progress - lastReported >= .1)
                {
                    lastReported = progress;
                    yield return new TestProgress("net.live.speed", "TestStage_Running", progress, $"{received / 1024d / 1024d:0.0} MB");
                }
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
        }

        stopwatch.Stop();
        var finalMbps = received * 8d / Math.Max(.001, stopwatch.Elapsed.TotalSeconds) / 1_000_000d;
        var complete = received == bytes;
        var detail = complete
            ? $"{finalMbps:0.0} Mbps"
            : context.Text.Get("TestResult_IncompleteDownload", received / 1024d / 1024d);
        yield return new TestProgress("net.live.speed", complete ? "TestStage_Result" : "TestStage_Failed", 1, detail,
            new Dictionary<string, double> { ["Mbps"] = finalMbps, ["Bytes"] = received });
    }

    private static async IAsyncEnumerable<TestProgress> RunMtuTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        const int minimumPayloadBytes = 548;
        const int maximumPayloadBytes = 1_472;
        const int protocolOverheadBytes = 28;
        const int maximumAdapters = 4;
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            "Get-NetIPConfiguration | Where-Object {$_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address} | ForEach-Object {[pscustomobject]@{Alias=$_.InterfaceAlias;Source=$_.IPv4Address[0].IPAddress}}",
            ct).ConfigureAwait(false);
        var adapters = ModuleHelpers.Array(root)
            .Select(row => (
                Alias: ModuleHelpers.GetString(row, "Alias") ?? "?",
                Source: ModuleHelpers.GetString(row, "Source") ?? string.Empty))
            .Where(adapter => !string.IsNullOrWhiteSpace(adapter.Source))
            .Take(maximumAdapters)
            .ToArray();

        if (adapters.Length == 0)
        {
            yield return new TestProgress(
                "net.live.mtu",
                "TestStage_NotAvailable",
                1,
                context.Text.Get("TestResult_NoActiveAdapter"));
            yield break;
        }

        for (var adapterIndex = 0; adapterIndex < adapters.Length; adapterIndex++)
        {
            var adapter = adapters[adapterIndex];
            var baseline = await context.Commands.RunAsync(
                "ping.exe",
                ["-n", "1", "-w", "2500", "-f", "-l", minimumPayloadBytes.ToString(), "-S", adapter.Source, "1.1.1.1"],
                TimeSpan.FromSeconds(4),
                ct).ConfigureAwait(false);
            if (!baseline.Success)
            {
                yield return new TestProgress(
                    "net.live.mtu",
                    "TestStage_NotAvailable",
                    (adapterIndex + 1d) / adapters.Length,
                    context.Text.Get("TestResult_MtuUnavailable", adapter.Alias),
                    new Dictionary<string, double>
                    {
                        ["MtuBytes"] = 0,
                        ["PayloadBytes"] = 0,
                        ["Success"] = 0
                    });
                continue;
            }

            var lower = minimumPayloadBytes;
            var upper = maximumPayloadBytes;
            var round = 0;
            const int maximumRounds = 10;
            while (lower < upper && round < maximumRounds)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = lower + (upper - lower + 1) / 2;
                var result = await context.Commands.RunAsync(
                    "ping.exe",
                    ["-n", "1", "-w", "2500", "-f", "-l", candidate.ToString(), "-S", adapter.Source, "1.1.1.1"],
                    TimeSpan.FromSeconds(4),
                    ct).ConfigureAwait(false);
                if (result.Success) lower = candidate;
                else upper = candidate - 1;
                round++;

                var adapterProgress = round / (double)maximumRounds;
                var overallProgress = (adapterIndex + adapterProgress) / adapters.Length;
                yield return new TestProgress(
                    "net.live.mtu",
                    "TestStage_Running",
                    overallProgress,
                    context.Text.Get("TestResult_MtuProbing", adapter.Alias, candidate + protocolOverheadBytes));
            }

            var mtu = lower + protocolOverheadBytes;
            yield return new TestProgress(
                "net.live.mtu",
                "TestStage_Result",
                (adapterIndex + 1d) / adapters.Length,
                context.Text.Get("TestResult_Mtu", adapter.Alias, mtu),
                new Dictionary<string, double>
                {
                    ["MtuBytes"] = mtu,
                    ["PayloadBytes"] = lower,
                    ["Success"] = 1
                });
        }
    }

    private static async IAsyncEnumerable<TestProgress> RunHttpTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TestProgress("net.live.http", "TestStage_Running", .15, "www.msftconnecttest.com");

        HttpProbeResult? probe = null;
        try
        {
            probe = await ProbeHttpAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The failure is represented as a bounded localized result below.
        }

        if (probe is null)
        {
            yield return new TestProgress(
                "net.live.http",
                "TestStage_Failed",
                1,
                context.Text.Get("TestResult_Failed"),
                new Dictionary<string, double>
                {
                    ["Milliseconds"] = 0,
                    ["StatusCode"] = 0,
                    ["Bytes"] = 0,
                    ["Expected"] = 0
                });
            yield break;
        }

        var stage = probe.ExpectedContent ? "TestStage_Result" : "TestStage_CaptivePortal";
        yield return new TestProgress(
            "net.live.http",
            stage,
            1,
            context.Text.Get("TestResult_HttpStatus", probe.StatusCode),
            new Dictionary<string, double>
            {
                ["Milliseconds"] = probe.Milliseconds,
                ["StatusCode"] = probe.StatusCode,
                ["Bytes"] = probe.Bytes,
                ["Expected"] = probe.ExpectedContent ? 1 : 0
            });
    }

    private static async Task<HttpProbeResult> ProbeHttpAsync(CancellationToken ct)
    {
        const int maximumResponseBytes = 64 * 1024;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://www.msftconnecttest.com/connecttest.txt");
        request.Headers.UserAgent.ParseAdd("Microsoft NCSI");
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body = new MemoryStream(capacity: 256);
        var buffer = new byte[4 * 1024];
        var oversized = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (body.Length + read > maximumResponseBytes)
                {
                    oversized = true;
                    break;
                }
                body.Write(buffer, 0, read);
            }
            stopwatch.Stop();

            var expected = response.IsSuccessStatusCode && !oversized && IsExpectedNcsiBody(body);
            return new HttpProbeResult(
                (int)response.StatusCode,
                stopwatch.Elapsed.TotalMilliseconds,
                body.Length,
                expected);
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (body.TryGetBuffer(out var captured) && captured.Array is not null)
            {
                Array.Clear(captured.Array, captured.Offset, (int)body.Length);
            }
        }
    }

    private static bool IsExpectedNcsiBody(MemoryStream body)
    {
        if (!body.TryGetBuffer(out var captured) || captured.Array is null) return false;
        var start = captured.Offset;
        var end = checked(captured.Offset + (int)body.Length);
        while (start < end && IsAsciiWhitespace(captured.Array[start])) start++;
        while (end > start && IsAsciiWhitespace(captured.Array[end - 1])) end--;
        if (end - start != ExpectedNcsiContent.Length) return false;
        for (var index = 0; index < ExpectedNcsiContent.Length; index++)
        {
            if (captured.Array[start + index] != ExpectedNcsiContent[index]) return false;
        }
        return true;
    }

    private static bool IsAsciiWhitespace(byte value) => value is 0x20 or 0x09 or 0x0D or 0x0A;

    private static List<string> ExtractThirdPartyLspEntries(string catalog)
    {
        var results = new List<string>();
        foreach (var rawLine in catalog.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || results.Count >= 40) continue;
            var lower = line.ToLowerInvariant();
            if (!lower.Contains("provider") && !lower.Contains("dll") && !lower.Contains("layered"))
            {
                continue;
            }

            if (lower.Contains("msafd") || lower.Contains("tcpip") || lower.Contains("afd ") ||
                lower.Contains("microsoft") || lower.Contains("windows\\system32") ||
                lower.Contains("winsock") && lower.Contains("microsoft"))
            {
                continue;
            }

            if (lower.Contains(".dll") || lower.Contains("provider"))
            {
                results.Add(line);
            }
        }

        return results;
    }

    private static bool TryParseLinkSpeedMbps(string text, out double mbps)
    {
        mbps = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().ToLowerInvariant().Replace(',', '.');
        var number = new string(normalized.TakeWhile(character =>
            char.IsDigit(character) || character is '.' or 'e' or '+' or '-').ToArray());
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value <= 0)
        {
            return false;
        }

        if (normalized.Contains("gbps") || normalized.Contains("gbit"))
        {
            mbps = value * 1000d;
            return true;
        }

        if (normalized.Contains("kbps") || normalized.Contains("kbit"))
        {
            mbps = value / 1000d;
            return true;
        }

        mbps = value;
        return true;
    }

    private static bool IsTrue(JsonElement element, string property) =>
        ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed));

    private static bool IsStatus(JsonElement element, string expected) =>
        string.Equals(ModuleHelpers.GetString(element, "Status") ?? ModuleHelpers.GetString(element, "State"), expected, StringComparison.OrdinalIgnoreCase);

    private static int GetInt(JsonElement element, string property) =>
        ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value) &&
        (value.TryGetInt32(out var number) || int.TryParse(value.ToString(), out number))
            ? number
            : 0;

    private sealed record HttpProbeResult(
        int StatusCode,
        double Milliseconds,
        long Bytes,
        bool ExpectedContent);

    private static IEnumerable<string> GetArrayStrings(JsonElement element, string property)
    {
        if (!ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item));
        }

        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? Enumerable.Empty<string>()
            : new[] { value.ToString() };
    }

    private static string DescribeAdapters(IEnumerable<JsonElement> adapters) =>
        string.Join(Environment.NewLine, adapters.Select(adapter =>
            $"{ModuleHelpers.GetString(adapter, "Name")}: {ModuleHelpers.GetString(adapter, "Status")}, {ModuleHelpers.GetString(adapter, "LinkSpeed")}, {ModuleHelpers.GetString(adapter, "DriverVersion")}"));

    private static string DescribeRows(IEnumerable<JsonElement> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => row.ToString()));

    private static void AttachDriverTarget(Finding finding, IEnumerable<JsonElement> rows)
    {
        var inf = rows.Select(row => ModuleHelpers.GetString(row, "InfName")).FirstOrDefault(IsPublishedInf);
        if (!string.IsNullOrWhiteSpace(inf)) finding.RepairParameters["net.driver-reset.target"] = inf;
    }

    private static bool IsPublishedInf(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length > 7 && value.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
        value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) &&
        value[3..^4].All(char.IsDigit);

    private static async Task<BackupEntry?> CaptureHostsBackupAsync(FixContext context, CancellationToken ct)
    {
        var path = HostsPath();
        if (File.Exists(path))
        {
            return await context.Backups.CaptureFileAsync(
                path, ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
        }

        var marker = await context.Backups.CaptureValueAsync(
            "hosts-file-absent", new { Path = path }, ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
        if (marker is not null)
        {
            var literal = path.Replace("'", "''", StringComparison.Ordinal);
            marker.Metadata["restoreExecutable"] = "powershell.exe";
            marker.Metadata["restoreArguments"] = JsonSerializer.Serialize(new[]
            {
                "-NoProfile", "-NonInteractive", "-Command",
                $"if(Test-Path -LiteralPath '{literal}'){{Remove-Item -LiteralPath '{literal}' -Force -ErrorAction Stop}}"
            });
        }
        return marker;
    }

    private static string HostsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
}

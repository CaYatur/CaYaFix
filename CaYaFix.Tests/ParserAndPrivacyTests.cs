// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Core;
using CaYaFix.Modules.Network;

namespace CaYaFix.Tests;

public sealed class ParserAndPrivacyTests
{
    [Theory]
    [InlineData("Fixtures/ping-en.txt", 4, 4, 0, 15)]
    [InlineData("Fixtures/ping-tr.txt", 4, 3, 25, 19)]
    [InlineData("Fixtures/ping-de.txt", 4, 3, 25, 13)]
    public void ParsesLocalizedPingOutput(string fixture, int sent, int received, double loss, double average)
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fixture));
        var result = NetworkParsers.ParsePing(output);

        Assert.Equal(sent, result.Sent);
        Assert.Equal(received, result.Received);
        Assert.Equal(loss, result.LossPercent);
        Assert.Equal(average, result.AverageMs);
    }

    [Theory]
    [InlineData("Packets: Sent = 10, Received = 0, Lost = 10 (100% loss)", 10, 0, 100)]
    [InlineData("Pakete: Gesendet = 4, Empfangen = 4, Verloren = 0 (0% Verlust)", 4, 4, 0)]
    public void ParsesFullyLostAndUnsupportedLanguageSummaries(
        string output,
        int sent,
        int received,
        double loss)
    {
        var result = NetworkParsers.ParsePing(output);

        Assert.Equal(sent, result.Sent);
        Assert.Equal(received, result.Received);
        Assert.Equal(loss, result.LossPercent);
        Assert.Equal(0, result.AverageMs);
        Assert.Equal(0, result.JitterMs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a ping result")]
    [InlineData("Packets: Sent = 999999999999999999999, Received = 1, Lost = 0 (0% loss)")]
    [InlineData("Reply from 203.0.113.1: time=999999999999999999999ms TTL=64")]
    [InlineData("Packets: Sent = 2, Received = 9, Lost = 0 (500% loss)")]
    public void MalformedOrOverflowingPingOutputReturnsBoundedStatistics(string output)
    {
        var result = NetworkParsers.ParsePing(output);

        Assert.InRange(result.Sent, 0, 100_000);
        Assert.InRange(result.Received, 0, result.Sent);
        Assert.InRange(result.LossPercent, 0, 100);
        Assert.InRange(result.MinimumMs, 0, 3_600_000);
        Assert.InRange(result.AverageMs, 0, 3_600_000);
        Assert.InRange(result.MaximumMs, 0, 3_600_000);
        Assert.InRange(result.JitterMs, 0, 3_600_000);
        Assert.True(double.IsFinite(result.LossPercent));
        Assert.True(double.IsFinite(result.AverageMs));
    }

    [Fact]
    public void PersistentRouteTargetsRoundTripWithoutDuplicates()
    {
        var encoded = NetworkParsers.EncodeRouteTargets(
        [
            new PersistentRouteTarget("0.0.0.0/0", "192.168.1.1", 12),
            new PersistentRouteTarget("10.20.0.0/16", "10.0.0.1", 7),
            new PersistentRouteTarget("0.0.0.0/0", "192.168.1.1", 12)
        ]);

        var parsed = NetworkParsers.ParseRouteTargets(encoded);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(new PersistentRouteTarget("0.0.0.0/0", "192.168.1.1", 12), parsed[0]);
        Assert.Equal(new PersistentRouteTarget("10.20.0.0/16", "10.0.0.1", 7), parsed[1]);
    }

    [Theory]
    [InlineData("0.0.0.0/0|192.168.1.1|0")]
    [InlineData("0.0.0.0/99|192.168.1.1|4")]
    [InlineData("0.0.0.0/0|192.168.1.1;Remove-Item|4")]
    [InlineData("0.0.0.0/0|not-an-address|4")]
    [InlineData("0.0.0.0/0|192.168.1.1|4|extra")]
    public void PersistentRouteTargetsRejectMalformedOrInjectableValues(string value)
    {
        Assert.Empty(NetworkParsers.ParseRouteTargets(value));
    }

    [Fact]
    public void PersistentRouteTargetCountIsBounded()
    {
        var tooMany = string.Join(';', Enumerable.Range(1, 65).Select(index =>
            $"10.{index}.0.0/16|192.168.1.1|{index}"));

        Assert.Empty(NetworkParsers.ParseRouteTargets(tooMany));
        Assert.Empty(NetworkParsers.EncodeRouteTargets(Enumerable.Range(1, 65).Select(index =>
            new PersistentRouteTarget($"10.{index}.0.0/16", "192.168.1.1", index))));
    }

    [Fact]
    public void RedactsPersonalNetworkDataDeterministically()
    {
        var source = "Host Name : HOME-PC\nSSID : FamilyNetwork\nKey Content : SuperSecretWifiKey\nName: Alex's Headset\nEmail: alex@example.test\nOwner: S-1-5-21-1000-1001-1002-1003\nContainer: 00112233-4455-6677-8899-aabbccddeeff\nPhysical Address: AA-BB-CC-DD-EE-FF\nIPv4 Address: 192.168.1.42\nPublic: 8.8.8.8\nIPv6: 2001:db8:85a3::8a2e:370:7334\nDeviceID: HDAUDIO\\FUNC_01&VEN_1234\n{\"InstanceId\":\"USB\\VID_1234&PID_5678\\ABC123\",\"token\":\"private-token-value\"}\nC:\\Users\\Alex\\Desktop";

        var redacted = PrivacyRedactor.Redact(source);

        Assert.DoesNotContain("FamilyNetwork", redacted);
        Assert.DoesNotContain("AA-BB-CC-DD-EE-FF", redacted);
        Assert.DoesNotContain("192.168.1.42", redacted);
        Assert.DoesNotContain("8.8.8.8", redacted);
        Assert.DoesNotContain("2001:db8", redacted);
        Assert.DoesNotContain("HDAUDIO", redacted);
        Assert.DoesNotContain("VID_1234", redacted);
        Assert.DoesNotContain("Alex", redacted);
        Assert.DoesNotContain("SuperSecretWifiKey", redacted);
        Assert.DoesNotContain("private-token-value", redacted);
        Assert.DoesNotContain("Alex's Headset", redacted);
        Assert.DoesNotContain("alex@example.test", redacted);
        Assert.DoesNotContain("S-1-5-21-1000", redacted);
        Assert.DoesNotContain("00112233-4455", redacted);
        Assert.Contains("<mac-address>", redacted);
        Assert.Contains("192.168.x.x", redacted);
        Assert.Contains("<public-ip>", redacted);
        Assert.Contains("<ipv6-address>", redacted);
        Assert.Contains("<email-address>", redacted);
        Assert.Contains("<windows-sid>", redacted);
        Assert.Contains("<identifier>", redacted);
    }

    [Fact]
    public void RedactionFailsClosedForLargeRepeatedSensitiveInput()
    {
        const string marker = "CaYaFix-Private-Value-7F3A91";
        var source = string.Join(
            Environment.NewLine,
            Enumerable.Repeat($"SSID: {marker}", 20_000));

        var redacted = PrivacyRedactor.Redact(source);

        Assert.DoesNotContain(marker, redacted, StringComparison.Ordinal);
        Assert.True(
            redacted.Contains("<redacted-", StringComparison.Ordinal) ||
            redacted.Contains("content omitted", StringComparison.Ordinal));
    }
}

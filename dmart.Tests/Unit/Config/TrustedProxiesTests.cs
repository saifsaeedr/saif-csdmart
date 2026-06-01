using System.Net;
using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Pins DmartSettings.ParseTrustedProxies — the trust list that decides whose
// X-Forwarded-For the per-IP rate limiter believes. Getting this wrong means
// either every client behind nginx shares one bucket (no limiting) or an
// attacker can spoof their client IP, so the parsing is asserted directly.
public class TrustedProxiesTests
{
    [Fact]
    public void Empty_Trusts_Nothing_Extra()
    {
        var (proxies, networks) = new DmartSettings { TrustedProxies = "" }.ParseTrustedProxies();
        proxies.ShouldBeEmpty();
        networks.ShouldBeEmpty();
    }

    [Fact]
    public void Parses_Ips_And_Cidr_Networks()
    {
        var (proxies, networks) = new DmartSettings
        {
            TrustedProxies = "10.0.0.5, 192.168.1.1 ,10.0.0.0/24, ::1",
        }.ParseTrustedProxies();

        proxies.Select(p => p.ToString()).ShouldBe(new[] { "10.0.0.5", "192.168.1.1", "::1" });
        networks.Count.ShouldBe(1);
        networks[0].BaseAddress.ToString().ShouldBe("10.0.0.0");
        networks[0].PrefixLength.ShouldBe(24);
    }

    [Fact]
    public void Drops_Garbage_And_OutOfRange_Rather_Than_Trusting_It()
    {
        var (proxies, networks) = new DmartSettings
        {
            // not-an-ip → dropped; 10.0.0.0/999 → invalid prefix; 10.0.0.5/24 →
            // non-canonical (host bits set) → dropped; only 1.2.3.4 survives.
            TrustedProxies = "not-an-ip, 1.2.3.4, 10.0.0.0/999, 10.0.0.5/24",
        }.ParseTrustedProxies();

        proxies.Select(p => p.ToString()).ShouldBe(new[] { "1.2.3.4" });
        networks.ShouldBeEmpty();
    }
}

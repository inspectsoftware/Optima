using System.Net;
using Optima.Core.Network;
using Xunit;

namespace Optima.Tests.Network;

public sealed class EndpointFilterTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("34.120.10.5", true)]
    [InlineData("100.20.1.1", true)]      // public, below the 100.64/10 CGNAT range
    [InlineData("10.0.0.5", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]      // just outside 172.16/12
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]     // carrier NAT
    [InlineData("224.0.0.1", false)]      // multicast
    [InlineData("255.255.255.255", false)]
    [InlineData("0.0.0.0", false)]
    public void FiltersIPv4Ranges(string address, bool expected)
        => Assert.Equal(expected, EndpointFilter.IsPublicUnicast(IPAddress.Parse(address)));

    [Fact]
    public void IPv6IsExcluded()
        => Assert.False(EndpointFilter.IsPublicUnicast(IPAddress.Parse("2606:4700:4700::1111")));
}

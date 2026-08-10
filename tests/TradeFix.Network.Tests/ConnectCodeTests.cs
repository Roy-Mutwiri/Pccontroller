using TradeFix.Network.Auth;

namespace TradeFix.Network.Tests;

public class ConnectCodeTests
{
    [Fact]
    public void Format_ThenParse_RoundTrips()
    {
        var formatted = ConnectCode.Format("TRADE-8391", "100.116.30.51", 8791);
        Assert.Equal("TRADE-8391@100.116.30.51:8791", formatted);

        var ok = ConnectCode.TryParse(formatted, out var code, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("TRADE-8391", code);
        Assert.Equal("100.116.30.51", host);
        Assert.Equal(8791, port);
    }

    [Fact]
    public void TryParse_TrimsWhitespace()
    {
        var ok = ConnectCode.TryParse("  TRADE-1234@localhost:8791  ", out var code, out var host, out var port);

        Assert.True(ok);
        Assert.Equal("TRADE-1234", code);
        Assert.Equal("localhost", host);
        Assert.Equal(8791, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-connect-code")]
    [InlineData("TRADE-1234@")]
    [InlineData("TRADE-1234@hostwithoutport")]
    [InlineData("TRADE-1234@host:notanumber")]
    [InlineData("@host:8791")]
    public void TryParse_RejectsMalformedInput(string input)
    {
        var ok = ConnectCode.TryParse(input, out _, out _, out _);
        Assert.False(ok);
    }
}

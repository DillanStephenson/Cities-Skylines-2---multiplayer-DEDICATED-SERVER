using Multiplayer.Core.Transport;
using Xunit;

namespace Multiplayer.Tests
{
    public class EndpointParserTests
    {
        [Theory]
        [InlineData("example.com", "example.com", 27015)]
        [InlineData("example.com:1234", "example.com", 1234)]
        [InlineData(" 192.168.1.10:5000 ", "192.168.1.10", 5000)]
        [InlineData("[::1]:6000", "::1", 6000)]
        [InlineData("[fe80::1]", "fe80::1", 27015)]
        [InlineData("fe80::1", "fe80::1", 27015)]
        public void ParsesHostAndPort(string input, string expectedHost, int expectedPort)
        {
            Assert.True(EndpointParser.TryParse(input, 27015, out string host, out int port));
            Assert.Equal(expectedHost, host);
            Assert.Equal(expectedPort, port);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("host:notaport")]
        [InlineData("host:70000")]
        [InlineData("host:0")]
        [InlineData(":1234")]
        [InlineData("[::1")]
        public void RejectsBadInput(string input)
        {
            Assert.False(EndpointParser.TryParse(input, 27015, out _, out _));
        }

        [Fact]
        public void PortParsing_IsStrict()
        {
            Assert.True(EndpointParser.TryParsePort("27015", out int port));
            Assert.Equal(27015, port);
            Assert.False(EndpointParser.TryParsePort("-1", out _));
            Assert.False(EndpointParser.TryParsePort("65536", out _));
            Assert.False(EndpointParser.TryParsePort("12a", out _));
        }
    }
}

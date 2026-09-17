using System.Collections.Generic;
using Multiplayer.Core.Protocol;
using Xunit;

namespace Multiplayer.Tests
{
    public class CodecTests
    {
        [Fact]
        public void HandshakeRequest_RoundTrips()
        {
            var original = new HandshakeRequestMessage { ProtocolVersion = 7, ModVersion = "9.9.9", GameVersion = "1.6.2f1", PlayerName = "Ada", Password = "hunter2", OwnerKey = "k3y" };
            var decoded = (HandshakeRequestMessage)MessageCodec.Decode(MessageCodec.Encode(original));

            Assert.Equal(7, decoded.ProtocolVersion);
            Assert.Equal("9.9.9", decoded.ModVersion);
            Assert.Equal("1.6.2f1", decoded.GameVersion);
            Assert.Equal("Ada", decoded.PlayerName);
            Assert.Equal("hunter2", decoded.Password);
            Assert.Equal("k3y", decoded.OwnerKey);
        }

        [Fact]
        public void HandshakeResponse_RoundTrips()
        {
            var original = new HandshakeResponseMessage { Accepted = true, Reason = "", PlayerId = 3, IsOwner = true, ServerName = "Grace" };
            var decoded = (HandshakeResponseMessage)MessageCodec.Decode(MessageCodec.Encode(original));

            Assert.True(decoded.Accepted);
            Assert.Equal(3, decoded.PlayerId);
            Assert.True(decoded.IsOwner);
            Assert.Equal("Grace", decoded.ServerName);
        }

        [Fact]
        public void PlayerList_RoundTrips()
        {
            var original = new PlayerListMessage
            {
                Players = new List<PlayerListEntry>
                {
                    new PlayerListEntry { PlayerId = 1, Name = "Host", IsOwner = true },
                    new PlayerListEntry { PlayerId = 2, Name = "Guest", IsOwner = false },
                },
            };
            var decoded = (PlayerListMessage)MessageCodec.Decode(MessageCodec.Encode(original));

            Assert.Equal(2, decoded.Players.Count);
            Assert.Equal("Guest", decoded.Players[1].Name);
            Assert.True(decoded.Players[0].IsOwner);
        }

        [Fact]
        public void GameplayCommand_RoundTrips_WithPayload()
        {
            var payload = new byte[1000];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)i;
            }

            var original = new GameplayCommandMessage { Kind = "net.place", OriginPlayerId = 2, Payload = payload };
            var decoded = (GameplayCommandMessage)MessageCodec.Decode(MessageCodec.Encode(original));

            Assert.Equal("net.place", decoded.Kind);
            Assert.Equal(2, decoded.OriginPlayerId);
            Assert.Equal(payload, decoded.Payload);
        }

        [Fact]
        public void SimulationSpeed_Chat_Disconnect_Heartbeat_RoundTrip()
        {
            Assert.Equal(2.5f, ((SimulationSpeedMessage)MessageCodec.Decode(MessageCodec.Encode(new SimulationSpeedMessage { Speed = 2.5f }))).Speed);

            var chat = (ChatMessage)MessageCodec.Decode(MessageCodec.Encode(new ChatMessage { PlayerId = 4, PlayerName = "Zed", Text = "hello" }));
            Assert.Equal(4, chat.PlayerId);
            Assert.Equal("Zed", chat.PlayerName);
            Assert.Equal("hello", chat.Text);

            Assert.Equal("bye", ((DisconnectMessage)MessageCodec.Decode(MessageCodec.Encode(new DisconnectMessage { Reason = "bye" }))).Reason);
            Assert.IsType<HeartbeatMessage>(MessageCodec.Decode(MessageCodec.Encode(new HeartbeatMessage())));

            var control = (ServerControlMessage)MessageCodec.Decode(MessageCodec.Encode(new ServerControlMessage { Action = ServerControlAction.Stop, Text = "closing" }));
            Assert.Equal(ServerControlAction.Stop, control.Action);
            Assert.Equal("closing", control.Text);
        }

        [Fact]
        public void UnknownType_Throws()
        {
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(new byte[] { 0xFF, 0xFF, 0, 0 }));
        }

        [Fact]
        public void Truncated_Throws()
        {
            byte[] full = MessageCodec.Encode(new ChatMessage { PlayerId = 1, PlayerName = "A", Text = "a long enough message" });
            var truncated = new byte[full.Length - 5];
            System.Array.Copy(full, truncated, truncated.Length);

            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(truncated));
            Assert.Throws<ProtocolException>(() => MessageCodec.Decode(new byte[1]));
        }
    }
}

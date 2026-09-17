using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>Player positions on the wire, the change threshold, and who counts as the leader that auto-saves.</summary>
    public class PresenceTests
    {
        [Fact]
        public void RoundTrips()
        {
            var command = new PresenceCommand { Pivot = new Vec3(100, 50, 200), Yaw = 135f, Zoom = 800f, HasCursor = true, Cursor = new Vec3(120, 51, 210) };
            PresenceCommand copy = PresenceCommand.FromBytes(command.ToBytes());
            Assert.Equal(100f, copy.Pivot.X);
            Assert.Equal(200f, copy.Pivot.Z);
            Assert.Equal(135f, copy.Yaw);
            Assert.Equal(800f, copy.Zoom);
            Assert.True(copy.HasCursor);
            Assert.Equal(210f, copy.Cursor.Z);
        }

        [Fact]
        public void OnlyRealMovementCounts()
        {
            var a = new PresenceCommand { Pivot = new Vec3(0, 0, 0), Yaw = 10f, Zoom = 500f };
            var nudge = new PresenceCommand { Pivot = new Vec3(1, 0, 1), Yaw = 11f, Zoom = 510f };
            var move = new PresenceCommand { Pivot = new Vec3(10, 0, 0), Yaw = 10f, Zoom = 500f };
            var turn = new PresenceCommand { Pivot = new Vec3(0, 0, 0), Yaw = 40f, Zoom = 500f };
            var cursor = new PresenceCommand { Pivot = new Vec3(0, 0, 0), Yaw = 10f, Zoom = 500f, HasCursor = true, Cursor = new Vec3(5, 0, 5) };

            Assert.True(a.DiffersFrom(null, 3f));
            Assert.False(nudge.DiffersFrom(a, 3f));
            Assert.True(move.DiffersFrom(a, 3f));
            Assert.True(turn.DiffersFrom(a, 3f));
            Assert.True(cursor.DiffersFrom(a, 3f));
        }

        [Fact]
        public void Server_RelaysPresenceToTheOthersOnly()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientSession a = loop.StartOwner("Ada");
            ClientSession b = loop.StartClient("Bob");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected && b.State == SessionState.Connected));

            int aGot = 0, bGot = 0;
            a.GameplayCommandReceived += m => { if (m.Kind == PresenceCommand.Kind) aGot++; };
            b.GameplayCommandReceived += m => { if (m.Kind == PresenceCommand.Kind) bGot++; };
            a.SendGameplayCommand(PresenceCommand.Kind, new PresenceCommand { Pivot = new Vec3(1, 2, 3) }.ToBytes());
            Assert.True(loop.PumpUntil(() => bGot == 1));
            loop.PumpFor(100);
            Assert.Equal(0, aGot);
        }

        [Fact]
        public void Leader_IsTheOwner_OrElseTheLongestConnectedPlayer()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientSession first = loop.StartClient("First");
            Assert.True(loop.PumpUntil(() => first.State == SessionState.Connected), first.LastError);
            ClientSession second = loop.StartClient("Second");
            Assert.True(loop.PumpUntil(() => second.State == SessionState.Connected && second.Players.Count == 2), second.LastError);
            Assert.True(loop.PumpUntil(() => first.Players.Count == 2));

            Assert.True(first.IsLeader);
            Assert.False(second.IsLeader);

            ClientSession owner = loop.StartOwner("Host");
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected && first.Players.Count == 3), owner.LastError);
            Assert.True(owner.IsLeader);
            Assert.False(first.IsLeader);
            Assert.False(second.IsLeader);
        }

        [Fact]
        public void AnyPlayer_MayUploadTheWorld()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientSession guest = loop.StartClient("Guest");
            Assert.True(loop.PumpUntil(() => guest.State == SessionState.Connected && guest.ServerWorldKnown), guest.LastError);

            bool? accepted = null;
            guest.WorldUploadFinished += (ok, revision, reason) => accepted = ok;
            Assert.True(guest.UploadWorld("Save", "City", "guid", new byte[] { 1, 2, 3, 4 }));
            Assert.True(loop.PumpUntil(() => accepted.HasValue));
            Assert.True(accepted.Value);
            Assert.NotNull(loop.Server.World);
            Assert.Equal("Guest", loop.Server.World.Info.UploaderName);
        }
    }
}

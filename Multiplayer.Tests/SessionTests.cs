using System.Collections.Generic;
using System.Linq;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Session;
using Multiplayer.Core.Transport;
using Xunit;

namespace Multiplayer.Tests
{
    public class SessionTests
    {
        [Fact]
        public void OwnerAndClient_Handshake_PopulatesRosterEverywhere()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            var joined = new List<PlayerInfo>();
            server.PlayerJoined += joined.Add;

            ClientSession owner = loop.StartOwner();
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);

            ClientSession client = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Connected), client.LastError);
            Assert.True(loop.PumpUntil(() => client.Players.Count == 2 && owner.Players.Count == 2));

            Assert.True(owner.IsOwner);
            Assert.False(client.IsOwner);
            Assert.Equal(1, owner.LocalPlayerId);
            Assert.Equal(2, client.LocalPlayerId);
            Assert.Equal(1, server.OwnerPlayerId);
            Assert.Equal("TestServer", client.ServerName);
            Assert.Equal(new[] { "HostPlayer", "Ada" }, joined.Select(p => p.Name).ToArray());

            Assert.Equal(new[] { "HostPlayer", "Ada" }, server.Players.Select(p => p.Name).ToArray());
            Assert.Equal(new[] { "HostPlayer", "Ada" }, client.Players.Select(p => p.Name).ToArray());
            Assert.True(client.Players[0].IsOwner);
            Assert.False(client.Players[1].IsOwner);
        }

        [Fact]
        public void WrongPassword_IsRejectedWithReason()
        {
            using var loop = new Loopback();
            loop.StartServer(Loopback.FastServerConfig("secret"));
            ClientSession client = loop.StartClient("Ada", "nope");

            Assert.True(loop.PumpUntil(() => client.State == SessionState.Failed));
            Assert.Contains("Wrong password", client.LastError);
            Assert.Empty(loop.Server.Players);
        }

        [Fact]
        public void CorrectPassword_IsAccepted()
        {
            using var loop = new Loopback();
            loop.StartServer(Loopback.FastServerConfig("secret"));
            ClientSession client = loop.StartClient("Ada", "secret");

            Assert.True(loop.PumpUntil(() => client.State == SessionState.Connected), client.LastError);
        }

        [Fact]
        public void WrongOwnerKey_IsRejected_AndSecondOwnerIsRejected()
        {
            using var loop = new Loopback();
            loop.StartServer();

            ClientSession impostor = loop.StartClient("Impostor", "", "not-the-key");
            Assert.True(loop.PumpUntil(() => impostor.State == SessionState.Failed));
            Assert.Contains("Wrong owner key", impostor.LastError);

            ClientSession owner = loop.StartOwner();
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);

            ClientSession second = loop.StartOwner("SecondOwner");
            Assert.True(loop.PumpUntil(() => second.State == SessionState.Failed));
            Assert.Contains("already connected", second.LastError);

            // Once the owner leaves, the key works again.
            owner.Leave("bye");
            Assert.True(loop.PumpUntil(() => loop.Server.OwnerPlayerId == 0));
            ClientSession again = loop.StartOwner("Returning");
            Assert.True(loop.PumpUntil(() => again.State == SessionState.Connected), again.LastError);
            Assert.True(again.IsOwner);
        }

        [Fact]
        public void ServerWithoutOwnerKey_RejectsAnyKey()
        {
            using var loop = new Loopback();
            ServerConfig config = Loopback.FastServerConfig();
            config.OwnerKey = "";
            loop.StartServer(config);

            ClientSession client = loop.StartClient("Ada", "", "anything");
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Failed));
            Assert.Contains("Wrong owner key", client.LastError);
        }

        [Fact]
        public void ModVersionMismatch_IsRejected()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientConfig config = Loopback.FastClientConfig("Old");
            config.ModVersion = "0.0.1";
            ClientSession client = loop.StartClient(config);

            Assert.True(loop.PumpUntil(() => client.State == SessionState.Failed));
            Assert.Contains("Mod version mismatch", client.LastError);
        }

        [Fact]
        public void GameVersion_MustMatchWhenServerHasOne()
        {
            using var loop = new Loopback();
            ServerConfig serverConfig = Loopback.FastServerConfig();
            serverConfig.GameVersion = "1.6.2f1";
            loop.StartServer(serverConfig);

            ClientConfig wrong = Loopback.FastClientConfig("Wrong");
            wrong.GameVersion = "1.5.0f1";
            ClientSession rejected = loop.StartClient(wrong);
            Assert.True(loop.PumpUntil(() => rejected.State == SessionState.Failed));
            Assert.Contains("Game version mismatch", rejected.LastError);

            ClientConfig silent = Loopback.FastClientConfig("Silent");
            silent.GameVersion = "";
            ClientSession alsoRejected = loop.StartClient(silent);
            Assert.True(loop.PumpUntil(() => alsoRejected.State == SessionState.Failed));
            Assert.Contains("Game version mismatch", alsoRejected.LastError);

            ClientConfig right = Loopback.FastClientConfig("Right");
            right.GameVersion = "1.6.2f1";
            ClientSession accepted = loop.StartClient(right);
            Assert.True(loop.PumpUntil(() => accepted.State == SessionState.Connected), accepted.LastError);
        }

        [Fact]
        public void GameVersion_IsNotCheckedWhenServerHasNone()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientConfig config = Loopback.FastClientConfig("Any");
            config.GameVersion = "whatever";
            ClientSession client = loop.StartClient(config);
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Connected), client.LastError);
        }

        [Fact]
        public void Chat_ReachesEveryoneIncludingSender_AndServerCanTalk()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartOwner("Ada");
            ClientSession b = loop.StartClient("Bob");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected && b.State == SessionState.Connected && server.Players.Count == 2));

            var serverSeen = new List<string>();
            var aSeen = new List<string>();
            var bSeen = new List<string>();
            server.ChatReceived += (p, t) => serverSeen.Add(p.Name + ": " + t);
            a.ChatReceived += (p, t) => aSeen.Add(p.Name + ": " + t);
            b.ChatReceived += (p, t) => bSeen.Add(p.Name + ": " + t);

            a.SendChat("hi from Ada");
            server.Say("welcome");

            Assert.True(loop.PumpUntil(() => serverSeen.Count == 1 && aSeen.Count == 2 && bSeen.Count == 2));
            Assert.Contains("Ada: hi from Ada", bSeen);
            Assert.Contains("Ada: hi from Ada", aSeen);
            Assert.Contains("TestServer: welcome", aSeen);
            Assert.Contains("TestServer: welcome", bSeen);
        }

        [Fact]
        public void SimulationSpeed_ClientRequest_IsAppliedByServerAndBroadcast()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartOwner("Ada");
            ClientSession b = loop.StartClient("Bob");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected && b.State == SessionState.Connected));

            float? serverGot = null, aGot = null, bGot = null;
            PlayerInfo requester = null;
            server.SimulationSpeedChanged += (s, p) => { serverGot = s; requester = p; };
            a.SimulationSpeedReceived += s => aGot = s;
            b.SimulationSpeedReceived += s => bGot = s;

            b.SubmitSimulationSpeed(0f);
            Assert.True(loop.PumpUntil(() => serverGot == 0f && aGot == 0f && bGot == 0f));
            Assert.Equal("Bob", requester.Name);
            Assert.Equal(0f, server.SimulationSpeed);
            Assert.Equal(0f, a.SimulationSpeed);

            aGot = bGot = null;
            server.SetSimulationSpeed(3f);
            Assert.True(loop.PumpUntil(() => aGot == 3f && bGot == 3f));
            Assert.Equal(3f, server.SimulationSpeed);
        }

        [Fact]
        public void NewClient_ReceivesCurrentSpeedOnJoin()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            server.SetSimulationSpeed(0f);

            ClientSession late = loop.StartClient("Late");
            Assert.True(loop.PumpUntil(() => late.State == SessionState.Connected && late.SimulationSpeed == 0f));
        }

        [Fact]
        public void GameplayCommand_ReachesServerAndOtherClientsNotSender()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartOwner("Ada");
            ClientSession b = loop.StartClient("Bob");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected && b.State == SessionState.Connected));

            var serverGot = new List<GameplayCommandMessage>();
            var aGot = new List<GameplayCommandMessage>();
            var bGot = new List<GameplayCommandMessage>();
            server.GameplayCommandRelayed += serverGot.Add;
            a.GameplayCommandReceived += aGot.Add;
            b.GameplayCommandReceived += bGot.Add;

            a.SendGameplayCommand("road.place", new byte[] { 1, 2, 3 });
            Assert.True(loop.PumpUntil(() => serverGot.Count == 1 && bGot.Count == 1));
            loop.PumpFor(100);

            Assert.Empty(aGot);
            Assert.Equal("road.place", bGot[0].Kind);
            Assert.Equal(a.LocalPlayerId, bGot[0].OriginPlayerId);
            Assert.Equal(new byte[] { 1, 2, 3 }, serverGot[0].Payload);
        }

        [Fact]
        public void ClientVanishing_IsDetectedByServer_AndOthersSeeItLeave()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartOwner("Ada");
            ClientSession b = loop.StartClient("Bob");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected && b.State == SessionState.Connected && b.Players.Count == 2));

            PlayerInfo leftOnServer = null;
            PlayerInfo leftOnB = null;
            server.PlayerLeft += (p, r) => leftOnServer = p;
            b.PlayerLeft += (p, r) => leftOnB = p;

            a.Leave("crash");

            Assert.True(loop.PumpUntil(() => leftOnServer != null && leftOnB != null, 3000));
            Assert.Equal("Ada", leftOnServer.Name);
            Assert.Equal("Ada", leftOnB.Name);
            Assert.Single(server.Players);
            Assert.Single(b.Players);
            Assert.Equal(0, server.OwnerPlayerId);
        }

        [Fact]
        public void SilentSocket_IsKickedAfterHandshakeTimeout()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            bool gotDisconnect = false;

            var raw = loop.RawClient();
            Assert.True(loop.PumpUntil(() =>
            {
                while (raw.TryDequeue(out TransportEvent ev))
                {
                    if (ev.Type == TransportEventType.Disconnected)
                    {
                        gotDisconnect = true;
                    }
                }

                return gotDisconnect;
            }, 3000));

            Assert.True(gotDisconnect);
            Assert.Empty(server.Players);
            Assert.Equal(0, server.PendingSockets);
        }

        [Fact]
        public void ServerStopping_TellsClientsWhy()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected));

            string stoppedReason = null;
            server.Stopped += r => stoppedReason = r;
            server.Stop("Host closed the session");

            Assert.True(loop.PumpUntil(() => a.State == SessionState.Failed));
            Assert.Contains("Host closed", a.LastError);
            Assert.Equal("Host closed the session", stoppedReason);
            Assert.False(server.IsRunning);
        }

        [Fact]
        public void OwnerCanStopServer_OthersCannot()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession owner = loop.StartOwner();
            ClientSession guest = loop.StartClient("Guest");
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected && guest.State == SessionState.Connected));

            guest.RequestServerStop("mine now");
            loop.PumpFor(150);
            Assert.True(server.IsRunning);

            string stoppedReason = null;
            server.Stopped += r => stoppedReason = r;
            owner.RequestServerStop("Host closed the session");
            owner.Leave("Host closed the session");

            Assert.True(loop.PumpUntil(() => !server.IsRunning && guest.State == SessionState.Failed));
            Assert.Equal("Host closed the session", stoppedReason);
            Assert.Contains("Host closed", guest.LastError);
        }

        [Fact]
        public void Kick_RemovesPlayerWithReason()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected));

            Assert.Equal("Ada", server.FindPlayer("ada").Name);
            Assert.True(server.KickPlayer(a.LocalPlayerId, "Be nice"));
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Failed));
            Assert.Contains("Be nice", a.LastError);
            Assert.Empty(server.Players);
            Assert.False(server.KickPlayer(99, "nobody"));
        }

        [Fact]
        public void DuplicateNames_GetSuffixed_AndSessionFullIsEnforced()
        {
            using var loop = new Loopback();
            ServerConfig config = Loopback.FastServerConfig();
            config.MaxPlayers = 2;
            ServerSession server = loop.StartServer(config);

            // Sequential joins so the order (and therefore who gets the suffix) is deterministic.
            ClientSession a = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected), a.LastError);
            ClientSession b = loop.StartClient("ada");
            Assert.True(loop.PumpUntil(() => b.State == SessionState.Connected), b.LastError);
            Assert.True(loop.PumpUntil(() => server.Players.Count == 2));

            // Suffixes are assigned case-insensitively but each player keeps the casing they typed.
            Assert.Equal(new[] { "Ada", "ada (2)" }, server.Players.Select(p => p.Name).ToArray());

            ClientSession c = loop.StartClient("Late");
            Assert.True(loop.PumpUntil(() => c.State == SessionState.Failed));
            Assert.Contains("full", c.LastError);
        }

        [Fact]
        public void AfterFailure_ClientCanJoinAgain()
        {
            using var loop = new Loopback();
            loop.StartServer(Loopback.FastServerConfig("pw"));
            ClientSession client = loop.StartClient("Ada", "wrong");
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Failed));

            client.Config.Password = "pw";
            loop.Rejoin(client);
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Connected), client.LastError);
        }

        [Fact]
        public void Server_CountsTraffic()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession a = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected));
            Assert.True(server.BytesIn > 0);
            Assert.True(server.BytesOut > 0);
        }
    }
}

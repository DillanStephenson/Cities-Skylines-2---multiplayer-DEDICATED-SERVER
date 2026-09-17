using Game;
using Game.SceneFlow;
using Game.Simulation;
using Multiplayer.Core.Session;

namespace Multiplayer
{
    /// <summary>
    /// Runs every frame in the MainLoop phase: pumps the session and keeps the simulation speed in step.
    /// Speed is server-authoritative. A local change is sent as a request; whatever the server announces wins.
    /// </summary>
    public partial class NetworkingSystem : GameSystemBase
    {
        private SimulationSystem m_SimulationSystem;
        private float m_LastKnownSpeed = float.NaN;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            Enabled = true;
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null)
            {
                return;
            }

            service.Update();

            if (service.Session.State != SessionState.Connected)
            {
                m_LastKnownSpeed = float.NaN;
                return;
            }

            GameManager gameManager = GameManager.instance;
            if (gameManager == null || gameManager.gameMode != GameMode.Game || gameManager.isGameLoading || m_SimulationSystem == null)
            {
                m_LastKnownSpeed = float.NaN;
                return;
            }

            // The server's announcement always wins over whatever the local UI did meanwhile.
            if (service.TryTakePendingSpeed(out float agreed))
            {
                m_SimulationSystem.selectedSpeed = agreed;
                m_LastKnownSpeed = agreed;
                return;
            }

            float local = m_SimulationSystem.selectedSpeed;
            if (float.IsNaN(m_LastKnownSpeed))
            {
                m_LastKnownSpeed = local;
                return;
            }

            if (local != m_LastKnownSpeed)
            {
                m_LastKnownSpeed = local;
                service.SubmitLocalSpeed(local);
            }
        }
    }
}

using System;
using System.Diagnostics;
using Game;
using Game.Common;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Mathematics;
using UnityEngine;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Sends where this player is: the camera pivot (the point on the map being looked at), its yaw and
    /// zoom, and the spot under the mouse. At most five times a second, and only when something moved.
    /// Runs in UIUpdate, after the camera and the tool raycast of the frame.
    /// </summary>
    public partial class PresenceSystem : GameSystemBase
    {
        private const long SendIntervalMs = 200;
        private const float MoveThreshold = 3f;
        private const long KeepAliveMs = 5000;

        private readonly Stopwatch m_Clock = Stopwatch.StartNew();
        private CameraUpdateSystem m_Camera;
        private ToolRaycastSystem m_Raycast;
        private PresenceCommand m_LastSent;
        private long m_LastSentMs = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_Raycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                if (PresenceStore.Count > 0 && (service == null || service.Session.State != SessionState.Connected))
                {
                    PresenceStore.Clear();
                }

                m_LastSent = null;
                return;
            }

            long now = m_Clock.ElapsedMilliseconds;
            if (now - m_LastSentMs < SendIntervalMs)
            {
                return;
            }

            CameraController controller = m_Camera != null ? m_Camera.gamePlayController : null;
            if (controller == null)
            {
                return;
            }

            Vector3 pivot = controller.pivot;
            Vector3 rotation = controller.rotation;
            var command = new PresenceCommand
            {
                Pivot = new Vec3(pivot.x, pivot.y, pivot.z),
                Yaw = rotation.y,
                Zoom = controller.zoom,
            };

            try
            {
                if (m_Raycast != null && m_Raycast.GetRaycastResult(out RaycastResult hit) && !math.all(hit.m_Hit.m_HitPosition == float3.zero))
                {
                    command.HasCursor = true;
                    command.Cursor = EntityResolver.ToVec(hit.m_Hit.m_HitPosition);
                }
            }
            catch (Exception)
            {
                // No raycast this frame (menu open, tool switching): send the camera alone.
            }

            bool keepAlive = m_LastSentMs >= 0 && now - m_LastSentMs > KeepAliveMs;
            if (!keepAlive && !command.DiffersFrom(m_LastSent, MoveThreshold))
            {
                return;
            }

            service.SendPresence(command);
            m_LastSent = command;
            m_LastSentMs = now;
        }
    }
}

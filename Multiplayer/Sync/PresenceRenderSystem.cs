using System.Collections.Generic;
using System.Diagnostics;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Draws the other players on the map with the game's own overlay renderer (the one the tools use for
    /// guide lines): a ring where their camera looks, a short line for the direction they face, and a dot
    /// under their mouse. Names are drawn by the UI on top (see the presence binding).
    /// </summary>
    public partial class PresenceRenderSystem : GameSystemBase
    {
        private readonly Stopwatch m_Clock = Stopwatch.StartNew();
        private OverlayRenderSystem m_Overlay;
        private TerrainSystem m_Terrain;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_Terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                return;
            }

            List<RemotePresence> players = PresenceStore.Snapshot(service.NowMs);
            List<RemotePreview> previews = PreviewStore.Snapshot(service.NowMs);
            if (players.Count == 0 && previews.Count == 0)
            {
                return;
            }

            OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            TerrainHeightData heights = m_Terrain.GetHeightData();

            // What the others are about to place: their tool's ghost, in their colour.
            foreach (RemotePreview remote in previews)
            {
                Color color = PresenceStore.ColorFor(remote.PlayerId);
                var fill = new Color(color.r, color.g, color.b, 0.35f);
                var deleting = new Color(1f, 0.25f, 0.2f, 0.45f);
                PreviewCommand preview = remote.Preview;
                foreach (PreviewCurve curve in preview.Curves)
                {
                    var bezier = new Bezier4x3(EntityResolver.ToFloat3(curve.A), EntityResolver.ToFloat3(curve.B), EntityResolver.ToFloat3(curve.C), EntityResolver.ToFloat3(curve.D));
                    buffer.DrawCurve(color, curve.Deleting ? deleting : fill, 1f, OverlayRenderSystem.StyleFlags.Projected, bezier, math.clamp(curve.Width, 2f, 60f));
                }

                foreach (PreviewPoint point in preview.Points)
                {
                    float3 position = OnGround(ref heights, EntityResolver.ToFloat3(point.Position));
                    buffer.DrawCircle(color, point.Deleting ? deleting : fill, 1f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, math.clamp(point.Radius * 2f, 4f, 200f));
                }

                foreach (PreviewLoop loop in preview.Loops)
                {
                    for (int n = 0; n < loop.Nodes.Count; n++)
                    {
                        float3 from = OnGround(ref heights, EntityResolver.ToFloat3(loop.Nodes[n]));
                        float3 to = OnGround(ref heights, EntityResolver.ToFloat3(loop.Nodes[(n + 1) % loop.Nodes.Count]));
                        buffer.DrawLine(color, new Line3.Segment(from, to), 2f);
                    }
                }
            }

            foreach (RemotePresence player in players)
            {
                Color color = PresenceStore.ColorFor(player.PlayerId);
                float3 pivot = OnGround(ref heights, player.Pivot);

                // Ring size follows how far out they are zoomed, so it stays readable from any height.
                float diameter = math.clamp(player.Zoom * 0.12f, 24f, 220f);
                float width = math.clamp(diameter * 0.06f, 1.5f, 8f);
                buffer.DrawCircle(color, new Color(color.r, color.g, color.b, 0.08f), width, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), pivot, diameter);

                // The way they are facing: a line out of the ring.
                float yaw = math.radians(player.Yaw);
                var direction = new float3(math.sin(yaw), 0f, math.cos(yaw));
                float3 from = pivot + direction * (diameter * 0.5f);
                float3 to = OnGround(ref heights, pivot + direction * diameter);
                buffer.DrawLine(color, new Line3.Segment(from, to), width);

                if (player.HasCursor)
                {
                    float3 cursor = OnGround(ref heights, player.Cursor);
                    buffer.DrawCircle(color, color, 1f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), cursor, math.clamp(diameter * 0.12f, 4f, 16f));
                }
            }

            m_Overlay.AddBufferWriter(default(JobHandle));
        }

        private static float3 OnGround(ref TerrainHeightData heights, float3 position)
        {
            position.y = TerrainUtils.SampleHeight(ref heights, position) + 1f;
            return position;
        }
    }
}

using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>A player's tool ghost on the wire: curves, points and area outlines, and the empty message that clears it.</summary>
    public class PreviewTests
    {
        [Fact]
        public void RoundTrips()
        {
            var preview = new PreviewCommand();
            preview.Curves.Add(new PreviewCurve { A = new Vec3(0, 1, 2), B = new Vec3(3, 4, 5), C = new Vec3(6, 7, 8), D = new Vec3(9, 10, 11), Width = 12.5f, Deleting = true });
            preview.Points.Add(new PreviewPoint { Position = new Vec3(20, 21, 22), Radius = 7f });
            var loop = new PreviewLoop();
            loop.Nodes.Add(new Vec3(1, 0, 1));
            loop.Nodes.Add(new Vec3(2, 0, 1));
            loop.Nodes.Add(new Vec3(2, 0, 2));
            preview.Loops.Add(loop);

            PreviewCommand copy = PreviewCommand.FromBytes(preview.ToBytes());

            Assert.False(copy.IsEmpty);
            Assert.Single(copy.Curves);
            Assert.Equal(11f, copy.Curves[0].D.Z);
            Assert.Equal(12.5f, copy.Curves[0].Width);
            Assert.True(copy.Curves[0].Deleting);
            Assert.Equal(7f, copy.Points[0].Radius);
            Assert.False(copy.Points[0].Deleting);
            Assert.Equal(3, copy.Loops[0].Nodes.Count);
            Assert.Equal(2f, copy.Loops[0].Nodes[2].Z);
            Assert.Contains("1 curves, 1 points, 1 areas", copy.ToString());
        }

        [Fact]
        public void Empty_ClearsAndSaysSo()
        {
            PreviewCommand copy = PreviewCommand.FromBytes(new PreviewCommand().ToBytes());
            Assert.True(copy.IsEmpty);
            Assert.Equal("preview cleared", copy.ToString());
        }

        [Fact]
        public void RefusesAbsurdCounts()
        {
            byte[] bytes = new PreviewCommand().ToBytes();
            bytes[1] = 0xFF;
            bytes[2] = 0xFF;
            bytes[3] = 0xFF;
            bytes[4] = 0x7F;
            Assert.Throws<ProtocolException>(() => PreviewCommand.FromBytes(bytes));
        }
    }
}

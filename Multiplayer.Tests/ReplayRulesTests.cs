using Multiplayer.Core.Build;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>Specialised industry areas are held back on both sides; plain districts and everything else replay.</summary>
    public class ReplayRulesTests
    {
        [Fact]
        public void AreaWithHubBuilding_IsHeld()
        {
            var command = new BuildCommand { ToolId = "Area Tool" };
            command.Definitions.Add(new DefinitionData { Prefab = new PrefabKey { Type = "AreaPrefab", Name = "Agriculture Area Placeholder - Grain" }, Object = new ObjectDefinitionData() });
            command.Definitions.Add(new DefinitionData { Prefab = new PrefabKey { Type = "NetPrefab", Name = "Invisible Road Path - 1xTwoway" }, Course = new NetCourseData() });
            Assert.True(ReplayRules.IsSpecialisedArea(command));
            Assert.Contains("next save", ReplayRules.HoldReason(command));
        }

        [Fact]
        public void PlainDistrict_AndOtherTools_Replay()
        {
            var district = new BuildCommand { ToolId = "Area Tool" };
            district.Definitions.Add(new DefinitionData { Prefab = new PrefabKey { Type = "DistrictPrefab", Name = "District" }, AreaNodes = new System.Collections.Generic.List<AreaNodeData>() });
            Assert.False(ReplayRules.IsSpecialisedArea(district));
            Assert.Null(ReplayRules.HoldReason(district));

            var road = new BuildCommand { ToolId = "Net Tool" };
            road.Definitions.Add(new DefinitionData { Course = new NetCourseData() });
            Assert.Null(ReplayRules.HoldReason(road));

            var building = new BuildCommand { ToolId = "Object Tool" };
            building.Definitions.Add(new DefinitionData { Object = new ObjectDefinitionData() });
            Assert.Null(ReplayRules.HoldReason(building));
        }
    }
}

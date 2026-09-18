namespace Multiplayer.Core.Build
{
    /// <summary>
    /// What is not replayed on other PCs (yet). Both the sender and the receiver check, so a newer game protects
    /// an older one on either side.
    /// </summary>
    public static class ReplayRules
    {
        public const string AreaTool = "Area Tool";

        /// <summary>
        /// Specialised industry areas (farms, forestry, mining, oil) come with their hub building and invisible
        /// service paths in one command. Recreating that set from definitions crashes the receiving game
        /// natively, so such commands are held back; the area reaches the others with the next save.
        /// Plain districts are just area nodes and replay fine.
        /// </summary>
        public static bool IsSpecialisedArea(BuildCommand command)
        {
            if (command == null || command.ToolId != AreaTool)
            {
                return false;
            }

            foreach (DefinitionData definition in command.Definitions)
            {
                if (definition.Object != null || definition.Course != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Why a command is held back, or null when it replays normally.
        /// <paramref name="touchesSpecialisedArea"/> is supplied by the caller, which can see the game's prefab
        /// data: an Area Tool command that only moves or adds a node of an existing farm or oil field carries no
        /// Object or Course definition, so the shape of the command alone cannot tell it apart from a district.
        /// Those edits used to go out while the area's creation was held back, so on the other PC they looked for
        /// an area that had never been built there and failed forever.
        /// </summary>
        public static string HoldReason(BuildCommand command, bool touchesSpecialisedArea = false)
        {
            return IsSpecialisedArea(command) || (touchesSpecialisedArea && command != null && command.ToolId == AreaTool)
                ? "specialised industry areas are not replayed yet; they arrive with the next save"
                : null;
        }
    }
}

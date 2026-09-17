import { MenuButton } from "cs2/ui";
import { openScreen } from "./bindings";

/** The MULTIPLAYER entry in the main menu list, styled like the game's own entries. */
export const MultiplayerMenuButton = () => {
  const props: any = { tinted: true, src: "Media/Glyphs/Passenger.svg", onSelect: openScreen };
  return <MenuButton {...props}>Multiplayer</MenuButton>;
};

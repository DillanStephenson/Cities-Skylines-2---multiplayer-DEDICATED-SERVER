import { useValue } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { screenActive$ } from "mods/bindings";
import { GamePanel, GameToolbarButton } from "mods/game-panel";
import { MultiplayerMenuButton } from "mods/menu-button";
import { MultiplayerScreen } from "mods/multiplayer-screen";

const MAIN_MENU = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const CREDITS = "game-ui/menu/components/credits-screen/credits-screen.tsx";

/**
 * Two hooks into the game's menu:
 *  - a MULTIPLAYER entry in the main menu list (after Continue, New Game, Load Game);
 *  - the credits sub-screen, which shows our screen instead while the C# side has it active. The main
 *    menu only knows its own sub-screens, and the credits one is the only one without side effects, so
 *    opening it is how a mod gets a full sub-screen with the game's backdrop, title bar and back button.
 */
const register: ModRegistrar = (moduleRegistry) => {
  // In-game: a toolbar button at the bottom right, and the panel it opens drawn over the game view.
  try {
    moduleRegistry.append("GameBottomRight", GameToolbarButton);
    moduleRegistry.append("Game", GamePanel);
  } catch (error) {
    console.warn("[Multiplayer] could not add the in-game panel", error);
  }

  let screenHooked = false;
  try {
    if (moduleRegistry.registry.has(CREDITS)) {
      moduleRegistry.extend(CREDITS, "CreditsScreen", (Original: any) => (props: any) => {
        const active = useValue(screenActive$);
        return active ? <MultiplayerScreen {...props} /> : <Original {...props} />;
      });
      screenHooked = true;
    }
  } catch (error) {
    console.warn("[Multiplayer] could not hook the credits sub-screen", error);
  }

  if (!screenHooked) {
    console.warn("[Multiplayer] menu screen unavailable in this game version; the Options page still works");
    return;
  }

  try {
    if (moduleRegistry.registry.has(MAIN_MENU)) {
      moduleRegistry.append(MAIN_MENU, "MainMenuNavigation", MultiplayerMenuButton, 3);
      return;
    }
  } catch (error) {
    console.warn("[Multiplayer] could not add the main menu entry; using the generic hook", error);
  }

  moduleRegistry.append("Menu", MultiplayerMenuButton);
};

export default register;

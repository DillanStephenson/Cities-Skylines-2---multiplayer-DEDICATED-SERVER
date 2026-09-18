import { useValue } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { screenActive$ } from "mods/bindings";
import { GamePanel, GameToolbarButton } from "mods/game-panel";
import { MultiplayerMenuButton } from "mods/menu-button";
import { MultiplayerScreen } from "mods/multiplayer-screen";
import { PresenceOverlay } from "mods/presence-overlay";
import { SyncModal } from "mods/sync-modal";

const MAIN_MENU = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const CREDITS = "game-ui/menu/components/credits-screen/credits-screen.tsx";

const log = (message: string) => console.log("[Multiplayer] " + message);

/**
 * Attaches one component to one of the game's anchors. Each one is attempted on its own: they used to share
 * a single try block, so the first anchor the game did not recognise took every later one down with it and
 * the panel, the presence overlay and the syncing box were silently never registered at all. Every outcome
 * is logged, so UI.log says exactly which parts of the interface exist in this game version.
 */
const attach = (registry: any, target: string, component: any, name: string): boolean => {
  try {
    if (typeof registry.hasAppend === "function" && !registry.hasAppend(target)) {
      console.warn(`[Multiplayer] anchor "${target}" is not offered by this game version; ${name} not shown`);
      return false;
    }

    registry.append(target, component);
    log(`${name} attached to ${target}`);
    return true;
  } catch (error) {
    console.warn(`[Multiplayer] could not attach ${name} to ${target}`, error);
    return false;
  }
};

/**
 * Two hooks into the game's menu:
 *  - a MULTIPLAYER entry in the main menu list (after Continue, New Game, Load Game);
 *  - the credits sub-screen, which shows our screen instead while the C# side has it active. The main
 *    menu only knows its own sub-screens, and the credits one is the only one without side effects, so
 *    opening it is how a mod gets a full sub-screen with the game's backdrop, title bar and back button.
 */
const register: ModRegistrar = (moduleRegistry) => {
  log("UI module registering...");

  // In a city: the toolbar button, the panel it opens, the other players' markers, and the syncing box.
  // The syncing box is attached first, because it is the one that must never be missing: it is what stops
  // a player building while everyone is being brought back into step.
  const registry = moduleRegistry as any;
  const modal = attach(registry, "Game", SyncModal, "syncing box");
  const panel = attach(registry, "Game", GamePanel, "in-game panel");
  attach(registry, "Game", PresenceOverlay, "player markers");
  const button = attach(registry, "GameBottomRight", GameToolbarButton, "toolbar button");

  if (!button && panel) {
    // No toolbar to put the button in, but the panel itself works. Put the button in the panel's own
    // corner instead so there is still a way to open it.
    attach(registry, "GameTopRight", GameToolbarButton, "toolbar button (top right)");
  }

  if (!modal) {
    console.warn("[Multiplayer] the syncing box could not be attached; a forced sync will not block this player");
  }

  let screenHooked = false;
  try {
    if (moduleRegistry.registry.has(CREDITS)) {
      moduleRegistry.extend(CREDITS, "CreditsScreen", (Original: any) => (props: any) => {
        const active = useValue(screenActive$);
        return active ? <MultiplayerScreen {...props} /> : <Original {...props} />;
      });
      screenHooked = true;
      log("menu screen hooked");
    }
  } catch (error) {
    console.warn("[Multiplayer] could not hook the credits sub-screen", error);
  }

  if (!screenHooked) {
    console.warn("[Multiplayer] menu screen unavailable in this game version; the Options page still works");
    log("UI module registrations completed.");
    return;
  }

  try {
    if (moduleRegistry.registry.has(MAIN_MENU)) {
      moduleRegistry.append(MAIN_MENU, "MainMenuNavigation", MultiplayerMenuButton, 3);
      log("main menu entry added");
      log("UI module registrations completed.");
      return;
    }
  } catch (error) {
    console.warn("[Multiplayer] could not add the main menu entry; using the generic hook", error);
  }

  attach(registry, "Menu", MultiplayerMenuButton, "main menu entry");
  log("UI module registrations completed.");
};

export default register;

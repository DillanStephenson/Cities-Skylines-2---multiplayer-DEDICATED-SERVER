import { useValue } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { screenActive$ } from "mods/bindings";
import { GameFloatingToggle, GamePanel, GameToolbarButton } from "mods/game-panel";
import { MultiplayerMenuButton } from "mods/menu-button";
import { MultiplayerScreen } from "mods/multiplayer-screen";
import { PresenceOverlay } from "mods/presence-overlay";
import { SyncModal } from "mods/sync-modal";

const MAIN_MENU = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const CREDITS = "game-ui/menu/components/credits-screen/credits-screen.tsx";

const log = (message: string) => console.log("[Multiplayer] " + message);

const REGISTERED = "multiplayer-coop/registered";

/**
 * Attaches one component to one of the game's anchors, logging the outcome to UI.log. Deliberately does NOT ask
 * hasAppend first: in this game version it answered "no" for "Game" after the interface reloaded, which is a slot
 * that works, so gating on it left the panel, the syncing box and the player markers out entirely. Each part is
 * attached in its own try, so one failure cannot take the rest down with it.
 */
const attach = (registry: any, target: string, component: any, name: string): boolean => {
  try {
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
  // Two copies of this mod (a local build and a subscribed one) serve the same module URL, so the game can hand
  // this registry our registration twice. Only the first may run, or every panel, box and button would appear
  // twice. The marker lives in the registry itself, not on the page: when the game resets the registry and
  // registers every mod again, the marker is gone with everything else and this registers afresh.
  if (moduleRegistry.registry.has(REGISTERED)) {
    console.warn("[Multiplayer] this mod's interface was registered twice into the same registry; ignoring the second");
    return;
  }

  moduleRegistry.add(REGISTERED, {});
  log("UI module registering...");

  // In a city: the toolbar button, the panel it opens, the other players' markers, and the syncing box.
  // The syncing box is attached first, because it is the one that must never be missing: it is what stops
  // a player building while everyone is being brought back into step.
  const registry = moduleRegistry as any;
  const modal = attach(registry, "Game", SyncModal, "syncing box");
  attach(registry, "Game", GamePanel, "in-game panel");
  attach(registry, "Game", PresenceOverlay, "player markers");
  attach(registry, "GameBottomRight", GameToolbarButton, "toolbar button");

  // Always attached, but it only shows itself while the toolbar button is not on screen, so there is always
  // exactly one way to open the panel.
  attach(registry, "Game", GameFloatingToggle, "floating open button");

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

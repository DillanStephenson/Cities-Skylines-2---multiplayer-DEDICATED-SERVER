import { useValue } from "cs2/api";
import { useEffect, useState } from "react";
import { getModule } from "cs2/modding";
import { Button } from "cs2/ui";
import * as b from "./bindings";
import styles from "./multiplayer.module.scss";

/** True while the button in the game's own right-hand column is on screen. */
let toolbarButtonShown = false;

/**
 * The game's own right-menu styles, the ones the Chirper and notification buttons use. Borrowing them makes the
 * button sit and look exactly like its neighbours. The paths are internal to the game and can move in an update,
 * so a missing one falls back to this mod's own look.
 */
const tryClasses = (path: string): Record<string, string> | null => {
  try {
    return getModule(path, "classes") || null;
  } catch {
    return null;
  }
};
const rightMenuButton = tryClasses("game-ui/game/components/right-menu/right-menu-button.module.scss");
const rightMenu = tryClasses("game-ui/game/components/right-menu/right-menu.module.scss");

/** The round Multiplayer button in this mod's own look; a green dot shows while connected. */
const ToggleButton = () => {
  const online = useValue(b.online$);
  const open = useValue(b.panelOpen$);
  return (
    <div
      className={styles.gameToggleWrap}
      onMouseDown={(e) => e.stopPropagation()}
      onClick={(e) => {
        e.stopPropagation();
        b.togglePanel();
      }}
    >
      <Button variant="floating" className={open ? styles.gameToggle + " " + styles.gameToggleOpen : styles.gameToggle} onSelect={b.togglePanel} tooltipLabel="Multiplayer">
        <img src="Media/Glyphs/Passenger.svg" className={styles.gameToggleIcon} />
        {online && <div className={styles.gameDot} />}
      </Button>
    </div>
  );
};

/**
 * The button in the game's right-hand column. That column, and everything the game puts under it, has mouse
 * input switched off; only the game's own buttons switch it back on, through their styles. This button used to
 * carry only this mod's styles, so it was drawn but the game's hit test never landed on it: a real click at its
 * centre went through into the city, which is why pressing it never opened the panel. It now wears the game's
 * right-menu styles like the Chirper does, and switches mouse input on itself as well, in case those styles move.
 * The click is taken both through the game's onSelect and a plain onClick, whichever arrives; the C# side ignores
 * the second of two toggles in the same instant, so one click is always one toggle.
 */
export const GameToolbarButton = () => {
  const online = useValue(b.online$);
  const open = useValue(b.panelOpen$);
  useEffect(() => {
    toolbarButtonShown = true;
    return () => {
      toolbarButtonShown = false;
    };
  }, []);

  if (!rightMenuButton) {
    return <ToggleButton />;
  }

  return (
    <div
      className={(rightMenu ? rightMenu.item + " " : "") + styles.rightMenuItem}
      onMouseDown={(e) => e.stopPropagation()}
      onClick={(e) => {
        e.stopPropagation();
        b.togglePanel();
      }}
    >
      <Button
        theme={{ button: rightMenuButton.button, icon: rightMenuButton.icon }}
        className={(rightMenuButton.toggleStates || "") + " " + styles.rightMenuButton}
        selected={open}
        onSelect={b.togglePanel}
        tooltipLabel="Multiplayer"
      >
        <img src="Media/Glyphs/Passenger.svg" className={rightMenuButton.icon} />
      </Button>
      {online && <div className={styles.rightMenuDot} />}
    </div>
  );
};

/**
 * The same button pinned over the game view, for when the toolbar slot does not show it. Whether a game version
 * offers that slot cannot be asked up front (hasAppend answered "no" for slots that work), so this looks after a
 * moment instead, and keeps looking: it shows only while the toolbar copy is absent, so there is always exactly
 * one way to open the panel and never two.
 */
export const GameFloatingToggle = () => {
  const open = useValue(b.panelOpen$);
  const [needed, setNeeded] = useState(false);
  useEffect(() => {
    const check = () => setNeeded(!toolbarButtonShown);
    const first = setTimeout(check, 1500);
    const again = setInterval(check, 5000);
    return () => {
      clearTimeout(first);
      clearInterval(again);
    };
  }, []);

  if (!needed) {
    return null;
  }

  return (
    <div className={styles.gameFloating}>
      <ToggleButton />
      {!open && <div className={styles.gameFloatingHint}>Multiplayer</div>}
    </div>
  );
};

/** The panel the toolbar button opens: status, players, and the session actions. */
export const GamePanel = () => {
  const open = useValue(b.panelOpen$);
  const online = useValue(b.online$);
  const state = useValue(b.state$);
  const isOwner = useValue(b.isOwner$);
  const serverName = useValue(b.serverName$);
  const players = useValue(b.players$);
  const world = useValue(b.world$);
  const worldStatus = useValue(b.worldStatus$);
  const newerCity = useValue(b.newerCity$);
  const recent = useValue(b.recent$);
  const joinAddress = useValue(b.joinAddress$);
  const busy = useValue(b.transferBusy$);
  const isLeader = useValue(b.isLeader$);
  const health = useValue(b.health$);
  const warning = useValue(b.warning$);
  const ledger = useValue(b.ledger$);
  if (!open) {
    return null;
  }

  const playerLines = players ? players.split("\n") : [];
  const recentLines = recent ? recent.split("\n").slice(-4) : [];

  return (
    <div className={styles.gamePanel} onMouseDown={(e) => e.stopPropagation()}>
      <div className={styles.gamePanelHeader}>
        <div className={styles.gamePanelTitle}>Multiplayer</div>
        <Button variant="icon" className={styles.gameClose} onSelect={b.togglePanel}>
          <img src="Media/Glyphs/Close.svg" className={styles.gameCloseIcon} />
        </Button>
      </div>

      {warning && <div className={styles.gameWarning}>{warning}</div>}

      <div className={styles.gameRow}>
        <span className={styles.gameKey}>Status</span>
        <span className={online ? styles.statusOnline : styles.statusValue}>{online ? `${state}${isOwner ? " as owner" : ""}` : "Not connected"}</span>
      </div>
      {serverName && (
        <div className={styles.gameRow}>
          <span className={styles.gameKey}>Server</span>
          <span className={styles.statusValue}>{serverName}</span>
        </div>
      )}
      {playerLines.length > 0 && (
        <div className={styles.gameRow}>
          <span className={styles.gameKey}>Players</span>
          <span className={styles.statusValue}>{playerLines.join(", ")}</span>
        </div>
      )}
      {world && (
        <div className={styles.gameRow}>
          <span className={styles.gameKey}>On server</span>
          <span className={styles.statusValue}>{world}</span>
        </div>
      )}
      {online && health && (
        <div className={styles.gameRow}>
          <span className={styles.gameKey}>Sync</span>
          <span className={styles.statusValue}>{health}</span>
        </div>
      )}
      {worldStatus && <div className={styles.gameTransfer}>{worldStatus}</div>}
      {online && ledger && <div className={styles.gameLedger}>{ledger}</div>}
      {recentLines.length > 0 && (
        <div className={styles.gameLog}>
          {recentLines.map((line, index) => (
            <div key={index}>{line}</div>
          ))}
        </div>
      )}

      <div className={styles.gameActions}>
        {online && (
          <Button variant="primary" className={styles.gameButton} disabled={busy} onSelect={b.uploadCity}>
            {busy ? "Working..." : "Save to server"}
          </Button>
        )}
        {online && newerCity && (
          <Button variant="primary" className={styles.gameButton} disabled={busy} onSelect={b.fetchCity}>
            Get the newer city
          </Button>
        )}
        {online && isLeader && (
          <Button variant="flat" className={styles.gameButtonQuiet} disabled={busy} onSelect={b.syncNow}>
            Sync everyone now
          </Button>
        )}
        {online && (
          <Button variant="flat" className={styles.gameButtonQuiet} onSelect={b.leave}>
            Disconnect
          </Button>
        )}
      </div>
      {online && <div className={styles.gameHint}>Saving sends the city you are playing to the server; the others fetch it from here or reload on their own if they have that switched on.</div>}
      {!online && joinAddress && (
        <div className={styles.gameActions}>
          <Button variant="primary" className={styles.gameButton} onSelect={b.join}>
            {"Reconnect to " + joinAddress}
          </Button>
        </div>
      )}
      {!online && <div className={styles.gameHint}>{"Join or host from the main menu, or under Options > Multiplayer."}</div>}
    </div>
  );
};

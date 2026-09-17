import { useValue } from "cs2/api";
import { Button } from "cs2/ui";
import * as b from "./bindings";
import styles from "./multiplayer.module.scss";

/**
 * Round button in the bottom-right toolbar; a green dot shows while connected. The click is taken both
 * through the game's onSelect and a plain onClick, whichever the toolbar lets through; the C# side ignores
 * the second of two toggles in the same instant, so one click is always one toggle.
 */
export const GameToolbarButton = () => {
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
      {worldStatus && <div className={styles.gameTransfer}>{worldStatus}</div>}
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

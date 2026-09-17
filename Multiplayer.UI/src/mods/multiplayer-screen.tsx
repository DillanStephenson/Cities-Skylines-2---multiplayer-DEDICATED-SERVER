import { useValue } from "cs2/api";
import { AutoNavigationScope, NavigationDirection } from "cs2/input";
import { getModule } from "cs2/modding";
import { Button } from "cs2/ui";
import { useEffect, useState } from "react";
import * as b from "./bindings";
import styles from "./multiplayer.module.scss";

type View = "choice" | "join" | "host";

function tryGetModule(path: string, name: string): any {
  try {
    return getModule(path, name);
  } catch {
    return null;
  }
}

// The game's own sub-screen frame (backdrop, title bar, back button). Falls back to a plain frame if the
// module path changes in a game update.
const SubScreen: any = tryGetModule("game-ui/menu/components/shared/sub-screen/sub-screen.tsx", "SubScreen");

/** Rendered in place of the credits screen while the C# side says the multiplayer screen is active. */
export const MultiplayerScreen = (props: any) => {
  const requested = useValue(b.requestedView$);
  const [view, setView] = useState<View>(requested === "join" || requested === "host" ? requested : "choice");
  const online = useValue(b.online$);
  const hostChoice = useValue(b.hostChoice$);

  // Tell the C# side when the player leaves the screen (back button, or the game loading a city), so the
  // credits screen goes back to being the credits screen.
  useEffect(() => () => b.screenExited(), []);

  const title = hostChoice ? "Load the last save, or start a new world?" : view === "choice" ? "Multiplayer" : view === "join" ? "Join a server" : "Host a session";
  const back = view === "choice" ? props.onClose : () => setView("choice");

  // One navigation scope around everything: the sub-screen's focus node hosts a single child, and this
  // also gives gamepad users a way through the cards, fields and buttons.
  const body = (
    <AutoNavigationScope debugName="Multiplayer screen" direction={NavigationDirection.Both} allowLooping>
    <div className={styles.page}>
      {hostChoice && <HostChoice />}
      {!hostChoice && view === "choice" && (
        <div className={styles.cards}>
          <Card icon="Media/Glyphs/Passenger.svg" label="Join game" hint="Connect to a friend's server window or a dedicated server by address" onSelect={() => setView("join")} />
          <Card icon="Media/Glyphs/Residence.svg" label="Host game" hint="Open the server window on this PC and play in it as the owner" onSelect={() => setView("host")} />
        </div>
      )}
      {!hostChoice && view === "join" && <JoinForm />}
      {!hostChoice && view === "host" && <HostForm />}
      {(view !== "choice" || online) && <StatusPanel />}
    </div>
    </AutoNavigationScope>
  );

  if (SubScreen) {
    return (
      <SubScreen focusKey={props.focusKey} className={props.className} title={title} onClose={back}>
        {body}
      </SubScreen>
    );
  }

  return (
    <div className={props.className}>
      <div className={styles.fallbackHeader}>
        <Button variant="icon" onSelect={back}>
          <img src="Media/Glyphs/TriangleArrowLeft.svg" className={styles.fallbackBackIcon} />
        </Button>
        <div className={styles.fallbackTitle}>{title}</div>
      </div>
      {body}
    </div>
  );
};

// ------------------------------------------------------------------ pieces

/** Asked of the host right after connecting from the menu, when the server already holds a city. */
const HostChoice = () => {
  const world = useValue(b.world$);
  return (
    <div className={styles.panel}>
      <div className={styles.note}>{"The server holds " + world + "."}</div>
      <div className={styles.note}>
        {"Load last save carries on with that city. New world opens the map list, mod maps included; pick one, set the options you want (unlock all map tiles, and so on), and the city you start replaces the one on the server as soon as it has loaded."}
      </div>
      <div className={styles.actions}>
        <Button variant="primary" className={styles.button} onSelect={b.chooseLoad}>
          Load last save
        </Button>
        <Button variant="flat" className={styles.button} onSelect={b.chooseNewWorld}>
          New world
        </Button>
      </div>
    </div>
  );
};

const Card = ({ icon, label, hint, onSelect }: { icon: string; label: string; hint: string; onSelect: () => void }) => (
  <Button variant="flat" className={styles.card} onSelect={onSelect}>
    <div className={styles.cardIconFrame}>
      {/* Masked instead of drawn, so dark glyphs (the house) come out as white as the light ones. */}
      <div className={styles.cardIcon} style={{ maskImage: `url(${icon})`, WebkitMaskImage: `url(${icon})` }} />
    </div>
    <div className={styles.cardLabel}>{label}</div>
    <div className={styles.cardHint}>{hint}</div>
  </Button>
);

const Field = ({ label, value, secret, placeholder, onCommit }: { label: string; value: string; secret?: boolean; placeholder?: string; onCommit: (value: string) => void }) => {
  const [text, setText] = useState(value);
  const [focused, setFocused] = useState(false);
  useEffect(() => {
    if (!focused) {
      setText(value);
    }
  }, [value, focused]);

  return (
    <div className={styles.row}>
      <div className={styles.label}>{label}</div>
      <input
        className={styles.input}
        type={secret ? "password" : "text"}
        value={text}
        placeholder={placeholder}
        spellCheck={false}
        autoComplete="off"
        onFocus={() => setFocused(true)}
        onBlur={() => {
          setFocused(false);
          if (text !== value) {
            onCommit(text);
          }
        }}
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          // Keep typing from reaching the game's shortcuts; Escape still closes the screen.
          if (e.key !== "Escape") {
            e.stopPropagation();
          }
        }}
        onChange={(e) => {
          setText(e.target.value);
          onCommit(e.target.value);
        }}
      />
    </div>
  );
};

const JoinForm = () => {
  const online = useValue(b.online$);
  const playerName = useValue(b.playerName$);
  const address = useValue(b.joinAddress$);
  const password = useValue(b.joinPassword$);
  const ownerKey = useValue(b.joinOwnerKey$);

  return (
    <div className={styles.panel}>
      <Field label="Player name" value={playerName} placeholder="How others see you" onCommit={b.setPlayerName} />
      <Field label="Address" value={address} placeholder="host or ip:port, for example 57.129.141.115:27015" onCommit={b.setJoinAddress} />
      <Field label="Password" value={password} secret placeholder="Leave empty if the server has none" onCommit={b.setJoinPassword} />
      <Field label="Owner key" value={ownerKey} secret placeholder="Only the host enters this" onCommit={b.setJoinOwnerKey} />
      <div className={styles.note}>
        {"Join fetches the server's city and loads it. New city (owner key needed) connects and opens New Game instead: pick any map, and the city you start replaces the one on the server as soon as it has loaded."}
      </div>
      <div className={styles.actions}>
        {!online && (
          <Button variant="primary" className={styles.button} onSelect={b.join}>
            Join
          </Button>
        )}
        {!online && ownerKey && (
          <Button variant="flat" className={styles.button} onSelect={b.joinNewCity}>
            New city
          </Button>
        )}
        {online && (
          <Button variant="primary" className={styles.button} onSelect={b.leave}>
            Disconnect
          </Button>
        )}
      </div>
    </div>
  );
};

const HostForm = () => {
  const online = useValue(b.online$);
  const playerName = useValue(b.playerName$);
  const port = useValue(b.hostPort$);
  const password = useValue(b.hostPassword$);

  return (
    <div className={styles.panel}>
      <Field label="Player name" value={playerName} placeholder="Also used as the server name" onCommit={b.setPlayerName} />
      <Field label="Port" value={port} placeholder="27015" onCommit={b.setHostPort} />
      <Field label="Password" value={password} secret placeholder="Optional" onCommit={b.setHostPassword} />
      <div className={styles.note}>
        Host opens the server window next to the game and joins it as the owner. Friends join your public address and this port, so the port has to be forwarded on your router. To play the city you have open, load it first and press Upload my city once connected.
      </div>
      <div className={styles.actions}>
        {!online && (
          <Button variant="primary" className={styles.button} onSelect={b.host}>
            Host
          </Button>
        )}
        {online && (
          <Button variant="primary" className={styles.button} onSelect={b.leave}>
            Disconnect
          </Button>
        )}
      </div>
    </div>
  );
};

const StatusPanel = () => {
  const state = useValue(b.state$);
  const online = useValue(b.online$);
  const isOwner = useValue(b.isOwner$);
  const inGame = useValue(b.inGame$);
  const serverName = useValue(b.serverName$);
  const players = useValue(b.players$);
  const world = useValue(b.world$);
  const worldStatus = useValue(b.worldStatus$);
  const lastError = useValue(b.lastError$);
  const recent = useValue(b.recent$);
  const playerLines = players ? players.split("\n") : [];
  const recentLines = recent ? recent.split("\n").slice(-6) : [];

  return (
    <div className={styles.status}>
      <div className={styles.statusRow}>
        <span className={styles.statusKey}>Status</span>
        <span className={online ? styles.statusOnline : styles.statusValue}>{`${state}${online && isOwner ? " as owner" : ""}`}</span>
      </div>
      {serverName && (
        <div className={styles.statusRow}>
          <span className={styles.statusKey}>Server</span>
          <span className={styles.statusValue}>{serverName}</span>
        </div>
      )}
      {lastError && <div className={styles.error}>{lastError}</div>}
      {playerLines.length > 0 && (
        <div className={styles.statusRow}>
          <span className={styles.statusKey}>Players</span>
          <span className={styles.statusValue}>{playerLines.join(", ")}</span>
        </div>
      )}
      {world && (
        <div className={styles.statusRow}>
          <span className={styles.statusKey}>Shared city</span>
          <span className={styles.statusValue}>{world}</span>
        </div>
      )}
      {worldStatus && (
        <div className={styles.statusRow}>
          <span className={styles.statusKey}>Transfer</span>
          <span className={styles.statusValue}>{worldStatus}</span>
        </div>
      )}
      {recentLines.length > 0 && (
        <div className={styles.log}>
          {recentLines.map((line, index) => (
            <div key={index}>{line}</div>
          ))}
        </div>
      )}
      {online && (
        <div className={styles.actions}>
          {inGame && isOwner && (
            <Button variant="primary" className={styles.button} onSelect={b.uploadCity}>
              Upload my city
            </Button>
          )}
          <Button variant="primary" className={styles.button} onSelect={b.fetchCity}>
            Get the shared city
          </Button>
        </div>
      )}
    </div>
  );
};

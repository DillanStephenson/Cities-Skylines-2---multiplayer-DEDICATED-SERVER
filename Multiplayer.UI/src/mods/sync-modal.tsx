import { useValue } from "cs2/api";
import * as b from "./bindings";
import styles from "./multiplayer.module.scss";

/**
 * Full-screen box shown while the group is being synced: it sits over the game view and swallows clicks, so
 * nobody places anything while the host's save is on its way. The C# side sets the text and clears it.
 */
export const SyncModal = () => {
  const text = useValue(b.syncModal$);
  if (!text) {
    return null;
  }

  return (
    <div
      className={styles.syncBackdrop}
      onMouseDown={(e) => e.stopPropagation()}
      onMouseUp={(e) => e.stopPropagation()}
      onClick={(e) => e.stopPropagation()}
    >
      <div className={styles.syncBox}>
        <div className={styles.syncTitle}>Syncing world</div>
        <div className={styles.syncText}>{text}</div>
        <div className={styles.syncSpinner} />
      </div>
    </div>
  );
};

import { useValue } from "cs2/api";
import * as b from "./bindings";
import styles from "./multiplayer.module.scss";

interface Tag {
  id: number;
  n: string;
  x: number;
  y: number;
  off: boolean;
  c: string;
}

/** Name tags over the other players' map markers. Positions come projected from the C# side every frame. */
export const PresenceOverlay = () => {
  const json = useValue(b.presence$);
  if (!json) {
    return null;
  }

  let tags: Tag[] = [];
  try {
    tags = JSON.parse(json);
  } catch {
    return null;
  }

  return (
    <div className={styles.presenceLayer}>
      {tags.map((tag) => (
        <div
          key={tag.id}
          className={tag.off ? styles.presenceTag + " " + styles.presenceTagOff : styles.presenceTag}
          style={{ left: tag.x + "px", top: tag.y + "px", borderColor: tag.c }}
        >
          <span className={styles.presenceDot} style={{ backgroundColor: tag.c }} />
          {tag.n}
        </div>
      ))}
    </div>
  );
};

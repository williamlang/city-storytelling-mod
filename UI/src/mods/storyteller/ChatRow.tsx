import styles from "./storyteller.module.scss";
import type { ChatMessage } from "./bindings";
import { mapGoto } from "./bindings";
import { MapRefChips } from "./mapRefs";

// Single chat-history row. Styled by role: user prompts on one side,
// assistant turns on the other. Body styling lives in the shared
// storyteller.module.scss alongside the panel rules.
//
// Assistant prose renders verbatim (coordinate pairs stay as readable text);
// any "(x, y)" coordinates it mentions are surfaced beneath the message as a
// row of clickable jump-to-camera chips (GH #29). The chips live out-of-prose
// because Cohtml can't inline-flow an element inside running text — see
// MapRefChips. User prompts get no chips — the player isn't citing the map.
export function ChatRow({ msg }: { msg: ChatMessage }) {
  const roleClass = msg.role === "user" ? styles.userRow : styles.assistantRow;
  return (
    <div className={`${styles.row} ${roleClass}`}>
      <span className={styles.role}>{msg.role}</span>
      <div className={styles.body}>
        <div className={styles.text}>{msg.text}</div>
        {msg.role === "assistant" && <MapRefChips text={msg.text} onGoto={mapGoto} />}
      </div>
    </div>
  );
}

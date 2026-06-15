import styles from "./storyteller.module.scss";
import { renderTextWithMapRefs } from "./mapRefs";
import { mapGoto } from "./bindings";
import type { ChatMessage } from "./bindings";

// Single chat-history row. Styled by role: user prompts on one side,
// assistant turns on the other. Body styling lives in the shared
// storyteller.module.scss alongside the panel rules.
//
// Coordinate pairs in the prose — "(820, 1140)" — render as inline
// camera-jump links (GH #44). The `cohinline` attribute on the text block
// lets the link flow within the sentence (Cohtml otherwise drops an inline
// child onto its own full-width line); the literal coordinate text is kept,
// so the prose reads naturally. Otherwise the message renders verbatim.
const cohinline = { cohinline: "" } as Record<string, string>;

export function ChatRow({ msg }: { msg: ChatMessage }) {
  const roleClass = msg.role === "user" ? styles.userRow : styles.assistantRow;
  return (
    <div className={`${styles.row} ${roleClass}`}>
      <span className={styles.role}>{msg.role}</span>
      <div className={styles.body}>
        <div className={styles.text} {...cohinline}>
          {renderTextWithMapRefs(msg.text, mapGoto, "m")}
        </div>
      </div>
    </div>
  );
}

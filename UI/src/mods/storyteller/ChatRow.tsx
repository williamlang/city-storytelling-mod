import styles from "./storyteller.module.scss";
import type { ChatMessage } from "./bindings";

// Single chat-history row. Styled by role: user prompts on one side,
// assistant turns on the other. Body styling lives in the shared
// storyteller.module.scss alongside the panel rules.
export function ChatRow({ msg }: { msg: ChatMessage }) {
  const roleClass = msg.role === "user" ? styles.userRow : styles.assistantRow;
  return (
    <div className={`${styles.row} ${roleClass}`}>
      <span className={styles.role}>{msg.role}</span>
      <div className={styles.body}>
        <div className={styles.text}>{msg.text}</div>
      </div>
    </div>
  );
}

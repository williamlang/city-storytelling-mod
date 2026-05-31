import styles from "./storyteller.module.scss";
import type { OpenEvent } from "./bindings";

// Compact strip of open-event cards rendered above the chat/canon body.
// Each card is title + deadline; clicking opens the existing FileModal
// (the same one the canon browser uses) so the player gets the full
// event body — motivating prose, the 2-4 options with in-game actions,
// and the acceptance criteria — without leaving the panel.
//
// The list is short by design: the agent caps open events at 3-5, and
// the strip wraps to a second line if the cards don't fit. The deadline
// is rendered as an absolute month/year ("by Aug 2027") rather than a
// relative duration — we don't have the current in-world date plumbed
// into the UI, and absolute is unambiguous regardless.
export function OpenEventsInbox(props: {
  events: OpenEvent[];
  onOpen: (path: string) => void;
}) {
  if (props.events.length === 0) return null;
  return (
    <div className={styles.inbox}>
      <div className={styles.inboxLabel}>Open events</div>
      <div className={styles.inboxList}>
        {props.events.map((e) => (
          <button
            key={e.path}
            type="button"
            className={styles.inboxCard}
            onClick={() => props.onOpen(e.path)}
            title={e.title}
          >
            <span className={styles.inboxCardTitle}>{e.title}</span>
            {e.in_world_deadline && (
              <span className={styles.inboxCardDeadline}>
                by {formatDeadline(e.in_world_deadline)}
              </span>
            )}
          </button>
        ))}
      </div>
    </div>
  );
}

// Turn an ISO date string into a short "Aug 2027" label. Falls back to
// the raw string when parsing fails (some frontmatter may carry "Q3
// 2027" or other non-ISO forms — we don't want the inbox to render
// "Invalid date" in that case).
function formatDeadline(iso: string): string {
  // Expecting YYYY-MM-DD; fast path, no Date object needed.
  const m = /^(\d{4})-(\d{2})-?\d{0,2}$/.exec(iso);
  if (m) {
    const year = m[1];
    const month = parseInt(m[2], 10);
    const monthNames = [
      "Jan", "Feb", "Mar", "Apr", "May", "Jun",
      "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    if (month >= 1 && month <= 12) {
      return `${monthNames[month - 1]} ${year}`;
    }
  }
  return iso;
}

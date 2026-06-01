// Probe Coherent UI's CDP directly to inspect the live in-game DOM.
// Bypasses the chrome-devtools MCP, which can't iterate Coherent's
// partial target list ("contextIds is not iterable" error).
//
// Usage:
//   node tools/coherent-probe.mjs [expression]
//
// If `expression` is given, evaluates it in the Coherent page and prints
// the JSON-serialized result. Otherwise runs a default diagnostic that
// dumps the chat element's computed styles + a few inbox metrics.

const CDP_HTTP = "http://127.0.0.1:9444";

async function main() {
  const res = await fetch(`${CDP_HTTP}/json`);
  if (!res.ok) {
    console.error(`Coherent CDP not reachable at ${CDP_HTTP}/json (${res.status}). Is CS2 running?`);
    process.exit(1);
  }
  const targets = await res.json();
  const page = targets.find((t) => t.type === "page");
  if (!page) {
    console.error("No page target found. Targets:", JSON.stringify(targets, null, 2));
    process.exit(1);
  }

  // Coherent emits a malformed ws URL with /json/ in the path. The bare
  // /devtools/page/<id> form (without /json/) is the one that actually
  // upgrades cleanly — same pattern as standard Chrome.
  const wsUrl = page.webSocketDebuggerUrl.replace("/json/devtools/", "/devtools/");

  const ws = new WebSocket(wsUrl);
  const pending = new Map();
  let nextId = 0;

  function call(method, params) {
    return new Promise((resolve, reject) => {
      const id = ++nextId;
      pending.set(id, { resolve, reject });
      ws.send(JSON.stringify({ id, method, params: params || {} }));
    });
  }

  await new Promise((resolve, reject) => {
    ws.addEventListener("open", resolve, { once: true });
    ws.addEventListener("error", (e) => reject(new Error(`ws error: ${e.message || "unknown"}`)), { once: true });
  });

  ws.addEventListener("message", (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id == null) return; // event, not response
    const slot = pending.get(msg.id);
    if (!slot) return;
    pending.delete(msg.id);
    if (msg.error) slot.reject(new Error(`${msg.method || "?"}: ${msg.error.message}`));
    else slot.resolve(msg.result);
  });

  const expression = process.argv[2] || defaultProbe();
  try {
    const result = await call("Runtime.evaluate", {
      expression,
      returnByValue: true,
      awaitPromise: true,
    });
    if (result.exceptionDetails) {
      console.error("page-side exception:");
      console.error(JSON.stringify(result.exceptionDetails, null, 2));
      process.exit(2);
    }
    const value = result.result?.value;
    if (typeof value === "string") {
      console.log(value);
    } else {
      console.log(JSON.stringify(value, null, 2));
    }
  } finally {
    ws.close();
  }
}

function defaultProbe() {
  // Inspect chat scrolling + inbox layout. Returns one JSON blob.
  return `
    (() => {
      const find = (sel) => document.querySelector(sel);
      const findClass = (substr) => {
        for (const el of document.querySelectorAll("*")) {
          const cls = el.className && el.className.baseVal != null ? el.className.baseVal : el.className;
          if (typeof cls === "string" && cls.includes(substr)) return el;
        }
        return null;
      };
      const dump = (el, label) => {
        if (!el) return { [label]: "(not found)" };
        const cs = getComputedStyle(el);
        const rect = el.getBoundingClientRect();
        return {
          [label]: {
            class: el.className,
            rect: { x: rect.x, y: rect.y, w: rect.width, h: rect.height },
            scrollHeight: el.scrollHeight,
            clientHeight: el.clientHeight,
            offsetWidth: el.offsetWidth,
            offsetHeight: el.offsetHeight,
            overflowY: cs.overflowY,
            overflowX: cs.overflowX,
            // Pseudo-element styling: Coherent may or may not honor ::-webkit-scrollbar.
            // Compare ::-webkit-scrollbar width against what the SCSS asked for.
            scrollbarWidth: cs.scrollbarWidth,
            scrollbarColor: cs.scrollbarColor,
            backgroundColor: cs.backgroundColor,
            padding: cs.padding,
            margin: cs.margin,
            display: cs.display,
            flexGrow: cs.flexGrow,
            flexShrink: cs.flexShrink,
            flexBasis: cs.flexBasis,
            minHeight: cs.minHeight,
          },
        };
      };

      const chat = findClass("chat");
      const inbox = findClass("inbox");
      const inboxLabel = findClass("inboxLabel");
      const inboxCard = findClass("inboxCard");
      const panel = findClass("panel");
      const body = findClass("body");

      // Check whether ::-webkit-scrollbar rules were even parsed by walking
      // every stylesheet's rules and looking for scrollbar pseudo-elements.
      let scrollbarRules = 0;
      let webkitRulesSeen = [];
      for (const sheet of document.styleSheets) {
        try {
          for (const rule of sheet.cssRules || []) {
            if (rule.cssText && rule.cssText.includes("::-webkit-scrollbar")) {
              scrollbarRules++;
              webkitRulesSeen.push(rule.cssText.slice(0, 200));
            }
          }
        } catch (_) { /* cross-origin sheet */ }
      }

      return {
        userAgent: navigator.userAgent,
        viewport: { w: innerWidth, h: innerHeight },
        ...dump(panel, "panel"),
        ...dump(inbox, "inbox"),
        ...dump(inboxLabel, "inboxLabel"),
        ...dump(inboxCard, "inboxCard"),
        ...dump(body, "body"),
        ...dump(chat, "chat"),
        scrollbarRulesFound: scrollbarRules,
        webkitRulesSeen,
      };
    })()
  `;
}

main().catch((e) => {
  console.error(e.stack || e.message);
  process.exit(1);
});

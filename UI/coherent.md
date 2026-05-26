# Coherent UI quirks

CS2's in-game UI runs on Coherent Labs' **Cohtml** (current build reports `Cohtml/1.64.0.7`, V8 9.4) — a Chromium-fork-shaped runtime tuned for game-engine integration. It speaks the **Chrome DevTools Protocol** at `localhost:9444` (when the game is running) but implements a strict subset, and its CSS/layout engine diverges from Chromium in ways that bite our React UI.

This file collects what we've learned the hard way. Update whenever a new quirk costs more than 30 minutes.

## Verifying live behavior

CS2 must be running with the Ghostwriter panel open (or whichever UI element you're inspecting).

- **CDP endpoint:** `http://127.0.0.1:9444/json` (HTTP) / `ws://127.0.0.1:9444/json/devtools/page/0` (WebSocket).
- **`chrome-devtools-mcp` does NOT work** against Coherent — it calls `Browser.getContexts` during init, which Coherent doesn't implement (errors with `contextIds is not iterable`).
- **Use `.dev/cdp.mjs` instead** — a small Node 22+ script that connects directly to the WebSocket and runs `Runtime.evaluate`. Usage: `node .dev/cdp.mjs '<js expression>'`. Returns JSON.

CDP **reads** work fully (`getBoundingClientRect`, `getComputedStyle`, `querySelector`, etc.). **Writes do not** (see below).

## Coherent ignores style mutations after initial layout

Confirmed against Cohtml 1.64.0.7:

- **Inline style writes after first render are silently dropped.** `el.style.height = '700px'` stores the attribute (you can read it back via `el.style.cssText`), but `getComputedStyle().height` and `getBoundingClientRect().height` are unchanged.
- **`!important` on inline styles** doesn't help (`el.style.setProperty('height', '700px', 'important')` still ignored).
- **Dynamically-added `<style>` elements do not parse** — the `<style>` element is inserted into the DOM, but `styleEl.sheet === null`, meaning Coherent never compiled it into a stylesheet. Rules never apply.

**Practical impact:** every CSS change requires a full webpack rebuild + CS2 reload. CDP is read-only as a debugging tool. Don't try to iterate via injected styles.

**Why React inline styles still work:** styles set via React's initial render (or re-render with new props) are part of the DOM mutations Coherent processes during its reconciliation pass. Mutations from outside that pipeline (CDP, vanilla JS event handlers) are not.

## CSS engine quirks

### `flex: <n>` shorthand misparses

`flex: 1` is **NOT** equivalent to `flex: 1 1 0%` like the spec says — Coherent leaves `flex-basis: auto` instead of `0`. Nested flex containers fall through this bug spectacularly: the element starts at its content's natural height in a flex column and doesn't grow to fill, leaving visible empty space.

**Fix:** always use explicit longhand. Replace `flex: 1` with:
```scss
flex-grow: 1;
flex-shrink: 1;
flex-basis: 0;
```

### `display: grid` falls back to `display: block`

Setting `display: grid` on an element silently downgrades to `display: block` — `grid-template-rows`, `grid-template-columns`, `grid-area`, etc. are ignored entirely. The element loses its grid layout AND its flex layout, so children stack as block boxes with no growth behavior. Don't reach for grid as a flex workaround. Stick to flex.

### `min-height` on a flex-grow item is double-counted

A flex item with `flex-grow: 1` AND a non-zero `min-height` in a flex column container double-claims space: the item grows to fill the leftover, AND Coherent reserves an extra `min-height`-worth of space somewhere else in the column. The result is a gap exactly equal to the min-height value (in resolved pixels — remember rem scales with viewport) appearing at the bottom of the container, below the last child.

Specifically observed: `min-height: 80rem` (≈107px on a 1440-tall display) on a flex-growing chat box produced exactly 107–120px of empty space below the column's last child, regardless of the column's total height.

**Fix:** set `min-height: 0` on the flex-growing child. Enforce usability minimums upstream (panel-level, or via JS clamping in the resize handler) rather than via `min-height` on the flex item.

### `gap` is silently ignored on flex containers

`gap: 8rem` on a flex container computes as empty string and produces zero visual spacing between children. (Possibly works on grid containers — not tested.)

**Fix:** use `margin-top` (or `margin-left` for row containers) on children to create explicit gaps.

### `align-items` and `align-self` defaults are unreliable

Coherent's per-element flex defaults don't reliably include `stretch` on the cross-axis. Row containers' children may render shorter than the container if you don't pin them. Always set both explicitly when you want children to fill:
```scss
.parent { display: flex; flex-direction: row; align-items: stretch; }
.child  { align-self: stretch; }
```

### Root font-size scales with viewport height

`html { font-size: 0.0925926vh; }` — `1rem ≈ 0.0926% of viewport height`. On a 1440-tall display that's ≈ 1.33px per rem; on a 1080-tall display ≈ 1.0px per rem. `rem` values you write in SCSS are NOT pixel-equivalent — they're viewport-scaled.

**Implication:** `min-height: 80rem` is ~107px on a 1440-tall display, not 80px and not 1280px. Sanity-check rem-based sizing against the actual rendered rect via CDP before drawing conclusions from computed-style values.

### Coherent emits its own per-element flex defaults

CS2's global stylesheet sets `display: flex; flex-direction: row; flex-wrap: nowrap` on every block element. Standard HTML elements (`<p>`, `<h1>`, `<ul>`, `<li>`, `<blockquote>`, `<pre>`) all become flex row containers. Inline children (text segments, `<a>`, `<span>`) become flex items laid out left-to-right with no wrapping.

**Practical impact:** markdown rendered into a chat / file view will display each block on its own row but text will not wrap; phrases overflow to the right. Long URLs and code spans don't break across lines unless you explicitly override:
```scss
.markdown {
  p, h1, h2, h3, h4, h5, h6, blockquote, pre { display: block; }
  ul, ol { display: block; list-style-position: inside; }
  li { display: list-item; }
}
```

### `<span style="display: inline">` renders as block-sized

`display: inline` on a span computes correctly but Coherent's layout engine renders it as a block-width element on its own line. Use `display: inline-block` instead — Coherent flows that correctly inline. Tradeoff: long link text won't wrap mid-phrase (the link is an atomic block).

## CDP

### What works
- `Runtime.enable` + `Runtime.evaluate` (with `returnByValue: true`).
- Reading any DOM/CSS state via `Runtime.evaluate` (`document.querySelector`, `getBoundingClientRect`, `getComputedStyle`, etc.).
- `document.styleSheets` enumeration — but only counts the stylesheets Coherent loaded at init.

### What doesn't work
- `Browser.getContexts` (and anything that depends on it — e.g. chrome-devtools-mcp's page enumeration).
- Mutating layout via JS (see "style mutations" above).
- Hot-reloading new CSS rules.

# Coherent UI quirks

CS2's in-game UI runs on Coherent Labs' **Cohtml** (current build reports `Cohtml/1.64.0.7`, V8 9.4) — a Chromium-fork-shaped runtime tuned for game-engine integration. It speaks the **Chrome DevTools Protocol** at `localhost:9444` (when the game is running) but implements a strict subset, and its CSS/layout engine diverges from Chromium in ways that bite our React UI.

This file collects what we've learned the hard way, empirically, against the live runtime. Where the official **Gameface docs** explain a quirk or sanction a workaround, the relevant entry links them under **"Docs say."** Update whenever a new quirk costs more than 30 minutes.

> **Gameface / Cohtml are the same engine.** "Gameface" is the product; "Cohtml" is the runtime/UA string. The docs live under two roots — `unity-gameface` and `cpp-gameface` — but the engine is one. Both are cited below.

## Verifying live behavior

CS2 must be running with the Ghostwriter panel open (or whichever UI element you're inspecting).

- **CDP endpoint:** `http://127.0.0.1:9444/json` (HTTP) / `ws://127.0.0.1:9444/json/devtools/page/0` (WebSocket).
- **`chrome-devtools-mcp` does NOT work** against Coherent — it calls `Browser.getContexts` during init, which Coherent doesn't implement (errors with `contextIds is not iterable`). Keep using the direct-CDP script.
- **Use `.dev/cdp.mjs`** — a small Node 22+ script that connects directly to the WebSocket and runs `Runtime.evaluate`. Usage: `node .dev/cdp.mjs '<js expression>'`. It awaits a returned Promise, so async probes work. (`tools/coherent-probe.mjs` is the richer probe.)

CDP **reads** work fully (`getBoundingClientRect`, `getComputedStyle`, `querySelector`, etc.). CDP **writes also take effect** — but you must read them back on the *next* frame, not the same one (see "Layout runs once per frame" below). This makes live CSS iteration over CDP genuinely possible.

> **When probing async via CDP, always wrap the body in try/catch and resolve a result object.** An unhandled throw inside an `async` IIFE leaves the outer Promise unresolved, and `awaitPromise` then hangs the WebSocket with no output. (Cost us two silent failures testing `styleEl.sheet.cssRules`, which is `undefined` and throws on `.length`.)

## Layout runs once per frame (you can write styles — just read them next frame)

**This section previously claimed "Coherent ignores style mutations after initial layout / CDP is read-only."** That was wrong — a read-timing artifact. Re-tested against Cohtml 1.64.0.7 (GH #44):

- **Inline style writes take effect.** `el.style.opacity = '0.42'` on a real, already-laid-out element updates `getComputedStyle().opacity` to `0.42` — **but only when read on the next frame.** Read it in the same JS turn and you get the *previous* frame's value.
- **Injected `<style>` rules take effect too.** Append a `<style>` whose rule sets `height: 333px`, give it a frame, and a matching element's `getComputedStyle().height` is `333px`. The earlier "`styleEl.sheet === null`, rules never apply" claim was also a stale read.
  - Caveat: `styleEl.sheet` exists as an object, but `styleEl.sheet.cssRules` is **`undefined`** (Cohtml doesn't expose the parsed rule list to JS). The rules still apply to elements — you just can't enumerate them. Don't gate logic on `cssRules`.
- **`!important` on inline styles** behaves normally.

**Why:** *"Gameface does layout only once per frame, so styles accessed from JS will be from the previous frame."* A write + same-turn read straddles a single layout pass, so the read predates the write's layout.

**Docs say:** [Accessing computed styles](https://docs.coherent-labs.com/cpp-gameface/content_development/accessingcomputedstyles/) — read styles one frame after writing them.

**The pattern for any write-then-read (CDP or in-app JS):**
```js
el.style.height = "700px";
requestAnimationFrame(() => {
  const h = getComputedStyle(el).height; // now reflects the write
});
```

**Practical impact:**
- **Live CSS iteration over CDP is possible.** Write a style or inject a `<style>`, `requestAnimationFrame`, then read back — the change is real and visible. The full webpack-rebuild + CS2-reload loop is still the source of truth (and the only way a change *persists*), but quick "does this property even do anything here" probes no longer require a reload.
- **In-app reads after a layout-affecting change must be deferred a frame.** Any JS that mutates layout (size, content, visibility) and then measures (`getBoundingClientRect`, `getComputedStyle`, `scrollHeight`) should read inside a `requestAnimationFrame`. See the `ChatScrollIndicator` update coalescer in `StorytellerToolbar.tsx`.
- **Event-handler reads of settled geometry are safe without rAF.** `useDrag.beginDrag` / `useResize.beginResize` read `getBoundingClientRect` from a mousedown handler on an element that's been sitting still, with no layout-affecting write earlier in the frame — so the "previous frame" value *is* the current truth. No deferral needed there.

## React inline styles are also subject to once-per-frame layout

Styles set via React's render are applied during Coherent's reconciliation pass and lay out on the next frame like any other write. They work reliably for *setting* values; just don't read geometry back synchronously in the same render/effect that set it — defer the measurement a frame.

## CSS engine quirks

### `flex: <n>` shorthand misparses

`flex: 1` is **NOT** equivalent to `flex: 1 1 0%` like the spec says — Coherent leaves `flex-basis: auto` instead of `0`. Nested flex containers fall through this bug spectacularly: the element starts at its content's natural height in a flex column and doesn't grow to fill, leaving visible empty space.

**Fix:** always use explicit longhand. Replace `flex: 1` with:
```scss
flex-grow: 1;
flex-shrink: 1;
flex-basis: 0;
```

### `flex-basis: content` is unsupported

`flex-basis: content` (size the item to its content) is not implemented — it resolves as if `auto`/unset and the item doesn't size to content. Use an explicit `flex-basis` (e.g. `0` to grow from nothing, or a pixel/rem value) plus `flex-grow`/`flex-shrink`. Same family of gap as the `flex: <n>` shorthand misparse above.

### `display: grid` falls back to `display: block`

Setting `display: grid` on an element silently downgrades to `display: block` — `grid-template-rows`, `grid-template-columns`, `grid-area`, etc. are ignored entirely. The element loses its grid layout AND its flex layout, so children stack as block boxes with no growth behavior. Don't reach for grid as a flex workaround. Stick to flex.

### `min-height` on a flex-grow item is double-counted

A flex item with `flex-grow: 1` AND a non-zero `min-height` in a flex column container double-claims space: the item grows to fill the leftover, AND Coherent reserves an extra `min-height`-worth of space somewhere else in the column. The result is a gap exactly equal to the min-height value (in resolved pixels — remember rem scales with viewport) appearing at the bottom of the container, below the last child.

Specifically observed: `min-height: 80rem` (≈107px on a 1440-tall display) on a flex-growing chat box produced exactly 107–120px of empty space below the column's last child, regardless of the column's total height.

**Fix:** set `min-height: 0` on the flex-growing child. Enforce usability minimums upstream (panel-level, or via JS clamping in the resize handler) rather than via `min-height` on the flex item.

### `gap` is silently ignored on flex containers

`gap: 8rem` on a flex container computes as empty string and produces zero visual spacing between children. (Possibly works on grid containers — moot, since grid falls back to block.)

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

`display: inline` on a span computes correctly but Coherent's layout engine renders it as a block-width element on its own line. Use `display: inline-block` instead — Coherent flows that correctly inline. (For *prose with mixed inline children*, prefer `cohinline` on the block — see next.)

### Inline elements between text nodes need `cohinline`

Any element sitting **between text nodes** in running prose — `<strong>`, `<em>`, `<code>`, `<a>` — lays out **block-sized on its own full-width line**, even with `display: block` on the paragraph. Verified via CDP (GH #44): a `<strong>` mid-paragraph computed `width === the paragraph's full width`, `left: 0`, on its own row; the surrounding text broke above and below it.

`display: block` on the block fixes block-level *stacking* and text *wrapping*, but does **not** make these inline children flow. Gameface ships an official opt-in that does:

```jsx
<p cohinline="">…text <a>link</a> more text…</p>
```

CDP confirms: under `cohinline` the `<a>`/`<strong>` flows inline at its natural width and wraps with the prose (link measured width 96px mid-line, vs. 360px full-width block without it). Works with or without `display: block` on the same element.

- **Caveat (per docs):** box decorations — `background`, `border`, masks — on **child** elements are *not rendered* under `cohinline`; only on the `cohinline` block itself. CDP can't disprove this (computed style still reports the border; it's a paint-level limitation), so trust the docs. **This is why inline links and coordinate camera-jump targets are rendered as plain TEXT links, not bordered pills** (`.markdownLink` / `.mapRefLink`): a pill child would lose its border. We previously surfaced both as out-of-prose block elements (a link list + a chip row) for exactly the inline-flow reason this attribute fixes; `cohinline` let them move back inline (GH #44).
- Applied in `MarkdownLite.tsx` to every text-bearing block (`p`, `h1`–`h6`, `li`, `blockquote`) and on the chat row (`ChatRow.tsx`). This restored inline cross-reference links and inline coordinate jump targets, *and* fixed the previously-broken inline bold/italic/code in canon prose.

**Docs say:** [Inline layout](https://docs.coherent-labs.com/unity-gameface/content_development/inlinelayout/) — `cohinline` is built "for cases where flex layout fails to render styled text"; child box decorations unsupported.

### `background` declaration dropped on some elements when set via a CSS rule

On certain elements a `background` / `background-color` set through a CSS *rule* computes as transparent (the declaration is dropped), while the same shorthand renders fine elsewhere. Setting it **inline** is honored reliably. Observed on the scroll-indicator track/thumb (`StorytellerToolbar.tsx`), which set their backgrounds inline for this reason.

## Fonts & SVG

### No color-emoji glyphs

The UI font has no color-emoji glyphs — emoji render as tofu boxes in-game (they're fine in `dev:web`). Use inline SVG with explicit `width`/`height` and a hard-coded `fill` for icons/glyphs.

### `currentColor` doesn't reliably propagate into SVG

Hard-code the `fill` hex on inline SVGs rather than relying on `fill="currentColor"` inheriting the text color. The SVG support table doesn't clearly document `currentColor` propagation either way, so this stays empirical until proven otherwise.

**Docs:** [SVG support table](https://docs.coherent-labs.com/cpp-gameface/content_development/supported_features_tables/svgsupport/) — check here before assuming an SVG feature works.

## Native form controls are prototyping-grade polyfills

Cohtml's native `radio`, `checkbox`, `dropdown`/`<select>`, `list`, and `slider` are **JS polyfills intended for prototyping**, not production-grade controls. This is the documented root cause of:

- our unreliable `<input type="radio">` behavior, and
- the **outright crash on `<select>`** — a controlled `<select>` throws on `select.options.length` (`undefined`), and with no React error boundary the *entire* UI blanks.

**Build your own controls from `div`/`button`.** `PillRow` (`QuickstartWizard.tsx`) is the sanctioned pattern for radio/segmented choices. Plain `<input type="text">` and `<textarea>` are fine.

**Docs say:** [JavaScript polyfills](https://docs.coherent-labs.com/cpp-gameface/content_development/pages_guides/js_polyfills/) — native controls are polyfilled for prototyping.

## JS runtime quirks

- **Adjacent string children in a Fragment can render out of order** — collapse adjacent strings into one before handing them to React (see `MarkdownLite.renderInline`'s collapse pass).
- **Pointer Events / `setPointerCapture` are unreliable** — use mouse events (`mousedown`/`mousemove`/`mouseup`) and attach the move/up listeners at the document/window level for the drag's lifetime. Pattern in `useDrag.ts`, `useResize.ts`, `ChatScrollIndicator`.
- **`react-markdown` (remark/micromark) fails to load and takes the whole bundle down** — the UI fails to register at all. Hence the hand-rolled `MarkdownLite`.

## CDP

### What works
- `Runtime.enable` + `Runtime.evaluate` (with `returnByValue: true`, `awaitPromise: true`).
- Reading any DOM/CSS state via `Runtime.evaluate` (`document.querySelector`, `getBoundingClientRect`, `getComputedStyle`, etc.).
- **Writing styles / injecting `<style>` and reading them back on the next frame** (see "Layout runs once per frame"). Good for live probing; not a persistence mechanism (a CS2 reload reloads the real bundle).
- `document.styleSheets` enumeration — but only counts the stylesheets Coherent loaded at init, and `cssRules` on a sheet is `undefined`.

### What doesn't work
- `Browser.getContexts` (and anything depending on it — e.g. chrome-devtools-mcp's page enumeration).
- Reading a style back in the *same* JS turn you wrote it (returns the previous frame — defer a frame).
- Enumerating `styleSheet.cssRules` (always `undefined`).

## Won't-fix / not actionable for a mod

- **Official `scrollbar.js`** exists but ships in `Samples/uiresources/Scrollbar`, not exposed to mods → keep the hand-rolled `ChatScrollIndicator`. Docs: [Scrollbar](https://docs.coherent-labs.com/cpp-gameface/content_development/pages_guides/scrollbar/)
- **`chrome-devtools-mcp`** is incompatible (calls `Browser.getContexts`, which Cohtml doesn't implement) → keep the direct-CDP script (`.dev/cdp.mjs`).
- **No scrollbar chrome is rendered at all** — `::-webkit-scrollbar` pseudos and `scrollbar-color`/`scrollbar-width` are both ignored. Render scrollable regions with custom JS-driven thumbs.

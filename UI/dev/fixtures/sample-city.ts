import { bindValue } from "cs2/api";

// Sample-city seed data for the Vite dev harness. Edit this file to change
// what the storyteller panel sees on first paint — sample messages,
// command list, canon tree, run state. Refreshes on Vite hot-reload.
//
// Anything you want to render in dev belongs here. The mocked cs2/api
// bindings only know about whatever bindValue() calls have run, so just
// add another bindValue() entry to seed a new state slot.
//
// Layout mirrors what CityStoryMod.Systems.PromptUISystem would push
// from C# at runtime — same group name, same binding name, same JSON
// shape. Drift between this fixture and the C# scan logic will surface
// as bugs that only show in CS2; keep them in sync.

export function seedSampleCity() {
  bindValue("CityStoryMod", "messages", JSON.stringify([
    { role: "user", text: "/story-driven" },
    {
      role: "assistant",
      text:
        "Here are four directions Halverson Crossing could move next. " +
        "Each one engages a different faction; pick the one that feels " +
        "like the next chapter.\n\n" +
        "1. The riverfront rezoning fight\n" +
        "2. Annika's reform push on the police union\n" +
        "3. Halverson Civil's contract bid for the new transit line\n" +
        "4. The Eastside neighbourhood association's pushback on the new mill",
    },
    {
      // Exercises the clickable map-coordinate pins (GH #29) — the parens
      // become 📍 pins that fly the camera. Includes a negative pair.
      role: "assistant",
      text:
        "Halverson Civil's site office sits on the north bank by the " +
        "interchange (820, 1140). If the rezoning carries, the first " +
        "subdivision goes on the SW flats (-430, -1180) off Loon Lane.",
    },
  ]));

  bindValue("CityStoryMod", "isRunning", false);
  bindValue("CityStoryMod", "tokenSummary", "in 4823 • out 1102 • cache r12300/w800");
  bindValue("CityStoryMod", "lastError", "");

  // Quickstart wizard preview. `quickstartAvailable: true` shows the warm-
  // amber banner inside the panel + flashes the toolbar icon; click "Start"
  // to open the founding modal. Set false for a normal (bootstrapped) city.
  //
  // To preview the wizard's non-form phases, edit these and reload:
  //   isRunning: true            → the "founding your city…" progress state
  //   setupNeeded: true          → the provider prerequisite gate
  //   cartoExporting: true       → the "mapping your terrain…" gate
  //   wizardDone: '{...}'        → the result card (see shape below)
  bindValue("CityStoryMod", "quickstartAvailable", true);
  bindValue("CityStoryMod", "wizardDone", "");
  // Example result-card payload:
  // bindValue("CityStoryMod", "wizardDone", JSON.stringify({
  //   city_name: "Selkirk Falls", region: "North America",
  //   founded: "1887", premise: "A timber port reinventing itself after the mills closed.",
  // }));

  // Carto-bridge state. Seed `cartoAvailable=true` so the Refresh map button
  // renders in the harness; toggle `cartoExporting` here to preview the
  // disabled/Updating state.
  bindValue("CityStoryMod", "cartoAvailable", true);
  bindValue("CityStoryMod", "cartoExporting", false);

  bindValue("CityStoryMod", "availableCommands", JSON.stringify([
    { name: "session-start", description: "Open a session — state scan + checklist of opening tasks", order: 20 },
    { name: "story-driven", description: "Generate concrete story-driven gameplay choices with for/against framing", order: 30 },
    { name: "session-end", description: "Close a session — record what happened, propagate consequences", order: 40 },
    { name: "session-archive", description: "Compress old session files into monthly summaries", order: 50 },
  ]));

  bindValue("CityStoryMod", "canonTree", JSON.stringify({
    canon: [
      { name: "INDEX", path: "canon/INDEX.md", content:
        "# Canon index\n\n" +
        "_Maintained by the storyteller_\n\n" +
        "Major entities and a one-paragraph summary per item.\n\n" +
        "## Characters\n\n" +
        "- **Annika Bergström** — Reform-ticket mayor, 2024. Pushing transparency on city contracts.\n" +
        "- **Marcus Devereaux** — Developer with a 14-acre option on the old Conrail yard.\n\n" +
        "## Places\n\n" +
        "- **Old Halverson** — 1920s brick core, six blocks downtown.\n" +
        "- **Riverside** — Working-class neighborhood east of the rail line.\n" },
      { name: "city", path: "canon/city.md", content:
        "# Halverson Crossing\n\n" +
        "A postwar industrial town in the Great Lakes region that found rail in the 1880s, " +
        "mills in the 1920s, decline in the 1980s, and is now reinventing itself as a tech " +
        "hub for the regional logistics industry.\n\n" +
        "## Region\n\n" +
        "Northwestern Indiana, twenty miles from Lake Michigan. Flat terrain, scattered with " +
        "the remains of small lake-effect drainage creeks." },
      { name: "era", path: "canon/era.md", content:
        "# Era\n\nPresent day. 2026.\n\n" +
        "- Climate: getting warmer, but Lake Michigan still moderates summer extremes.\n" +
        "- Politics: post-Trump retrenchment, mixed local races, generally pragmatic.\n" +
        "- Economy: regional logistics boom (Indianapolis-Chicago corridor)." },
      { name: "playthrough-premise", path: "canon/playthrough-premise.md", content:
        "_Halverson Crossing survives the postwar decline by reinventing itself as an " +
        "inland logistics + tech hub, but the old industrial families don't go quietly._" },
      { name: "tone", path: "canon/tone.md", content:
        "# Tone\n\nGrounded realism. Closer to *The Wire* than *Sim City*. " +
        "Systems, people, second-order consequences." },
    ],
    characters: [
      { name: "annika-bergstrom", path: "characters/annika-bergstrom.md", content:
        "---\n" +
        "name: Annika Bergström\n" +
        "role: Mayor (reform ticket, 2024–)\n" +
        "age: 51\n" +
        "status: active\n" +
        "agenda: Reform city procurement; break Halverson Civil's stranglehold on transit contracts\n" +
        "---\n\n" +
        "## Background\n\n" +
        "Annika ran for mayor in 2024 on a transparency platform after the *Tribune* broke " +
        "the story about the 2023 Conrail yard option. She won by 4 points.\n\n" +
        "## Current moves\n\n" +
        "- Pushing the council to adopt sealed-bid rules for contracts over $250k.\n" +
        "- Quietly building a relationship with the regional FBI office on procurement fraud." },
      { name: "marcus-devereaux", path: "characters/marcus-devereaux.md", content:
        "---\n" +
        "name: Marcus Devereaux\n" +
        "role: Developer (Halverson Civil)\n" +
        "age: 62\n" +
        "status: active\n" +
        "agenda: Get the Conrail yard rezoned for mixed-use; secure the transit-line contract\n" +
        "---\n\n" +
        "## Background\n\n" +
        "Third-generation Halverson Civil. Inherited the 14-acre Conrail option from his " +
        "father in 2019. Has quietly assembled support from two of five councilors over a " +
        "long-running set of dinners at the country club.\n\n" +
        "## Threats\n\n" +
        "Annika's procurement reform. The Eastside neighbourhood association." },

      // -- Layout test fixtures: long prose + internal cross-links --
      // patricia-kovach exercises link-followed-by-text (the "space-eating"
      // bug), same-dir relative links (magnus-lindgren.md), cross-dir links
      // (../events/...), and content long enough to force the body to
      // scroll inside the modal's max-height. magnus-lindgren and
      // erik-lindgren exist as link targets so the path-resolution code
      // path can be exercised in the harness too.
      { name: "patricia-kovach", path: "characters/patricia-kovach.md", content:
        "---\n" +
        "name: Patricia Kovach\n" +
        "role: Selkirk Co-op board member\n" +
        "age: 58\n" +
        "status: active\n" +
        "agenda: Hold the line on rural electric rates; protect her father's legacy\n" +
        "---\n\n" +
        "She knew [Magnus Lindgren](magnus-lindgren.md) the way everyone in the panhandle's " +
        "small-town infrastructure community knew Magnus: as a name from the mill-and-co-op " +
        "era, a man who had served on Selkirk's member board for one term in 1979 and had " +
        "spoken at exactly one annual meeting in fifty years. He had voted against a proposed " +
        "rate increase. The increase had passed anyway. Magnus had been polite about it. Pat " +
        "remembered him from her father's stories.\n\n" +
        "She met [Erik](erik-lindgren.md) for the first time on May 28, 2026 — see " +
        "[the Selkirk meeting](../events/2026-05-28-selkirk-meeting.md). She liked him; " +
        "the project gets a quiet ally as long as Erik plays it straight.\n\n" +
        "On July 8, 2026, Erik called her from his kitchen before filing the [Crossing " +
        "Ridge wind permit](../events/2026-07-08-permit-filing.md). She told him the " +
        "co-op's lawyers would not block it. That was as close to a public endorsement as " +
        "she has ever given anyone outside her family.\n\n" +
        "## Long paragraph to test wrapping\n\n" +
        "Patricia drives a 2017 Subaru Outback with 198,000 miles on it. She refuses to " +
        "replace it because the new ones are bigger and she does not like bigger cars. " +
        "She lives in a 1948 farmhouse on twelve acres west of Bonners Ferry that she " +
        "inherited from her father in 2003. The barn is full of equipment her husband " +
        "Reidar used before he died in 2019. She has not sold any of it. She tells people " +
        "she will, eventually. She will not. The neighbours know this and have stopped " +
        "asking. The barn is part of the property the way the kitchen is part of the house." },

      { name: "magnus-lindgren", path: "characters/magnus-lindgren.md", content:
        "---\n" +
        "name: Magnus Lindgren\n" +
        "role: Millworker, co-op board (1979–80)\n" +
        "age: 91\n" +
        "status: deceased (2025-10-08)\n" +
        "---\n\n" +
        "Norwegian-American GI, returned from the Pacific in 1945. Worked the green chain " +
        "at Halverson Lumber from 1946 until the closure in 1989. Married Astrid in 1948. " +
        "Father of [Erik](erik-lindgren.md); stepfather to [Bjorn](bjorn-lindgren.md), " +
        "Astrid's son from her first marriage.\n\n" +
        "Served one term on the Selkirk Co-op member board in 1979. Voted against a rate " +
        "increase. The increase passed anyway. He was polite about it." },

      { name: "erik-lindgren", path: "characters/erik-lindgren.md", content:
        "---\n" +
        "name: Erik Lindgren\n" +
        "role: Engineer; mill-restart applicant\n" +
        "age: 64\n" +
        "status: active\n" +
        "---\n\n" +
        "Son of [Magnus](magnus-lindgren.md) and Astrid. Returned to Halverson Crossing " +
        "in 2024 after thirty years in Spokane. Filed the [mill-restart application]" +
        "(../events/2026-07-28-mill-restart-decision.md) on July 28, 2026." },

      { name: "bjorn-lindgren", path: "characters/bjorn-lindgren.md", content:
        "---\nname: Bjorn Lindgren\nrole: Millworker\nstatus: deceased (1974)\n---\n\n" +
        "Astrid's son from her first marriage; raised by [Magnus](magnus-lindgren.md) " +
        "as his own from age 13." },
    ],
    companies: [
      { name: "halverson-civil", path: "companies/halverson-civil.md", content:
        "---\n" +
        "name: Halverson Civil\n" +
        "sector: construction, real estate\n" +
        "founded: 1924\n" +
        "key_people: [marcus-devereaux]\n" +
        "---\n\n" +
        "Third-generation family construction firm. Built half the public works in town " +
        "and likely overcharged for two-thirds of them. Currently bidding on the new " +
        "transit line." },
    ],
    places: [
      { name: "downtown", path: "places/downtown.md", content:
        "---\nname: Downtown / Old Halverson\ntype: neighborhood\nstatus: existing\n---\n\n" +
        "Six blocks of 1920s brick. About half the storefronts are filled; rest have " +
        "been waiting for a tenant since the 2008 downturn. The county courthouse, two " +
        "banks, and three bars anchor the core." },
      { name: "riverside", path: "places/riverside.md", content:
        "---\nname: Riverside\ntype: neighborhood\nstatus: existing\n---\n\n" +
        "Working-class neighborhood east of the rail line. Built out 1948–1962 as " +
        "housing for the mill workers; many of the original residents' grandchildren " +
        "still live there." },
    ],
    events: [
      { name: "2024-11-05-mayoral-election", path: "events/2024-11-05-mayoral-election.md", content:
        "---\ntitle: 2024 Mayoral Election\ndate: 2024-11-05\ntype: election\n---\n\n" +
        "Annika Bergström defeats two-term incumbent **Henrik Lassen** by 4.2 points. " +
        "Margin: 1,847 votes." },
      { name: "2026-05-28-selkirk-meeting", path: "events/2026-05-28-selkirk-meeting.md", content:
        "---\ntitle: Selkirk co-op board meeting\ndate: 2026-05-28\n---\n\n" +
        "First meeting where Erik appeared as the wind project applicant. " +
        "[Patricia](../characters/patricia-kovach.md) spoke briefly in his favor." },
      { name: "2026-07-08-permit-filing", path: "events/2026-07-08-permit-filing.md", content:
        "---\ntitle: Crossing Ridge wind permit filed\ndate: 2026-07-08\n---\n\n" +
        "[Erik](../characters/erik-lindgren.md) filed the formal permit for the Crossing " +
        "Ridge turbine cluster. The Selkirk Co-op declined to oppose." },
      { name: "2026-07-28-mill-restart-decision", path: "events/2026-07-28-mill-restart-decision.md", content:
        "---\ntitle: Mill-restart application filed\ndate: 2026-07-28\n---\n\n" +
        "[Erik Lindgren](../characters/erik-lindgren.md) filed the application to restart " +
        "the Halverson Lumber green chain on July 28, 2026. The application sat on the " +
        "county planning desk for eleven days before anyone read it." },
    ],
    sessions: [],
    stories: [],
  }));

  // Open-events inbox seed — these paths must match entries in canonTree's
  // events array above so clicking a card resolves through flatCanon into
  // the FileModal. Sorted ascending by deadline.
  bindValue("CityStoryMod", "openEvents", JSON.stringify([
    {
      path: "events/2026-07-28-mill-restart-decision.md",
      title: "Mill-restart application filed",
      date: "2026-07-28",
      in_world_deadline: "2027-03-01",
    },
    {
      path: "events/2026-05-28-selkirk-meeting.md",
      title: "Selkirk Co-op rate hike on the agenda",
      date: "2026-05-28",
      in_world_deadline: "2027-08-15",
    },
  ]));
}

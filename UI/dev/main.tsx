import React from "react";
import { createRoot } from "react-dom/client";
import { bindValue } from "cs2/api";
import { StorytellerToolbar } from "../src/mods/promptWindow/PromptWindow";

// Seed mock bindings BEFORE rendering so the component sees realistic
// initial state. Each call also registers the binding so production
// imports of `availableCommandsBinding` etc. reuse the same instance.

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
]));

bindValue("CityStoryMod", "isRunning", false);
bindValue("CityStoryMod", "tokenSummary", "in 4823 • out 1102 • cache r12300/w800");
bindValue("CityStoryMod", "lastError", "");

bindValue("CityStoryMod", "availableCommands", JSON.stringify([
  { name: "session-start", description: "Open a session — state scan + checklist of opening tasks", order: 20 },
  { name: "story-driven", description: "Generate concrete story-driven gameplay choices with for/against framing", order: 30 },
  { name: "session-end", description: "Close a session — record what happened, propagate consequences", order: 40 },
  { name: "session-archive", description: "Compress old session files into monthly summaries", order: 50 },
]));

bindValue("CityStoryMod", "canonTree", JSON.stringify({
  canon: [
    { name: "INDEX", path: "canon/INDEX.md", content: "# Canon index\n\n_Maintained by the storyteller_\n\nMajor entities and a one-paragraph summary per item.\n\n## Characters\n\n- **Annika Bergström** — Reform-ticket mayor, 2024. Pushing transparency on city contracts.\n- **Marcus Devereaux** — Developer with a 14-acre option on the old Conrail yard.\n\n## Places\n\n- **Old Halverson** — 1920s brick core, six blocks downtown.\n- **Riverside** — Working-class neighborhood east of the rail line.\n" },
    { name: "city", path: "canon/city.md", content: "# Halverson Crossing\n\nA postwar industrial town in the Great Lakes region that found rail in the 1880s, mills in the 1920s, decline in the 1980s, and is now reinventing itself as a tech hub for the regional logistics industry.\n\n## Region\n\nNorthwestern Indiana, twenty miles from Lake Michigan. Flat terrain, scattered with the remains of small lake-effect drainage creeks." },
    { name: "era", path: "canon/era.md", content: "# Era\n\nPresent day. 2026.\n\n- Climate: getting warmer, but Lake Michigan still moderates summer extremes.\n- Politics: post-Trump retrenchment, mixed local races, generally pragmatic.\n- Economy: regional logistics boom (Indianapolis-Chicago corridor)." },
    { name: "playthrough-premise", path: "canon/playthrough-premise.md", content: "_Halverson Crossing survives the postwar decline by reinventing itself as an inland logistics + tech hub, but the old industrial families don't go quietly._" },
    { name: "tone", path: "canon/tone.md", content: "# Tone\n\nGrounded realism. Closer to *The Wire* than *Sim City*. Systems, people, second-order consequences." },
  ],
  characters: [
    { name: "annika-bergstrom", path: "characters/annika-bergstrom.md", content: "---\nname: Annika Bergström\nrole: Mayor (reform ticket, 2024–)\nage: 51\nstatus: active\nagenda: Reform city procurement; break Halverson Civil's stranglehold on transit contracts\n---\n\n## Background\n\nAnnika ran for mayor in 2024 on a transparency platform after the *Tribune* broke the story about the 2023 Conrail yard option. She won by 4 points.\n\n## Current moves\n\n- Pushing the council to adopt sealed-bid rules for contracts over $250k.\n- Quietly building a relationship with the regional FBI office on procurement fraud — see secret #3." },
    { name: "marcus-devereaux", path: "characters/marcus-devereaux.md", content: "---\nname: Marcus Devereaux\nrole: Developer (Halverson Civil)\nage: 62\nstatus: active\nagenda: Get the Conrail yard rezoned for mixed-use; secure the transit-line contract\n---\n\n## Background\n\nThird-generation Halverson Civil. Inherited the 14-acre Conrail option from his father in 2019. Has quietly assembled support from two of five councilors over a long-running set of dinners at the country club.\n\n## Threats\n\nAnnika's procurement reform. The Eastside neighbourhood association." },
  ],
  companies: [
    { name: "halverson-civil", path: "companies/halverson-civil.md", content: "---\nname: Halverson Civil\nsector: construction, real estate\nfounded: 1924\nkey_people: [marcus-devereaux]\n---\n\nThird-generation family construction firm. Built half the public works in town and likely overcharged for two-thirds of them. Currently bidding on the new transit line." },
  ],
  places: [
    { name: "downtown", path: "places/downtown.md", content: "---\nname: Downtown / Old Halverson\ntype: neighborhood\nstatus: existing\n---\n\nSix blocks of 1920s brick. About half the storefronts are filled; rest have been waiting for a tenant since the 2008 downturn. The county courthouse, two banks, and three bars anchor the core." },
    { name: "riverside", path: "places/riverside.md", content: "---\nname: Riverside\ntype: neighborhood\nstatus: existing\n---\n\nWorking-class neighborhood east of the rail line. Built out 1948–1962 as housing for the mill workers; many of the original residents' grandchildren still live there." },
  ],
  events: [
    { name: "2024-11-05-mayoral-election", path: "events/2024-11-05-mayoral-election.md", content: "---\ntitle: 2024 Mayoral Election\ndate: 2024-11-05\ntype: election\n---\n\nAnnika Bergström defeats two-term incumbent **Henrik Lassen** by 4.2 points. Margin: 1,847 votes." },
  ],
  sessions: [],
  stories: [],
}));

const root = createRoot(document.getElementById("root")!);
root.render(
  <React.StrictMode>
    <StorytellerToolbar />
  </React.StrictMode>
);

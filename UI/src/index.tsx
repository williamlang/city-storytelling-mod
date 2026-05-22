import { ModRegistrar } from "cs2/modding";
import mod from "../mod.json";
import { StorytellerToolbar } from "mods/promptWindow/PromptWindow";

// Mount our toolbar entry in the top-left icon row alongside other tool mods
// (Zoning Toolkit, etc.). GameTopLeft is one of CS2's append hook targets;
// the icon click toggles the panel rendered as a sibling in the same slot.
//
// To explore CS2's UI module registry for other injection points, launch the
// game with -uiDeveloperMode and inspect at localhost:9444.
const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", StorytellerToolbar);
  console.log(`${mod.id} UI module registered.`);
};

export default register;

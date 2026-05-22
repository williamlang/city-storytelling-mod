import { ModRegistrar } from "cs2/modding";
import mod from "../mod.json";
import { PromptWindow } from "mods/promptWindow/PromptWindow";

// Single registration: append the prompt window to the in-game UI root. CS2
// renders this as a sibling of the vanilla game UI; layout is controlled by
// the component's own CSS (fixed-position by default).
//
// To find injection points in CS2's own UI tree, launch the game with the
// -uiDeveloperMode launch option and inspect the registry at localhost:9444.
const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("Game", PromptWindow);
  console.log(`${mod.id} UI module registered.`);
};

export default register;

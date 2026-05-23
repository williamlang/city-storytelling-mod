// cs2/modding's ModRegistrar type isn't actually used by the dev harness —
// we import StorytellerToolbar directly and render it, bypassing the
// module-registry entry path. Stubbed so existing imports type-check.

export type ModRegistrar = any;
export const findModule = () => [];
export const getModule = () => null;

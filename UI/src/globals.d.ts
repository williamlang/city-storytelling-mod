// Globals injected by the build system. webpack's DefinePlugin (and
// Vite's `define` config in the dev harness) replaces these at build
// time so the running bundle reports when it was compiled.
//
// Add a sanity-check display somewhere visible (currently the
// storyteller panel header) so we can rule out stale-bundle bugs at
// a glance.

declare const __BUILD_TIME__: string;

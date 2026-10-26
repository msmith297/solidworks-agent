// Lets the checks load panel modules that import their siblings without extensions (vite resolves those; node
// needs the .ts). Imported first by checks that load such modules with a dynamic import. Not a check itself.
import { registerHooks } from "node:module";

// The panel's sources import without extensions (vite resolves them); node needs the .ts.
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (/^\.\.?\//.test(specifier) && !/\.[cm]?[jt]sx?$/.test(specifier)) {
      for (const ext of [".ts", ".tsx", "/index.ts"]) {
        try {
          return nextResolve(specifier + ext, context);
        } catch {
          /* next */
        }
      }
    }
    return nextResolve(specifier, context);
  },
});

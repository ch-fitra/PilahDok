import { defineConfig } from "vitest/config";
import path from "node:path";

export default defineConfig({
  test: { environment: "jsdom", globals: true, include: ["lib/**/*.test.ts"] },
  resolve: { alias: { "@": path.resolve(import.meta.dirname, ".") } },
});

import { fileURLToPath, URL } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@contracts": fileURLToPath(new URL("../packages/contracts/src/generated.ts", import.meta.url)),
    },
  },
  // Tauri : port fixe en développement, aucune ressource externe.
  server: { port: 1420, strictPort: true },
  clearScreen: false,
  build: { target: "es2022", sourcemap: false },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    restoreMocks: true,
  },
});

import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { App } from "./App";
import { EngineProvider } from "./engine/EngineContext";
import { createTauriTransport } from "./engine/transport";
import "./styles.css";

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } } });

const root = document.getElementById("root");
if (!root) throw new Error("Élément #root absent");

void createTauriTransport().then((transport) => {
  createRoot(root).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <EngineProvider transport={transport}>
          <App />
        </EngineProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
});

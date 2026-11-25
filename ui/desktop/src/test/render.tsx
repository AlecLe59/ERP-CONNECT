import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { EngineProvider } from "../engine/EngineContext";
import type { EngineTransport } from "../engine/transport";

export function renderWithEngine(ui: ReactElement, transport: EngineTransport, retryMs = 50) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <EngineProvider transport={transport} retryMs={retryMs}>{ui}</EngineProvider>
    </QueryClientProvider>,
  );
}

import { describe, expect, it } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { Dashboard } from "./Dashboard";
import { ALL_PERMISSIONS, FakeEngine, STATUS } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";

describe("Dashboard", () => {
  it("affiche l'état renvoyé par le moteur et l'utilisateur Windows reconnu", async () => {
    renderWithEngine(<Dashboard />, new FakeEngine(ALL_PERMISSIONS));
    expect(await screen.findByTestId("engine-state")).toHaveTextContent("En fonctionnement");
    expect(screen.getByText(/PC\\alice/)).toBeInTheDocument();
    expect(screen.getByText(/audit : INDISPONIBLE/)).toBeInTheDocument();
    expect(screen.getByText(/1 h 2 min/)).toBeInTheDocument();
    expect(screen.getByText(/clé v1/)).toBeInTheDocument();
  });

  it("se rafraîchit sur engine.statusChanged", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    renderWithEngine(<Dashboard />, engine);
    await screen.findByTestId("engine-state");
    engine.handlers["system.getStatus"] = () => ({ ...STATUS, state: "degraded" });
    engine.emit({ jsonrpc: "2.0", method: "engine.statusChanged", params: { state: "degraded", at: "x" } });
    await waitFor(() => expect(screen.getByTestId("engine-state")).toHaveTextContent("Mode dégradé"));
  });
});

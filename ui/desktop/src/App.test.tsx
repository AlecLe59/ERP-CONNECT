import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "./App";
import { ALL_PERMISSIONS, FakeEngine, OPERATOR_PERMISSIONS } from "./test/fakeEngine";
import { renderWithEngine } from "./test/render";

describe("App", () => {
  it("affiche les 11 menus pour un administrateur", async () => {
    renderWithEngine(<App />, new FakeEngine(ALL_PERMISSIONS));
    const nav = await screen.findByRole("navigation", { name: "Menu principal" });
    await waitFor(() => expect(within(nav).getAllByRole("button")).toHaveLength(11));
  });

  it("restreint l'interface d'un opérateur", async () => {
    renderWithEngine(<App />, new FakeEngine(OPERATOR_PERMISSIONS, ["operator"]));
    const nav = await screen.findByRole("navigation", { name: "Menu principal" });
    await waitFor(() => expect(within(nav).getByText("Tableau de bord")).toBeInTheDocument());
    expect(within(nav).queryByText("Utilisateurs et droits")).toBeNull();
    expect(within(nav).queryByText("Journal d'audit")).toBeNull();
    expect(within(nav).queryByText("Licence")).toBeNull();
    expect(within(nav).getByText("Paramètres")).toBeInTheDocument();
  });

  it("tous les menus ouvrent un écran développé", async () => {
    renderWithEngine(<App />, new FakeEngine(ALL_PERMISSIONS));
    const nav = await screen.findByRole("navigation", { name: "Menu principal" });
    await waitFor(() => expect(within(nav).getAllByRole("button")).toHaveLength(11));
    for (const button of within(nav).getAllByRole("button")) {
      await userEvent.click(button);
      expect(screen.queryByText(/Disponible en Phase/)).toBeNull();
    }
    expect(screen.getByRole("main")).not.toBeEmptyDOMElement();
  });

  it("signale le moteur indisponible puis se reconnecte seul", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.down = true;
    renderWithEngine(<App />, engine, 30);
    expect(await screen.findByText(/Moteur indisponible/)).toBeInTheDocument();
    engine.down = false;
    await waitFor(() => expect(screen.queryByText(/Moteur indisponible/)).toBeNull(), { timeout: 2000 });
    expect(await screen.findByTestId("engine-state")).toHaveTextContent("En fonctionnement");
  });

  it("refuse l'accès à un compte sans rôle", async () => {
    const engine = new FakeEngine([]);
    engine.handlers["session.hello"] = () => {
      throw { code: -32002, message: "Aucun rôle.", data: { code: "FORBIDDEN" } };
    };
    renderWithEngine(<App />, engine);
    expect(await screen.findByText(/Accès refusé/)).toBeInTheDocument();
  });

  it("demande une mise à jour si la version du protocole est incompatible", async () => {
    const engine = new FakeEngine([]);
    engine.handlers["session.hello"] = () => {
      throw { code: -32006, message: "Version incompatible", data: { code: "PROTOCOL_VERSION_MISMATCH" } };
    };
    renderWithEngine(<App />, engine);
    expect(await screen.findByText(/mettez à jour l'application/)).toBeInTheDocument();
  });

  it("rouvre la session si le service a redémarré", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    let first = true;
    engine.handlers["system.getInfo"] = () => {
      if (first) {
        first = false;
        throw { code: -32001, message: "session.hello doit être appelée en premier.", data: { code: "UNAUTHENTICATED" } };
      }
      return { installationId: "inst-2", machineName: "SRV01", serviceAccount: "NT SERVICE\\ERPConnectEngine", dataDirectory: "d", logDirectory: "l", osDescription: "w", installedAt: "2026-11-19T10:00:00Z" };
    };
    renderWithEngine(<App />, engine);
    expect(await screen.findByText(/inst-2/)).toBeInTheDocument();
    expect(engine.calls.filter((c) => c.method === "session.hello").length).toBeGreaterThanOrEqual(2);
  });
});

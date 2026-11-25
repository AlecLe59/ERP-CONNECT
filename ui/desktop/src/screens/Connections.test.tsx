import { describe, expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Connections } from "./Connections";
import { ALL_PERMISSIONS, FakeEngine, OPERATOR_PERMISSIONS } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";
import { API, CONNECTORS, LOCAL } from "../test/phase3Fixtures";

describe("Connections", () => {
  it("crée une connexion REST : le jeton part dans « secrets », jamais dans les paramètres", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.handlers["connectors.list"] = () => CONNECTORS;
    engine.handlers["connections.list"] = () => ({ connections: [] });
    engine.handlers["connections.save"] = (p) => ({ ...API, ...(p as object) });
    renderWithEngine(<Connections />, engine);
    await userEvent.selectOptions(await screen.findByLabelText("Type"), "rest");
    await userEvent.click(screen.getByRole("button", { name: "Nouvelle connexion" }));
    await userEvent.type(screen.getByLabelText("Nom *"), "ERP");
    await userEvent.type(screen.getByLabelText("URL de base *"), "https://erp.example/");
    await userEvent.selectOptions(screen.getByLabelText("Authentification"), "bearer");
    await userEvent.type(screen.getByLabelText("Jeton (bearer)"), "jeton-secret");
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    const params = engine.calls.find((c) => c.method === "connections.save")!.params as Record<string, unknown>;
    expect(params.secrets).toEqual([{ key: "token", value: "jeton-secret" }]);
    expect(JSON.stringify(params.settings)).not.toContain("jeton-secret");
    expect(params.expectedVersion).toBe(0);
  });

  it("n'affiche jamais un secret enregistré et le conserve si le champ reste vide", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.handlers["connectors.list"] = () => CONNECTORS;
    engine.handlers["connections.list"] = () => ({ connections: [API] });
    engine.handlers["connections.save"] = () => API;
    renderWithEngine(<Connections />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Modifier" }));
    const token = screen.getByLabelText("Jeton (bearer)");
    expect(token).toHaveValue("");
    expect(token).toHaveAttribute("placeholder", expect.stringContaining("laisser vide pour conserver"));
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    const params = engine.calls.find((c) => c.method === "connections.save")!.params as Record<string, unknown>;
    expect(params.secrets).toEqual([]);
    expect(params.clearSecrets).toEqual([]);
    expect(params.expectedVersion).toBe(3);
  });

  it("teste une connexion et affiche le résultat", async () => {
    const engine = new FakeEngine(OPERATOR_PERMISSIONS.concat("connections.use"));
    engine.handlers["connectors.list"] = () => CONNECTORS;
    engine.handlers["connections.list"] = () => ({ connections: [LOCAL] });
    engine.handlers["connections.test"] = () => ({ success: false, message: "Dossier introuvable", durationMs: 3 });
    renderWithEngine(<Connections />, engine);
    const row = (await screen.findByText("Entrées")).closest("tr")!;
    expect(within(row).queryByRole("button", { name: "Modifier" })).toBeNull();
    await userEvent.click(within(row).getByRole("button", { name: "Tester" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Échec : Dossier introuvable");
  });

  it("affiche les erreurs de champ renvoyées par le moteur", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.handlers["connectors.list"] = () => CONNECTORS;
    engine.handlers["connections.list"] = () => ({ connections: [] });
    engine.handlers["connections.save"] = () => {
      throw { code: -32005, message: "Connexion invalide.", data: { code: "VALIDATION_FAILED", fields: [{ field: "rootPath", message: "Obligatoire." }] } };
    };
    renderWithEngine(<Connections />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Nouvelle connexion" }));
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    expect(await screen.findByText("rootPath : Obligatoire.")).toBeInTheDocument();
  });
});

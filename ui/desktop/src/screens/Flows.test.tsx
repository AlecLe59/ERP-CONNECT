import { describe, expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { FlowSummaryDto } from "@contracts";
import { Flows } from "./Flows";
import { ALL_PERMISSIONS, FakeEngine, OPERATOR_PERMISSIONS } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";
import { API, CONNECTORS, LOCAL, RUN } from "../test/phase3Fixtures";

const FLOW: FlowSummaryDto = { id: "clients", name: "Clients", description: "", draftVersion: 2, publishedVersion: 1, updatedAt: "2026-11-19T10:00:00Z", updatedBy: "PC\\alice" };

function engineWithFlows(permissions: string[]) {
  const engine = new FakeEngine(permissions);
  engine.handlers["flows.list"] = () => ({ flows: [FLOW] });
  engine.handlers["connections.list"] = () => ({ connections: [LOCAL, API] });
  engine.handlers["connectors.list"] = () => CONNECTORS;
  engine.handlers["runs.get"] = () => ({ run: RUN, rejects: [], logs: [] });
  return engine;
}

describe("Flows", () => {
  it("crée un flux fichier CSV vers API avec correspondance explicite", async () => {
    const engine = engineWithFlows(ALL_PERMISSIONS);
    engine.handlers["flows.save"] = () => ({ ...FLOW, id: "nouveau", draftVersion: 1, publishedVersion: null });
    renderWithEngine(<Flows />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Nouveau flux" }));
    await userEvent.type(screen.getByLabelText("Nom *"), "Import clients");
    const [source, , target] = screen.getAllByRole("group");
    await userEvent.selectOptions(within(source!).getByLabelText("Connexion"), "entrees");
    await userEvent.type(within(source!).getByLabelText(/Fichiers/), "in/*.csv");
    await userEvent.selectOptions(within(target!).getByLabelText("Connexion"), "erp");
    await userEvent.type(within(target!).getByLabelText("Chemin d'API"), "/clients");
    await userEvent.selectOptions(screen.getByLabelText("Mode"), "explicit");
    await userEvent.click(screen.getByRole("button", { name: "Ajouter un champ" }));
    await userEvent.type(screen.getByLabelText("Champ cible 1"), "code");
    await userEvent.type(screen.getByLabelText("Expression 1"), "id");
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    const p = engine.calls.find((c) => c.method === "flows.save")!.params as { name: string; definition: Record<string, unknown>; expectedDraftVersion: number };
    expect(p.name).toBe("Import clients");
    expect(p.expectedDraftVersion).toBe(0);
    expect(p.definition).toMatchObject({
      source: { connectionId: "entrees", resource: "in/*.csv", options: [{ key: "format", value: "csv" }] },
      target: { connectionId: "erp", resource: "/clients" },
      mapping: { mode: "explicit", fields: [{ target: "code", expression: "id" }] },
    });
  });

  it("publie la version en cours puis lance l'exécution et affiche son détail", async () => {
    const engine = engineWithFlows(ALL_PERMISSIONS);
    engine.handlers["flows.publish"] = () => ({ ...FLOW, publishedVersion: 2 });
    engine.handlers["runs.start"] = () => RUN;
    renderWithEngine(<Flows />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Publier v2" }));
    expect(engine.calls.find((c) => c.method === "flows.publish")!.params).toEqual({ id: "clients", version: 2 });
    await userEvent.click(screen.getByRole("button", { name: "Exécuter" }));
    expect(engine.calls.find((c) => c.method === "runs.start")!.params).toEqual({ flowId: "clients", useDraft: false });
    expect(await screen.findByText("Échec")).toBeInTheDocument();
  });

  it("un opérateur exécute la version publiée mais ne modifie ni ne teste de brouillon", async () => {
    renderWithEngine(<Flows />, engineWithFlows(OPERATOR_PERMISSIONS));
    expect(await screen.findByRole("button", { name: "Exécuter" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Modifier" })).toBeNull();
    expect(screen.queryByRole("button", { name: /Tester/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Nouveau flux" })).toBeNull();
  });
});

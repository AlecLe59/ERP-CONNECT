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
    const source = screen.getByRole("group", { name: "Source" });
    const target = screen.getByRole("group", { name: "Cible" });
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

  it("ajoute des blocs de transformation, les réordonne et les enregistre", async () => {
    const engine = engineWithFlows(ALL_PERMISSIONS);
    engine.handlers["flows.save"] = () => ({ ...FLOW, id: "nouveau", draftVersion: 1, publishedVersion: null });
    renderWithEngine(<Flows />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Nouveau flux" }));
    await userEvent.type(screen.getByLabelText("Nom *"), "Avec blocs");
    await userEvent.click(screen.getByRole("button", { name: "Ajouter un bloc" }));
    await userEvent.type(screen.getByLabelText("Expression du bloc 1"), "[[actif] = 'oui'");
    await userEvent.click(screen.getByRole("button", { name: "Ajouter un bloc" }));
    await userEvent.selectOptions(screen.getByLabelText("Type de bloc 2"), "validate");
    await userEvent.type(screen.getByLabelText("Expression du bloc 2"), "len([[id]) > 0");
    await userEvent.type(screen.getByLabelText("Motif de rejet 2"), "id vide");
    await userEvent.click(screen.getByRole("button", { name: "Monter le bloc 2" }));
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    const p = engine.calls.find((c) => c.method === "flows.save")!.params as { definition: { steps: unknown[] } };
    expect(p.definition.steps).toEqual([
      { kind: "validate", field: null, expression: "len([id]) > 0", target: null, message: "id vide" },
      { kind: "filter", field: null, expression: "[actif] = 'oui'", target: null, message: null },
    ]);
  });

  it("affiche l'aperçu : champs source, lignes transmises, filtrées et rejetées", async () => {
    const engine = engineWithFlows(ALL_PERMISSIONS);
    engine.handlers["flows.preview"] = () => ({
      sourceFields: ["id", "nom"],
      rows: [
        { sourceJson: '{"id":"1"}', outputJson: '{"code":"0001"}', fate: "output", reason: null },
        { sourceJson: '{"id":"2"}', outputJson: null, fate: "filtered", reason: null },
        { sourceJson: '{"id":""}', outputJson: null, fate: "rejected", reason: "id vide" },
      ],
    });
    renderWithEngine(<Flows />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Nouveau flux" }));
    const source = screen.getByRole("group", { name: "Source" });
    await userEvent.selectOptions(within(source!).getByLabelText("Connexion"), "entrees");
    await userEvent.click(screen.getByRole("button", { name: "Aperçu (20 lignes)" }));
    const panel = await screen.findByRole("region", { name: "Aperçu" });
    expect(within(panel).getByText("[nom]")).toBeInTheDocument();
    expect(within(panel).getByText('{"code":"0001"}')).toBeInTheDocument();
    expect(within(panel).getByText("Filtré")).toBeInTheDocument();
    expect(within(panel).getByText("id vide")).toBeInTheDocument();
    expect(engine.calls.find((c) => c.method === "flows.preview")!.params).toMatchObject({ limit: 20, definition: { source: { connectionId: "entrees" } } });
    expect(engine.calls.some((c) => c.method === "flows.save")).toBe(false);
  });

  it("propose l'archive ZIP pour une source fichier", async () => {
    renderWithEngine(<Flows />, engineWithFlows(ALL_PERMISSIONS));
    await userEvent.click(await screen.findByRole("button", { name: "Nouveau flux" }));
    const source = screen.getByRole("group", { name: "Source" });
    await userEvent.selectOptions(within(source!).getByLabelText("Connexion"), "entrees");
    await userEvent.selectOptions(within(source!).getByLabelText("Archive"), "zip");
    expect(within(source!).getByLabelText(/Entrées lues/)).toBeInTheDocument();
  });
});

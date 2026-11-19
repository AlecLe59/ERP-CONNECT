import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Settings } from "./Settings";
import { ALL_PERMISSIONS, FakeEngine, OPERATOR_PERMISSIONS, SETTINGS } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";

function engineWithSettings(perms: string[]) {
  const engine = new FakeEngine(perms);
  engine.handlers["settings.get"] = () => ({ settings: SETTINGS, version: 3, updatedAt: "2026-11-19T10:00:00Z", updatedBy: "PC\\bob" });
  return engine;
}

describe("Settings", () => {
  it("active les scripts, désactivés par défaut", async () => {
    const engine = engineWithSettings(ALL_PERMISSIONS);
    engine.handlers["settings.update"] = (p) => ({ settings: (p as { settings: unknown }).settings, version: 4, updatedAt: "2026-11-19T11:00:00Z", updatedBy: "PC\\alice" });
    renderWithEngine(<Settings />, engine);
    const box = await screen.findByLabelText(/Autoriser les scripts/);
    expect(box).not.toBeChecked();
    await userEvent.click(box);
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    expect(await screen.findByText(/version 4/)).toBeInTheDocument();
    const sent = engine.calls.find((c) => c.method === "settings.update")!.params as { settings: { scriptsEnabled: boolean } };
    expect(sent.settings.scriptsEnabled).toBe(true);
  });

  it("envoie la version attendue et affiche le succès", async () => {
    const engine = engineWithSettings(ALL_PERMISSIONS);
    engine.handlers["settings.update"] = (p) => ({ settings: (p as { settings: unknown }).settings, version: 4, updatedAt: "2026-11-19T11:00:00Z", updatedBy: "PC\\alice" });
    renderWithEngine(<Settings />, engine);
    const input = await screen.findByLabelText(/Journaux des exécutions en échec/);
    await userEvent.clear(input);
    await userEvent.type(input, "120");
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    expect(await screen.findByText(/version 4/)).toBeInTheDocument();
    const sent = engine.calls.find((c) => c.method === "settings.update")!.params as { expectedVersion: number; settings: { failedLogRetentionDays: number } };
    expect(sent.expectedVersion).toBe(3);
    expect(sent.settings.failedLogRetentionDays).toBe(120);
  });

  it("affiche les erreurs de validation champ par champ", async () => {
    const engine = engineWithSettings(ALL_PERMISSIONS);
    engine.handlers["settings.update"] = () => {
      throw { code: -32005, message: "Paramètres invalides.", data: { code: "VALIDATION_FAILED", fields: [{ field: "RejectRetentionDays", message: "Minimum 7 jours." }] } };
    };
    renderWithEngine(<Settings />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Enregistrer" }));
    expect(await screen.findByText("Minimum 7 jours.")).toBeInTheDocument();
  });

  it("propose de recharger en cas de modification concurrente", async () => {
    const engine = engineWithSettings(ALL_PERMISSIONS);
    engine.handlers["settings.update"] = () => {
      throw { code: -32004, message: "Conflit", data: { code: "CONFLICT" } };
    };
    renderWithEngine(<Settings />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Enregistrer" }));
    expect(await screen.findByRole("button", { name: "Recharger" })).toBeInTheDocument();
  });

  it("est en lecture seule sans settings.edit", async () => {
    renderWithEngine(<Settings />, engineWithSettings(OPERATOR_PERMISSIONS));
    expect(await screen.findByText(/Lecture seule/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Enregistrer" })).toBeNull();
  });
});

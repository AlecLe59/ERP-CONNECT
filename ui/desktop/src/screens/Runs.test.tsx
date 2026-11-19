import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { RunDetail, Runs } from "./Runs";
import { ALL_PERMISSIONS, FakeEngine, OPERATOR_PERMISSIONS } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";
import { RUN } from "../test/phase3Fixtures";

describe("Runs", () => {
  it("liste les exécutions et montre rejets et journal", async () => {
    const engine = new FakeEngine(OPERATOR_PERMISSIONS);
    engine.handlers["runs.list"] = () => ({ items: [RUN], total: 1, page: 1, pageSize: 50 });
    engine.handlers["runs.get"] = () => ({
      run: RUN,
      rejects: [{ batchNumber: 1, recordIndex: 6, stage: "write", reason: "HTTP 422 client 7 refusé", recordJson: "{\"id\":\"7\"}", at: "2026-11-19T12:00:03Z" }],
      logs: [{ at: "2026-11-19T12:00:01Z", level: "info", message: "Démarrage (version 2)." }],
    });
    renderWithEngine(<Runs />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Détail" }));
    expect(await screen.findByText("HTTP 422 client 7 refusé")).toBeInTheDocument();
    expect(screen.getByText(/Démarrage \(version 2\)/)).toBeInTheDocument();
    expect(screen.getByText("1 rejet(s) : l'exécution est en échec.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Annuler l'exécution" })).toBeNull();
  });

  it("filtre par statut et permet d'annuler une exécution en cours", async () => {
    const running = { ...RUN, status: "running", statusLabel: "En cours", endedAt: null, error: null };
    const engine = new FakeEngine(OPERATOR_PERMISSIONS);
    engine.handlers["runs.list"] = () => ({ items: [running], total: 1, page: 1, pageSize: 50 });
    engine.handlers["runs.get"] = () => ({ run: running, rejects: [], logs: [] });
    engine.handlers["runs.cancel"] = () => ({ accepted: true });
    renderWithEngine(<Runs />, engine);
    await userEvent.selectOptions(await screen.findByLabelText("Statut"), "running");
    expect(engine.calls.filter((c) => c.method === "runs.list").at(-1)!.params).toMatchObject({ status: "running", page: 1 });
    await userEvent.click(await screen.findByRole("button", { name: "Détail" }));
    await userEvent.click(await screen.findByRole("button", { name: "Annuler l'exécution" }));
    expect(engine.calls.find((c) => c.method === "runs.cancel")!.params).toEqual({ id: running.id });
  });

  it("se met à jour sur notification runs.changed", async () => {
    const engine = new FakeEngine(OPERATOR_PERMISSIONS);
    let items = [RUN];
    engine.handlers["runs.list"] = () => ({ items, total: items.length, page: 1, pageSize: 50 });
    renderWithEngine(<Runs />, engine);
    expect(await screen.findByText("Échec")).toBeInTheDocument();
    items = [{ ...RUN, id: "r2", status: "succeeded", statusLabel: "Réussi" }, RUN];
    engine.emit({ jsonrpc: "2.0", method: "runs.changed", params: { runId: "r2", flowId: "clients", status: "succeeded" } });
    expect(await screen.findByText("Réussi")).toBeInTheDocument();
  });

  it("propose la reprise d'une exécution suspendue ou en échec transitoire, pas d'un échec par rejet", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.handlers["runs.get"] = () => ({ run: { ...RUN, status: "suspended", statusLabel: "Suspendu", errorKind: null }, rejects: [], logs: [] });
    engine.handlers["runs.resume"] = () => ({ ...RUN, status: "pending" });
    const { unmount } = renderWithEngine(<RunDetail id={RUN.id} onClose={() => undefined} />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Reprendre l'exécution" }));
    expect(engine.calls.find((c) => c.method === "runs.resume")!.params).toEqual({ id: RUN.id });
    expect(await screen.findByText(/Reprise lancée/)).toBeInTheDocument();
    unmount();

    const failed = new FakeEngine(ALL_PERMISSIONS);
    failed.handlers["runs.get"] = () => ({ run: RUN, rejects: [], logs: [] });
    renderWithEngine(<RunDetail id={RUN.id} onClose={() => undefined} />, failed);
    expect(await screen.findByText("Échec")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Reprendre|Relancer/ })).toBeNull();
  });
});

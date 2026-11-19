import { describe, expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { FlowSummaryDto, ScheduleDto } from "@contracts";
import { Schedules } from "./Schedules";
import { Monitoring } from "./Monitoring";
import { ALL_PERMISSIONS, FakeEngine, OPERATOR_PERMISSIONS } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";

const FLOW: FlowSummaryDto = { id: "clients", name: "Clients", description: "", draftVersion: 2, publishedVersion: 2, updatedAt: "2026-11-19T10:00:00Z", updatedBy: "PC\\alice" };
const SCHEDULE: ScheduleDto = {
  flowId: "clients", cron: "0 6 * * 1-5", timeZone: "Europe/Paris", enabled: true, maxRetries: 3, retryDelaySeconds: 300, version: 4,
  updatedAt: "2026-11-19T10:00:00Z", updatedBy: "PC\\alice", nextRunAt: "2026-11-20T05:00:00Z", lastRunId: null, retryAttempt: 0, retryAt: null,
};

function engine(permissions: string[], schedules: ScheduleDto[] = [SCHEDULE]) {
  const e = new FakeEngine(permissions);
  e.handlers["flows.list"] = () => ({ flows: [FLOW, { ...FLOW, id: "stock", name: "Stock", publishedVersion: null }] });
  e.handlers["schedules.list"] = () => ({ schedules });
  return e;
}

describe("Schedules", () => {
  it("liste les flux avec leur planification et la prochaine exécution", async () => {
    renderWithEngine(<Schedules />, engine(ALL_PERMISSIONS));
    const row = (await screen.findByText("Clients")).closest("tr")!;
    expect(within(row).getByText("0 6 * * 1-5")).toBeInTheDocument();
    expect(within(row).getByText(/3 \(délai 300 s\)/)).toBeInTheDocument();
    const stock = screen.getByText("Stock").closest("tr")!;
    expect(within(stock).getByText("aucune")).toBeInTheDocument();
    expect(within(stock).getByText("non publié")).toBeInTheDocument();
  });

  it("modifie une planification avec la version attendue", async () => {
    const e = engine(ALL_PERMISSIONS);
    e.handlers["schedules.save"] = () => ({ ...SCHEDULE, cron: "*/15 8-18 * * *", version: 5 });
    renderWithEngine(<Schedules />, e);
    await userEvent.click(await screen.findByRole("button", { name: "Modifier" }));
    const cron = screen.getByLabelText(/Expression cron/);
    await userEvent.clear(cron);
    await userEvent.type(cron, "*/15 8-18 * * *");
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    expect(e.calls.find((c) => c.method === "schedules.save")!.params).toEqual({
      flowId: "clients", cron: "*/15 8-18 * * *", timeZone: "Europe/Paris", enabled: true, maxRetries: 3, retryDelaySeconds: 300, expectedVersion: 4,
    });
  });

  it("affiche les erreurs de validation du moteur", async () => {
    const e = engine(ALL_PERMISSIONS, []);
    e.handlers["schedules.save"] = () => {
      throw { code: -32005, message: "Expression cron invalide.", data: { code: "VALIDATION_FAILED", fields: [{ field: "cron", message: "Format attendu : minute heure…" }] } };
    };
    renderWithEngine(<Schedules />, e);
    await userEvent.click((await screen.findAllByRole("button", { name: "Planifier" }))[0]!);
    await userEvent.click(screen.getByRole("button", { name: "Enregistrer" }));
    expect(await screen.findByText(/cron : Format attendu/)).toBeInTheDocument();
  });

  it("un opérateur consulte sans modifier", async () => {
    renderWithEngine(<Schedules />, engine(OPERATOR_PERMISSIONS));
    expect(await screen.findByText("0 6 * * 1-5")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Modifier" })).toBeNull();
  });
});

describe("Monitoring", () => {
  it("affiche les alertes ouvertes et les acquitte", async () => {
    const e = new FakeEngine(ALL_PERMISSIONS);
    let acked = false;
    e.handlers["alerts.list"] = () => ({
      alerts: acked ? [] : [{ id: 7, flowId: "clients", runId: "r1", severity: "error", message: "Exécution r1 en échec : API indisponible", createdAt: "2026-11-19T10:00:00Z", acknowledgedAt: null, acknowledgedBy: null }],
    });
    e.handlers["alerts.acknowledge"] = () => {
      acked = true;
      return { acknowledged: true };
    };
    renderWithEngine(<Monitoring />, e);
    expect(await screen.findByText(/API indisponible/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Acquitter" }));
    expect(e.calls.find((c) => c.method === "alerts.acknowledge")!.params).toEqual({ id: 7 });
    expect(await screen.findByText("Aucune alerte.")).toBeInTheDocument();
    expect(e.calls.find((c) => c.method === "events.subscribe")!.params).toEqual({ topics: ["alerts.raised"] });
  });
});

import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { LicenseStatusResult } from "@contracts";
import { License } from "./License";
import { ALL_PERMISSIONS, FakeEngine } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";

const EVALUATION: LicenseStatusResult = {
  state: "evaluation", installationId: "1b9d6bcd-bbfd-4b2d", evaluationEndsAt: "2026-12-19T12:00:00Z", license: null, problem: null,
  importedAt: null, importedBy: null, canVerify: true, publishedFlows: 2,
};

const LICENSED: LicenseStatusResult = {
  ...EVALUATION, state: "licensed", evaluationEndsAt: null, importedAt: "2026-11-20T09:00:00Z", importedBy: "PC\\alice",
  license: { licenseId: "L-1", customer: "Client SA", edition: "Standard", installationId: "1b9d6bcd-bbfd-4b2d", issuedAt: "2026-11-20T00:00:00Z", expiresAt: null, features: ["scheduler"], maxPublishedFlows: 10 },
};

describe("License", () => {
  it("affiche l'identifiant d'installation et importe le fichier collé", async () => {
    const e = new FakeEngine(ALL_PERMISSIONS);
    let current = EVALUATION;
    e.handlers["license.get"] = () => current;
    e.handlers["license.import"] = () => { current = LICENSED; return LICENSED; };
    renderWithEngine(<License />, e);
    expect(await screen.findByTestId("installation-id")).toHaveTextContent("1b9d6bcd-bbfd-4b2d");
    expect(screen.getByTestId("license-state")).toHaveTextContent("Évaluation");
    await userEvent.click(screen.getByLabelText(/contenu collé/));
    await userEvent.paste('{"format":1,"payload":"x","signature":"y"}');
    await userEvent.click(screen.getByRole("button", { name: "Importer" }));
    expect(await screen.findByText("Client SA")).toBeInTheDocument();
    expect(screen.getByTestId("license-state")).toHaveTextContent("Licence active");
    expect(screen.getByText("2 / 10")).toBeInTheDocument();
    expect((e.calls.find((c) => c.method === "license.import")!.params as { content: string }).content).toContain("signature");
  });

  it("montre le refus de la licence sur le champ", async () => {
    const e = new FakeEngine(ALL_PERMISSIONS);
    e.handlers["license.get"] = () => EVALUATION;
    e.handlers["license.import"] = () => {
      throw { code: -32005, message: "Signature de licence invalide.", data: { code: "VALIDATION_FAILED", fields: [{ field: "content", message: "Signature de licence invalide." }] } };
    };
    renderWithEngine(<License />, e);
    await userEvent.click(await screen.findByLabelText(/contenu collé/));
    await userEvent.paste("{}");
    await userEvent.click(screen.getByRole("button", { name: "Importer" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Signature de licence invalide.");
  });

  it("désactive l'import quand la version n'a pas de clé d'éditeur", async () => {
    const e = new FakeEngine(ALL_PERMISSIONS);
    e.handlers["license.get"] = () => ({ ...EVALUATION, canVerify: false });
    renderWithEngine(<License />, e);
    expect(await screen.findByText(/aucune licence ne peut être importée/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Importer" })).toBeDisabled();
  });
});

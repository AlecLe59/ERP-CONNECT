import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Scripts } from "./Scripts";
import { ALL_PERMISSIONS, FakeEngine } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";

function engine() {
  const e = new FakeEngine(ALL_PERMISSIONS);
  e.handlers["scripts.limits"] = () => ({ timeoutMs: 500, memoryBytes: 33554432, maxStatements: 1000000, maxRecursion: 64, maxArraySize: 100000, maxSourceLength: 65536 });
  return e;
}

describe("Scripts", () => {
  it("affiche les limites et essaie le script sur l'exemple", async () => {
    const e = engine();
    let sent: unknown = null;
    e.handlers["scripts.test"] = (p) => {
      sent = p;
      return { fate: "output", outputJson: '{"code":"A1","total":7.5}', reason: null, elapsedMs: 3 };
    };
    renderWithEngine(<Scripts />, e);
    expect(await screen.findByTestId("script-limits")).toHaveTextContent("500 ms");
    await userEvent.click(screen.getByRole("button", { name: "Essayer" }));
    expect(await screen.findByTestId("script-fate")).toHaveTextContent("Sortie");
    expect(screen.getByTestId("script-output")).toHaveTextContent('"total": 7.5');
    expect(sent).toMatchObject({ recordJson: expect.stringContaining("A1") as string, code: expect.stringContaining("function transform") as string });
  });

  it("montre un script arrêté par les limites", async () => {
    const e = engine();
    e.handlers["scripts.test"] = () => ({ fate: "stopped", outputJson: null, reason: "Script arrêté : délai dépassé.", elapsedMs: 501 });
    renderWithEngine(<Scripts />, e);
    await userEvent.click(await screen.findByRole("button", { name: "Essayer" }));
    expect(await screen.findByTestId("script-fate")).toHaveTextContent("Arrêté");
    expect(screen.getByText(/délai dépassé/)).toBeInTheDocument();
  });

  it("affiche l'erreur de syntaxe sur le champ Script", async () => {
    const e = engine();
    e.handlers["scripts.test"] = () => {
      throw { code: -32602, message: "Paramètres invalides.", data: { code: "VALIDATION_FAILED", fields: [{ field: "code", message: "syntaxe : Unexpected end of input" }] } };
    };
    renderWithEngine(<Scripts />, e);
    await userEvent.click(await screen.findByRole("button", { name: "Essayer" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Script : syntaxe");
  });
});

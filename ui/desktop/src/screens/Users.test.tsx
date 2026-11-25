import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Users } from "./Users";
import { ALL_PERMISSIONS, FakeEngine } from "../test/fakeEngine";
import { renderWithEngine } from "../test/render";

const ADMIN_ROW = { id: 1, principalSid: "S-1-5-32-544", principalName: "BUILTIN\\Administrateurs", principalKind: "group", role: "administrator", scope: "global", createdAt: "2026-11-19T10:00:00Z", createdBy: "installation" };

describe("Users", () => {
  it("recherche un groupe Windows puis lui attribue un rôle", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    const items = [ADMIN_ROW];
    engine.handlers["users.listAssignments"] = () => ({ items });
    engine.handlers["users.resolvePrincipal"] = () => ({ found: true, sid: "S-1-5-21-9-9-9-1200", name: "CORP\\Compta", kind: "group" });
    engine.handlers["users.assignRole"] = (p) => {
      const row = { ...ADMIN_ROW, id: 2, ...(p as object) };
      items.push(row as typeof ADMIN_ROW);
      return row;
    };
    renderWithEngine(<Users />, engine);
    await userEvent.type(await screen.findByLabelText(/Compte ou groupe Windows/), "CORP\\Compta");
    await userEvent.click(screen.getByRole("button", { name: "Rechercher" }));
    await userEvent.selectOptions(await screen.findByLabelText("Rôle"), "operator");
    await userEvent.click(screen.getByRole("button", { name: "Attribuer" }));
    expect(await screen.findByText("CORP\\Compta")).toBeInTheDocument();
    expect(engine.calls.find((c) => c.method === "users.assignRole")!.params).toEqual({
      principalSid: "S-1-5-21-9-9-9-1200", principalName: "CORP\\Compta", principalKind: "group", role: "operator",
    });
  });

  it("demande confirmation avant de retirer et affiche le refus du dernier administrateur", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.handlers["users.listAssignments"] = () => ({ items: [ADMIN_ROW] });
    engine.handlers["users.revokeRole"] = () => {
      throw { code: -32004, message: "Impossible de retirer le dernier Administrateur.", data: { code: "CONFLICT", correlationId: "s-9" } };
    };
    renderWithEngine(<Users />, engine);
    await userEvent.click(await screen.findByRole("button", { name: "Retirer" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(engine.calls.some((c) => c.method === "users.revokeRole")).toBe(false);
    await userEvent.click(screen.getAllByRole("button", { name: "Retirer" }).at(-1)!);
    expect(await screen.findByText(/dernier Administrateur/)).toBeInTheDocument();
  });

  it("indique qu'aucun compte ne correspond", async () => {
    const engine = new FakeEngine(ALL_PERMISSIONS);
    engine.handlers["users.listAssignments"] = () => ({ items: [] });
    engine.handlers["users.resolvePrincipal"] = () => ({ found: false, sid: null, name: null, kind: null });
    renderWithEngine(<Users />, engine);
    await userEvent.type(await screen.findByLabelText(/Compte ou groupe Windows/), "inconnu");
    await userEvent.click(screen.getByRole("button", { name: "Rechercher" }));
    expect(await screen.findByText(/Aucun compte ou groupe/)).toBeInTheDocument();
  });
});

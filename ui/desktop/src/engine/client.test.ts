import { describe, expect, it } from "vitest";
import { EngineClient, RpcError } from "./client";
import { FakeEngine } from "../test/fakeEngine";

describe("EngineClient", () => {
  it("renvoie le résultat typé", async () => {
    const client = new EngineClient(new FakeEngine(["dashboard.read"]));
    const status = await client.call("system.getStatus");
    expect(status.state).toBe("running");
  });

  it("convertit les erreurs JSON-RPC en RpcError avec le code métier", async () => {
    const engine = new FakeEngine([]);
    engine.handlers["settings.get"] = () => {
      throw { code: -32002, message: "Permission refusée.", data: { code: "FORBIDDEN", correlationId: "abc-1" } };
    };
    const err = await new EngineClient(engine).call("settings.get").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(RpcError);
    expect((err as RpcError).isForbidden).toBe(true);
    expect((err as RpcError).correlationId).toBe("abc-1");
  });

  it("refuse un nom de méthode hors format domaine.action avant tout envoi", async () => {
    const engine = new FakeEngine([]);
    await expect(new EngineClient(engine).call("../x" as never)).rejects.toThrow("invalide");
    expect(engine.calls).toHaveLength(0);
  });
});

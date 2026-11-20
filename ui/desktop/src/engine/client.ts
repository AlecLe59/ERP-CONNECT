import { RPC_ERROR_CODES, type RpcMethodMap, type RpcMethodName } from "@contracts";
import { TransportError, type EngineTransport } from "./transport";

export class RpcError extends Error {
  readonly code: number;
  readonly errorName: string;
  readonly correlationId: string | undefined;
  readonly fields: { field: string; message: string }[];

  constructor(code: number, message: string, errorName: string, correlationId?: string, fields?: { field: string; message: string }[] | null) {
    super(message);
    this.name = "RpcError";
    this.code = code;
    this.errorName = errorName;
    this.correlationId = correlationId;
    this.fields = fields ?? [];
  }

  get isForbidden() {
    return this.code === RPC_ERROR_CODES.Forbidden;
  }
}

const METHOD_FORMAT = /^[a-z][a-zA-Z]{0,31}\.[a-z][a-zA-Z]{0,63}$/;

/** Client typé : les noms de méthodes et les types viennent du contrat généré. */
export class EngineClient {
  private readonly transport: EngineTransport;

  constructor(transport: EngineTransport) {
    this.transport = transport;
  }

  async call<M extends RpcMethodName>(method: M, params?: RpcMethodMap[M]["params"]): Promise<RpcMethodMap[M]["result"]> {
    if (!METHOD_FORMAT.test(method)) throw new Error(`Nom de méthode invalide : ${method}`);
    const response = await this.transport.call(method, params ?? null);
    if (response.error) {
      const e = response.error;
      throw new RpcError(e.code, e.message, e.data?.code ?? "PROTOCOL_ERROR", e.data?.correlationId, e.data?.fields);
    }
    if (!("result" in response)) throw new TransportError("Réponse sans résultat");
    return response.result as RpcMethodMap[M]["result"];
  }

  onNotification(listener: Parameters<EngineTransport["onNotification"]>[0]) {
    return this.transport.onNotification(listener);
  }
}

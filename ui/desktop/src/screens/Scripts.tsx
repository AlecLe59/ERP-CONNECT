import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import type { ScriptTestResult } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage, formatBytes } from "../components/Feedback";
import { RpcError } from "../engine/client";

const EXAMPLE = `// Reçoit l'enregistrement (objet JSON). Renvoyer l'objet, null pour l'écarter,
// ou lever une erreur pour le rejeter avec un motif.
function transform(record) {
  if (!record.code) throw new Error("Code obligatoire");
  record.total = Math.round(record.qte * record.pu * 100) / 100;
  return record;
}`;

const FATES: Record<string, string> = {
  output: "Sortie",
  filtered: "Écarté (null)",
  rejected: "Rejeté",
  stopped: "Arrêté (limite atteinte)",
};

/** Atelier de scripts : écrire et essayer un script isolé sur un enregistrement d'exemple, sans connexion ni écriture. */
export function Scripts() {
  const { call } = useEngine();
  const [code, setCode] = useState(EXAMPLE);
  const [sample, setSample] = useState('{ "code": "A1", "qte": 3, "pu": 2.5 }');
  const limits = useQuery({ queryKey: ["scripts.limits"], queryFn: () => call("scripts.limits") });
  const test = useMutation({ mutationFn: (): Promise<ScriptTestResult> => call("scripts.test", { code, recordJson: sample }) });
  const fields = test.error instanceof RpcError ? test.error.fields : [];
  const r = test.data;
  return (
    <section aria-labelledby="scripts-title">
      <h1 id="scripts-title">Scripts</h1>
      <p>
        Les scripts JavaScript s'exécutent dans un bac à sable : aucun accès aux fichiers, au réseau ni au système, pas d'<code>eval</code>.
        Ajoutez-les à un flux par un bloc « Script JavaScript ».
      </p>
      {limits.data && (
        <p data-testid="script-limits">
          Limites par enregistrement : {limits.data.timeoutMs} ms, {formatBytes(limits.data.memoryBytes)} de mémoire,
          {" "}{limits.data.maxStatements.toLocaleString("fr-FR")} instructions, récursion {limits.data.maxRecursion}.
        </p>
      )}
      <label>
        Script
        <textarea className="code" rows={14} spellCheck={false} value={code} onChange={(e) => setCode(e.target.value)} />
      </label>
      <label>
        Enregistrement d'exemple (JSON)
        <textarea className="code" rows={5} spellCheck={false} value={sample} onChange={(e) => setSample(e.target.value)} />
      </label>
      <button type="button" onClick={() => test.mutate()} disabled={test.isPending}>Essayer</button>
      {fields.length > 0 ? (
        <ul role="alert" className="error">
          {fields.map((f) => <li key={f.field + f.message}>{f.field === "code" ? "Script" : "Exemple"} : {f.message}</li>)}
        </ul>
      ) : <ErrorMessage error={test.error} />}
      {r && (
        <div aria-live="polite">
          <p><strong data-testid="script-fate">{FATES[r.fate] ?? r.fate}</strong> en {r.elapsedMs} ms{r.reason ? ` : ${r.reason}` : ""}</p>
          {r.outputJson && <pre className="code" data-testid="script-output">{JSON.stringify(JSON.parse(r.outputJson), null, 2)}</pre>}
        </div>
      )}
    </section>
  );
}

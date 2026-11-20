import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage } from "../components/Feedback";

const STATUS_CLASS: Record<string, string> = {
  succeeded: "ok", succeededWithWarnings: "warn", failed: "error", cancelled: "muted", suspended: "warn", running: "", pending: "",
};

function formatDate(d: string | null) {
  return d ? new Date(d).toLocaleString("fr-FR") : "";
}

export function RunDetail({ id, onClose }: { id: string; onClose: () => void }) {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const detail = useQuery({ queryKey: ["runs.get", id], queryFn: () => call("runs.get", { id }) });
  const cancel = useMutation({
    mutationFn: () => call("runs.cancel", { id }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["runs.get", id] }),
  });
  const resume = useMutation({
    mutationFn: () => call("runs.resume", { id }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["runs.get", id] }),
  });
  const run = detail.data?.run;
  const resumable = run && (run.status === "suspended" || (run.status === "failed" && run.errorKind === "transient"));
  return (
    <section aria-label="Détail de l'exécution" className="panel">
      <h2>Exécution {id}</h2>
      <ErrorMessage error={detail.error ?? cancel.error ?? resume.error} />
      {run && (
        <>
          <p>
            <strong className={STATUS_CLASS[run.status]}>{run.statusLabel}</strong> — flux {run.flowId} v{run.flowVersion}, lancé par {run.triggeredBy}
          </p>
          <dl className="counters">
            <dt>Lus</dt><dd>{run.readCount}</dd>
            <dt>Écrits</dt><dd>{run.writtenCount}</dd>
            <dt>Rejetés</dt><dd>{run.rejectedCount}</dd>
            <dt>Ignorés (filtrés ou doublons)</dt><dd>{run.skippedCount}</dd>
          </dl>
          {run.error && <p role="alert" className="error">{run.error}</p>}
          {can("runs.cancel") && (run.status === "running" || run.status === "pending" || run.status === "suspended") && (
            <button type="button" onClick={() => cancel.mutate()} disabled={cancel.isPending}>Annuler l'exécution</button>
          )}
          {can("flows.run") && resumable && (
            <button type="button" onClick={() => resume.mutate()} disabled={resume.isPending}>
              {run.status === "suspended" ? "Reprendre l'exécution" : "Relancer depuis le point de reprise"}
            </button>
          )}
          {resume.data && <p role="status">Reprise lancée : exécution {resume.data.id}.</p>}
        </>
      )}
      {detail.data && detail.data.rejects.length > 0 && (
        <>
          <h3>Rejets</h3>
          <table>
            <thead><tr><th>Lot</th><th>Ligne</th><th>Étape</th><th>Motif</th><th>Données</th></tr></thead>
            <tbody>
              {detail.data.rejects.map((r) => (
                <tr key={`${r.batchNumber}-${r.recordIndex}-${r.stage}`}>
                  <td>{r.batchNumber}</td><td>{r.recordIndex + 1}</td><td>{r.stage}</td><td>{r.reason}</td><td><code>{r.recordJson}</code></td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
      {detail.data && (
        <>
          <h3>Journal</h3>
          <ul className="log">
            {detail.data.logs.map((l, i) => <li key={i} className={l.level}>{formatDate(l.at)} [{l.level}] {l.message}</li>)}
          </ul>
        </>
      )}
      <button type="button" onClick={onClose}>Fermer</button>
    </section>
  );
}

export function Runs() {
  const { call, client } = useEngine();
  const queryClient = useQueryClient();
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<string | null>(null);
  const list = useQuery({
    queryKey: ["runs.list", status, page],
    queryFn: () => call("runs.list", { flowId: null, status: status || null, page, pageSize: 50 }),
  });

  useEffect(() => {
    if (!client) return;
    void call("events.subscribe", { topics: ["runs.changed"] }).catch(() => undefined);
    return client.onNotification((n) => {
      if (n.method === "runs.changed") {
        void queryClient.invalidateQueries({ queryKey: ["runs.list"] });
        void queryClient.invalidateQueries({ queryKey: ["runs.get"] });
      }
    });
  }, [client, call, queryClient]);

  return (
    <section aria-labelledby="runs-title">
      <h1 id="runs-title">Exécutions</h1>
      <label>
        Statut
        <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
          <option value="">Tous</option>
          <option value="running">En cours</option>
          <option value="succeeded">Réussi</option>
          <option value="succeededWithWarnings">Réussi avec avertissements</option>
          <option value="failed">Échec</option>
          <option value="suspended">Suspendu</option>
          <option value="cancelled">Annulé</option>
        </select>
      </label>
      <ErrorMessage error={list.error} />
      <table>
        <thead><tr><th>Début</th><th>Flux</th><th>Statut</th><th>Lus</th><th>Écrits</th><th>Rejetés</th><th><span className="sr-only">Détail</span></th></tr></thead>
        <tbody>
          {list.data?.items.map((r) => (
            <tr key={r.id}>
              <td>{formatDate(r.startedAt ?? r.createdAt)}</td>
              <td>{r.flowId} v{r.flowVersion}</td>
              <td className={STATUS_CLASS[r.status]}>{r.statusLabel}</td>
              <td>{r.readCount}</td><td>{r.writtenCount}</td><td>{r.rejectedCount}</td>
              <td><button type="button" onClick={() => setSelected(r.id)}>Détail</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      {list.data && list.data.total === 0 && <p>Aucune exécution.</p>}
      {list.data && list.data.total > 50 && (
        <div className="actions">
          <button type="button" disabled={page === 1} onClick={() => setPage(page - 1)}>Précédent</button>
          <span>Page {page} / {Math.ceil(list.data.total / 50)}</span>
          <button type="button" disabled={page * 50 >= list.data.total} onClick={() => setPage(page + 1)}>Suivant</button>
        </div>
      )}
      {selected && <RunDetail id={selected} onClose={() => setSelected(null)} />}
    </section>
  );
}

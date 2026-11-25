import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { FlowSummaryDto, ScheduleDto } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage } from "../components/Feedback";

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString("fr-FR") : "—");

function ScheduleForm({ flow, schedule, onDone }: { flow: FlowSummaryDto; schedule: ScheduleDto | undefined; onDone: () => void }) {
  const { call } = useEngine();
  const [cron, setCron] = useState(schedule?.cron ?? "0 6 * * 1-5");
  const [timeZone, setTimeZone] = useState(schedule?.timeZone ?? "Europe/Paris");
  const [enabled, setEnabled] = useState(schedule?.enabled ?? true);
  const [maxRetries, setMaxRetries] = useState(schedule?.maxRetries ?? 3);
  const [retryDelaySeconds, setRetryDelaySeconds] = useState(schedule?.retryDelaySeconds ?? 300);
  const save = useMutation({
    mutationFn: () => call("schedules.save", { flowId: flow.id, cron, timeZone, enabled, maxRetries, retryDelaySeconds, expectedVersion: schedule?.version ?? 0 }),
    onSuccess: onDone,
  });
  const fields = save.error && "fields" in save.error ? (save.error as { fields: { field: string; message: string }[] }).fields : [];
  const submit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };
  return (
    <form onSubmit={submit} aria-label={`Planification de ${flow.name}`} className="panel">
      <h2>Planification de « {flow.name} »</h2>
      <label>Expression cron (minute heure jour mois jour-semaine)<input value={cron} onChange={(e) => setCron(e.target.value)} /></label>
      <p><small>Exemples : <code>0 6 * * 1-5</code> (6 h en semaine), <code>*/15 8-18 * * *</code> (toutes les 15 min de 8 h à 18 h).</small></p>
      <label>Fuseau horaire<input value={timeZone} onChange={(e) => setTimeZone(e.target.value)} /></label>
      <label><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Active</label>
      <label>Nouvelles tentatives sur erreur transitoire<input inputMode="numeric" value={maxRetries} onChange={(e) => setMaxRetries(Number(e.target.value) || 0)} /></label>
      <label>Délai initial entre tentatives (s, doublé à chaque fois)<input inputMode="numeric" value={retryDelaySeconds} onChange={(e) => setRetryDelaySeconds(Number(e.target.value) || 0)} /></label>
      {fields.length > 0 ? (
        <ul role="alert" className="error">{fields.map((f) => <li key={f.field}>{f.field} : {f.message}</li>)}</ul>
      ) : <ErrorMessage error={save.error} />}
      <div className="actions">
        <button type="button" onClick={onDone}>Annuler</button>
        <button type="submit" disabled={save.isPending}>Enregistrer</button>
      </div>
    </form>
  );
}

/** Une planification par flux ; seule la version publiée est exécutée, sans chevauchement. */
export function Schedules() {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const flows = useQuery({ queryKey: ["flows.list"], queryFn: () => call("flows.list") });
  const schedules = useQuery({ queryKey: ["schedules.list"], queryFn: () => call("schedules.list") });
  const [editing, setEditing] = useState<FlowSummaryDto | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ["schedules.list"] });
  const remove = useMutation({ mutationFn: (flowId: string) => call("schedules.delete", { id: flowId }), onSuccess: refresh });
  const byFlow = new Map((schedules.data?.schedules ?? []).map((s) => [s.flowId, s]));
  return (
    <section aria-labelledby="schedules-title">
      <h1 id="schedules-title">Planification</h1>
      <ErrorMessage error={flows.error ?? schedules.error ?? remove.error} />
      {!editing && (
        <table>
          <thead><tr><th>Flux</th><th>Planification</th><th>Prochaine exécution</th><th>Nouvelles tentatives</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>
            {flows.data?.flows.map((f) => {
              const s = byFlow.get(f.id);
              return (
                <tr key={f.id}>
                  <td>{f.name}{f.publishedVersion ? "" : <><br /><small>non publié</small></>}</td>
                  <td>{s ? <><code>{s.cron}</code> ({s.timeZone}){s.enabled ? "" : " — désactivée"}</> : "aucune"}</td>
                  <td>{s?.enabled ? when(s.nextRunAt) : "—"}{s?.retryAt ? <><br /><small>nouvelle tentative {s.retryAttempt} à {when(s.retryAt)}</small></> : null}</td>
                  <td>{s ? `${s.maxRetries} (délai ${s.retryDelaySeconds} s)` : "—"}</td>
                  <td>
                    {can("flows.edit") && <button type="button" onClick={() => setEditing(f)}>{s ? "Modifier" : "Planifier"}</button>}
                    {can("flows.edit") && s && <button type="button" onClick={() => remove.mutate(f.id)}>Supprimer</button>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {editing && (
        <ScheduleForm flow={editing} schedule={byFlow.get(editing.id)} onDone={() => { setEditing(null); refresh(); }} />
      )}
    </section>
  );
}

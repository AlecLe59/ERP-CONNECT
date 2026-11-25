import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage } from "../components/Feedback";

/** Alertes du moteur (échecs, occurrences sautées, nouvelles tentatives épuisées), mises à jour en direct. */
export function Monitoring() {
  const { call, can, client } = useEngine();
  const queryClient = useQueryClient();
  const [openOnly, setOpenOnly] = useState(true);
  const alerts = useQuery({ queryKey: ["alerts.list", openOnly], queryFn: () => call("alerts.list", { openOnly, limit: 200 }) });
  const ack = useMutation({
    mutationFn: (id: number) => call("alerts.acknowledge", { id }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["alerts.list"] }),
  });
  useEffect(() => {
    void call("events.subscribe", { topics: ["alerts.raised"] }).catch(() => undefined);
    return client.onNotification((n) => {
      if (n.method === "alerts.raised") void queryClient.invalidateQueries({ queryKey: ["alerts.list"] });
    });
  }, [call, client, queryClient]);
  const list = alerts.data?.alerts ?? [];
  return (
    <section aria-labelledby="monitoring-title">
      <h1 id="monitoring-title">Supervision</h1>
      <ErrorMessage error={alerts.error ?? ack.error} />
      <label><input type="checkbox" checked={openOnly} onChange={(e) => setOpenOnly(e.target.checked)} /> Alertes non acquittées seulement</label>
      <table>
        <thead><tr><th>Date</th><th>Gravité</th><th>Flux</th><th>Message</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {list.map((a) => (
            <tr key={a.id} className={a.severity}>
              <td>{new Date(a.createdAt).toLocaleString("fr-FR")}</td>
              <td>{a.severity === "error" ? "Erreur" : "Avertissement"}</td>
              <td>{a.flowId}</td>
              <td>{a.message}</td>
              <td>
                {a.acknowledgedAt ? <small>acquittée par {a.acknowledgedBy}</small>
                  : can("flows.run") && <button type="button" onClick={() => ack.mutate(a.id)}>Acquitter</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {alerts.data && list.length === 0 && <p>Aucune alerte.</p>}
    </section>
  );
}

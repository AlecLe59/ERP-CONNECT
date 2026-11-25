import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage, formatBytes, formatDuration } from "../components/Feedback";

const STATE_LABELS: Record<string, string> = {
  running: "En fonctionnement",
  degraded: "Mode dégradé",
  starting: "Démarrage",
  stopping: "Arrêt en cours",
};

/** État réel du moteur, rafraîchi toutes les 5 s et à chaque notification engine.statusChanged. */
export function Dashboard() {
  const { call, session, client } = useEngine();
  const queryClient = useQueryClient();
  const status = useQuery({ queryKey: ["system.getStatus"], queryFn: () => call("system.getStatus"), refetchInterval: 5000 });
  const info = useQuery({ queryKey: ["system.getInfo"], queryFn: () => call("system.getInfo") });

  useEffect(() => {
    void call("events.subscribe", { topics: ["engine.statusChanged"] }).catch(() => undefined);
    return client.onNotification((n) => {
      if (n.method === "engine.statusChanged") void queryClient.invalidateQueries({ queryKey: ["system.getStatus"] });
    });
  }, [call, client, queryClient]);

  const s = status.data;
  return (
    <section aria-labelledby="dash-title">
      <h1 id="dash-title">Tableau de bord</h1>
      <ErrorMessage error={status.error} />
      {session && (
        <p className="identity">
          Connecté en tant que <strong>{session.userName}</strong> — rôles : {session.roles.length ? session.roles.join(", ") : "aucun"}
        </p>
      )}
      {s && (
        <dl className="grid">
          <dt>État du moteur</dt>
          <dd data-testid="engine-state">{STATE_LABELS[s.state] ?? s.state}</dd>
          <dt>Version</dt>
          <dd>{s.version}</dd>
          <dt>Démarré depuis</dt>
          <dd>{formatDuration(s.uptimeSeconds)}</dd>
          <dt>Espace disque (données)</dt>
          <dd>{formatBytes(s.dataDiskFreeBytes)} libres sur {formatBytes(s.dataDiskTotalBytes)}</dd>
          <dt>Bases de données</dt>
          <dd>
            <ul>
              {s.databases.map((d) => (
                <li key={d.name}>
                  {d.name} : {d.ok ? `disponible (schéma v${d.schemaVersion})` : "INDISPONIBLE"}
                </li>
              ))}
            </ul>
          </dd>
          <dt>Coffre de secrets</dt>
          <dd>
            {s.vault.initialized ? `initialisé, clé v${s.vault.activeKeyVersion}, ${s.vault.secretCount} secret(s)` : "non initialisé"} — {s.vault.protection}
          </dd>
        </dl>
      )}
      {info.data && (
        <p className="muted">
          Installation {info.data.installationId} sur {info.data.machineName}, service exécuté par {info.data.serviceAccount}.
        </p>
      )}
    </section>
  );
}

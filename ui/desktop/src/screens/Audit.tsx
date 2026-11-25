import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage } from "../components/Feedback";

const PAGE_SIZE = 50;

export function Audit() {
  const { call } = useEngine();
  const [page, setPage] = useState(1);
  const query = useQuery({
    queryKey: ["audit.query", page],
    queryFn: () => call("audit.query", { page, pageSize: PAGE_SIZE, action: null, from: null, to: null }),
  });
  const verify = useMutation({ mutationFn: () => call("audit.verifyChain") });
  const total = query.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <section aria-labelledby="audit-title">
      <h1 id="audit-title">Journal d'audit</h1>
      <button type="button" onClick={() => verify.mutate()} disabled={verify.isPending}>Vérifier l'intégrité</button>
      {verify.data && (
        <p role="status" className={verify.data.valid ? "ok" : "error"}>
          {verify.data.valid
            ? `Chaîne intègre : ${verify.data.checkedCount} entrées vérifiées.`
            : `Chaîne ROMPUE à l'entrée ${verify.data.firstInvalidSequence} : ${verify.data.reason}`}
        </p>
      )}
      <ErrorMessage error={query.error ?? verify.error} />
      <table>
        <thead><tr><th>N°</th><th>Date</th><th>Auteur</th><th>Action</th><th>Cible</th><th>Résultat</th></tr></thead>
        <tbody>
          {query.data?.items.map((e) => (
            <tr key={e.sequence}>
              <td>{e.sequence}</td>
              <td>{new Date(e.timestamp).toLocaleString("fr-FR")}</td>
              <td>{e.actorName}</td>
              <td>{e.action}</td>
              <td>{e.target}</td>
              <td>{e.outcome === "success" ? "Succès" : e.outcome === "denied" ? "Refusé" : e.outcome}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <nav aria-label="Pagination">
        <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Précédent</button>
        <span> Page {page} / {pages} </span>
        <button type="button" disabled={page >= pages} onClick={() => setPage(page + 1)}>Suivant</button>
      </nav>
    </section>
  );
}

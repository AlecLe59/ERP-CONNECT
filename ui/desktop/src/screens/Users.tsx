import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ResolvePrincipalResult, RoleAssignmentDto } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { ConfirmDialog, ErrorMessage } from "../components/Feedback";

export const ROLE_LABELS: Record<string, string> = {
  administrator: "Administrateur",
  designer: "Concepteur",
  operator: "Opérateur",
  reader: "Lecteur",
  auditor: "Auditeur",
};

export function Users() {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const manage = can("users.manage");
  const list = useQuery({ queryKey: ["users.listAssignments"], queryFn: () => call("users.listAssignments") });
  const [name, setName] = useState("");
  const [role, setRole] = useState("operator");
  const [found, setFound] = useState<ResolvePrincipalResult | null>(null);
  const [toRevoke, setToRevoke] = useState<RoleAssignmentDto | null>(null);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["users.listAssignments"] });

  const resolve = useMutation({ mutationFn: (n: string) => call("users.resolvePrincipal", { name: n }), onSuccess: setFound });
  const assign = useMutation({
    mutationFn: (p: { sid: string; name: string; kind: string }) =>
      call("users.assignRole", { principalSid: p.sid, principalName: p.name, principalKind: p.kind, role }),
    onSuccess: () => {
      setFound(null);
      setName("");
      void refresh();
    },
  });
  const revoke = useMutation({
    mutationFn: (id: number) => call("users.revokeRole", { assignmentId: id }),
    onSettled: () => {
      setToRevoke(null);
      void refresh();
    },
  });

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    if (name.trim()) resolve.mutate(name.trim());
  };

  return (
    <section aria-labelledby="users-title">
      <h1 id="users-title">Utilisateurs et droits</h1>
      <p className="muted">Les droits sont attribués à des comptes ou groupes Windows. ERP Connect ne stocke aucun mot de passe.</p>
      <ErrorMessage error={list.error} />
      <table>
        <thead>
          <tr><th>Compte ou groupe</th><th>Type</th><th>Rôle</th><th>Attribué par</th>{manage && <th><span className="sr-only">Actions</span></th>}</tr>
        </thead>
        <tbody>
          {list.data?.items.map((a) => (
            <tr key={a.id}>
              <td>{a.principalName}<br /><small>{a.principalSid}</small></td>
              <td>{a.principalKind === "group" ? "Groupe" : "Compte"}</td>
              <td>{ROLE_LABELS[a.role] ?? a.role}</td>
              <td>{a.createdBy}</td>
              {manage && <td><button type="button" onClick={() => setToRevoke(a)}>Retirer</button></td>}
            </tr>
          ))}
        </tbody>
      </table>
      <ErrorMessage error={revoke.error} />

      {manage && (
        <form onSubmit={onSearch} aria-label="Attribuer un rôle">
          <h2>Attribuer un rôle</h2>
          <label>
            Compte ou groupe Windows (ex. DOMAINE\Comptabilité)
            <input value={name} onChange={(e) => { setName(e.target.value); setFound(null); }} />
          </label>
          <button type="submit" disabled={resolve.isPending}>Rechercher</button>
          {found && !found.found && <p role="alert" className="error">Aucun compte ou groupe Windows de ce nom.</p>}
          {found?.found && found.sid && found.name && found.kind && (
            <div>
              <p>Trouvé : <strong>{found.name}</strong> ({found.kind === "group" ? "groupe" : "compte"}, {found.sid})</p>
              <label>
                Rôle
                <select value={role} onChange={(e) => setRole(e.target.value)}>
                  {Object.entries(ROLE_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                </select>
              </label>
              <button type="button" onClick={() => assign.mutate({ sid: found.sid ?? "", name: found.name ?? "", kind: found.kind ?? "" })} disabled={assign.isPending}>Attribuer</button>
            </div>
          )}
          <ErrorMessage error={resolve.error ?? assign.error} />
        </form>
      )}

      <ConfirmDialog
        open={toRevoke !== null}
        title="Retirer ce rôle ?"
        confirmLabel="Retirer"
        onCancel={() => setToRevoke(null)}
        onConfirm={() => toRevoke && revoke.mutate(toRevoke.id)}
      >
        {toRevoke && <p>{toRevoke.principalName} perdra le rôle {ROLE_LABELS[toRevoke.role] ?? toRevoke.role}. La modification est auditée.</p>}
      </ConfirmDialog>
    </section>
  );
}

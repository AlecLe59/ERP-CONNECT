import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ConfigFieldDto, ConnectionDto, ConnectorDto } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { ConfirmDialog, ErrorMessage } from "../components/Feedback";

/** Champ du formulaire généré à partir du schéma du connecteur. Un champ secret n'est jamais pré-rempli. */
function ConfigInput({ field, value, onChange, hasSecret }: { field: ConfigFieldDto; value: string; onChange: (v: string) => void; hasSecret: boolean }) {
  const label = `${field.label}${field.required ? " *" : ""}`;
  if (field.type === "choice") {
    return (
      <label>
        {label}
        <select value={value || field.default || ""} onChange={(e) => onChange(e.target.value)}>
          {field.choices.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
      </label>
    );
  }
  if (field.type === "boolean") {
    return (
      <label className="inline">
        <input type="checkbox" checked={(value || field.default) === "true"} onChange={(e) => onChange(e.target.checked ? "true" : "false")} />
        {label}
      </label>
    );
  }
  return (
    <label>
      {label}
      {field.secret ? (
        <textarea
          rows={field.name === "privateKey" ? 4 : 1}
          value={value}
          autoComplete="off"
          spellCheck={false}
          placeholder={hasSecret ? "•••••• (enregistré : laisser vide pour conserver)" : ""}
          onChange={(e) => onChange(e.target.value)}
          className="secret"
        />
      ) : (
        <input value={value} placeholder={field.default ?? ""} onChange={(e) => onChange(e.target.value)} inputMode={field.type === "number" ? "numeric" : undefined} />
      )}
      {field.help && <small className="muted">{field.help}</small>}
    </label>
  );
}

function ConnectionForm({ connector, existing, onDone }: { connector: ConnectorDto; existing: ConnectionDto | null; onDone: () => void }) {
  const { call } = useEngine();
  const [name, setName] = useState(existing?.name ?? "");
  const [values, setValues] = useState<Record<string, string>>(
    Object.fromEntries((existing?.settings ?? []).map((s) => [s.key, s.value ?? ""])),
  );
  const [secrets, setSecrets] = useState<Record<string, string>>({});
  const [clear, setClear] = useState<string[]>([]);
  const save = useMutation({
    mutationFn: () => call("connections.save", {
      id: existing?.id ?? null,
      name,
      connectorId: connector.id,
      settings: connector.configFields.filter((f) => !f.secret && values[f.name]).map((f) => ({ key: f.name, value: values[f.name] ?? null })),
      secrets: connector.configFields.filter((f) => f.secret && secrets[f.name]).map((f) => ({ key: f.name, value: secrets[f.name] ?? null })),
      clearSecrets: clear,
      expectedVersion: existing?.version ?? 0,
    }),
    onSuccess: () => {
      setSecrets({});
      onDone();
    },
  });
  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };
  const fieldErrors = save.error && "fields" in save.error ? (save.error as { fields: { field: string; message: string }[] }).fields : [];

  return (
    <form onSubmit={onSubmit} aria-label={existing ? "Modifier la connexion" : "Nouvelle connexion"}>
      <h2>{existing ? `Modifier « ${existing.name} »` : `Nouvelle connexion : ${connector.name}`}</h2>
      <label>
        Nom *
        <input value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      {connector.configFields.map((f) => (
        <div key={f.name}>
          <ConfigInput
            field={f}
            hasSecret={!!existing?.secretFields.includes(f.name) && !clear.includes(f.name)}
            value={f.secret ? secrets[f.name] ?? "" : values[f.name] ?? ""}
            onChange={(v) => (f.secret ? setSecrets({ ...secrets, [f.name]: v }) : setValues({ ...values, [f.name]: v }))}
          />
          {f.secret && existing?.secretFields.includes(f.name) && !clear.includes(f.name) && (
            <button type="button" className="link" onClick={() => setClear([...clear, f.name])}>Effacer ce secret</button>
          )}
        </div>
      ))}
      {fieldErrors.length > 0 && (
        <ul role="alert" className="error">
          {fieldErrors.map((f) => <li key={f.field + f.message}>{f.field} : {f.message}</li>)}
        </ul>
      )}
      {fieldErrors.length === 0 && <ErrorMessage error={save.error} />}
      <div className="actions">
        <button type="button" onClick={onDone}>Annuler</button>
        <button type="submit" disabled={save.isPending}>Enregistrer</button>
      </div>
    </form>
  );
}

export function Connections() {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const edit = can("connections.edit");
  const connectors = useQuery({ queryKey: ["connectors.list"], queryFn: () => call("connectors.list") });
  const list = useQuery({ queryKey: ["connections.list"], queryFn: () => call("connections.list") });
  const [editing, setEditing] = useState<{ connector: ConnectorDto; existing: ConnectionDto | null } | null>(null);
  const [newType, setNewType] = useState("file.local");
  const [toDelete, setToDelete] = useState<ConnectionDto | null>(null);
  const [tests, setTests] = useState<Record<string, { success: boolean; message: string }>>({});
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["connections.list"] });
  const test = useMutation({
    mutationFn: (id: string) => call("connections.test", { id }),
    onSuccess: (r, id) => setTests({ ...tests, [id]: r }),
  });
  const remove = useMutation({
    mutationFn: (id: string) => call("connections.delete", { id }),
    onSettled: () => {
      setToDelete(null);
      void refresh();
    },
  });
  const connectorOf = (id: string) => connectors.data?.connectors.find((c) => c.id === id);

  return (
    <section aria-labelledby="connections-title">
      <h1 id="connections-title">Connexions</h1>
      <p className="muted">Les mots de passe, jetons et clés sont chiffrés dans le coffre du moteur et ne sont jamais réaffichés.</p>
      <ErrorMessage error={list.error ?? connectors.error} />
      <table>
        <thead>
          <tr><th>Nom</th><th>Type</th><th>Secrets</th><th>Modifiée par</th><th><span className="sr-only">Actions</span></th></tr>
        </thead>
        <tbody>
          {list.data?.connections.map((c) => (
            <tr key={c.id}>
              <td>{c.name}<br /><small>{c.id}</small></td>
              <td>{connectorOf(c.connectorId)?.name ?? c.connectorId}</td>
              <td>{c.secretFields.length > 0 ? c.secretFields.join(", ") : "aucun"}</td>
              <td>{c.updatedBy}</td>
              <td>
                {can("connections.use") && <button type="button" onClick={() => test.mutate(c.id)} disabled={test.isPending}>Tester</button>}
                {edit && (() => {
                  const connector = connectorOf(c.connectorId);
                  return connector ? <button type="button" onClick={() => setEditing({ connector, existing: c })}>Modifier</button> : null;
                })()}
                {edit && <button type="button" onClick={() => setToDelete(c)}>Supprimer</button>}
                {(() => {
                  const t = tests[c.id];
                  return t ? <p role="status" className={t.success ? "ok" : "error"}>{t.success ? "Réussi" : "Échec"} : {t.message}</p> : null;
                })()}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {list.data?.connections.length === 0 && <p>Aucune connexion.</p>}
      <ErrorMessage error={test.error ?? remove.error} />

      {edit && !editing && connectors.data && (
        <div className="actions">
          <label>
            Type
            <select value={newType} onChange={(e) => setNewType(e.target.value)}>
              {connectors.data.connectors.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <button type="button" onClick={() => {
            const connector = connectorOf(newType);
            if (connector) setEditing({ connector, existing: null });
          }}>Nouvelle connexion</button>
        </div>
      )}
      {editing && (
        <ConnectionForm
          key={editing.existing?.id ?? "new"}
          connector={editing.connector}
          existing={editing.existing}
          onDone={() => {
            setEditing(null);
            void refresh();
          }}
        />
      )}
      <ConfirmDialog
        open={toDelete !== null}
        title="Supprimer cette connexion ?"
        confirmLabel="Supprimer"
        onCancel={() => setToDelete(null)}
        onConfirm={() => toDelete && remove.mutate(toDelete.id)}
      >
        {toDelete && <p>« {toDelete.name} » et ses secrets seront supprimés. Refusé si un flux l'utilise.</p>}
      </ConfirmDialog>
    </section>
  );
}

import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ConnectionDto, FieldMapDto, FlowDefinitionDto, FlowSummaryDto, FormatDto, KeyValueDto } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { ErrorMessage } from "../components/Feedback";
import { RunDetail } from "./Runs";

const FILE_CONNECTORS = ["file.local", "file.sftp", "file.ftp"];

export const EMPTY_DEFINITION: FlowDefinitionDto = {
  source: { connectionId: "", resource: "", options: [{ key: "format", value: "csv" }] },
  target: { connectionId: "", resource: "", mode: "createNew", options: [{ key: "format", value: "json" }] },
  mapping: { mode: "identity", fields: [] },
  batchSize: 1000,
  maxRejects: 1000,
};

export function getOption(options: KeyValueDto[], key: string) {
  return options.find((o) => o.key === key)?.value ?? "";
}

export function setOption(options: KeyValueDto[], key: string, value: string): KeyValueDto[] {
  const rest = options.filter((o) => o.key !== key);
  return value ? [...rest, { key, value }] : rest;
}

/** Zone source ou cible : connexion, ressource, format de fichier (si la connexion est un transport de fichiers). */
function EndpointEditor({ title, side, value, onChange, connections, formats, connectorOf }: {
  title: string;
  side: "source" | "target";
  value: { connectionId: string; resource: string; options: KeyValueDto[]; mode?: string };
  onChange: (v: { connectionId: string; resource: string; options: KeyValueDto[]; mode?: string }) => void;
  connections: ConnectionDto[];
  formats: FormatDto[];
  connectorOf: (connectionId: string) => string | undefined;
}) {
  const isFile = FILE_CONNECTORS.includes(connectorOf(value.connectionId) ?? "");
  const isRest = connectorOf(value.connectionId) === "rest";
  const format = formats.find((f) => f.id === getOption(value.options, "format"));
  return (
    <fieldset className="zone">
      <legend>{title}</legend>
      <label>
        Connexion
        <select value={value.connectionId} onChange={(e) => onChange({ ...value, connectionId: e.target.value })}>
          <option value="">— choisir —</option>
          {connections.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label>
        {isFile ? (side === "source" ? "Fichiers (motif, ex. entrees/*.csv)" : "Fichier cible ({runId}, {date}, {timestamp})") : "Chemin d'API"}
        <input value={value.resource} onChange={(e) => onChange({ ...value, resource: e.target.value })} />
      </label>
      {side === "target" && isFile && (
        <label>
          Si le fichier existe
          <select value={value.mode} onChange={(e) => onChange({ ...value, mode: e.target.value })}>
            <option value="createNew">Échec</option>
            <option value="overwrite">Remplacer</option>
          </select>
        </label>
      )}
      {isFile && (
        <>
          <label>
            Format
            <select value={getOption(value.options, "format")} onChange={(e) => onChange({ ...value, options: setOption(value.options, "format", e.target.value) })}>
              {formats.map((f) => <option key={f.id} value={f.id}>{f.id.toUpperCase()}</option>)}
            </select>
          </label>
          {format?.options.map((o) => (
            <label key={o.name}>
              {o.label}
              {o.type === "choice" ? (
                <select value={getOption(value.options, o.name) || o.default || ""} onChange={(e) => onChange({ ...value, options: setOption(value.options, o.name, e.target.value) })}>
                  {o.choices.map((c) => <option key={c} value={c}>{c === "\t" ? "tabulation" : c}</option>)}
                </select>
              ) : (
                <input value={getOption(value.options, o.name)} placeholder={o.default ?? ""} onChange={(e) => onChange({ ...value, options: setOption(value.options, o.name, e.target.value) })} />
              )}
            </label>
          ))}
        </>
      )}
      {isRest && side === "source" && (
        <>
          <label>
            Chemin des éléments dans la réponse (ex. value)
            <input value={getOption(value.options, "itemsPath")} onChange={(e) => onChange({ ...value, options: setOption(value.options, "itemsPath", e.target.value) })} />
          </label>
          <label>
            Chemin du lien « page suivante » (ex. @odata.nextLink)
            <input value={getOption(value.options, "nextLinkPath")} onChange={(e) => onChange({ ...value, options: setOption(value.options, "nextLinkPath", e.target.value) })} />
          </label>
        </>
      )}
      {isRest && side === "target" && (
        <>
          <label>
            Méthode
            <select value={getOption(value.options, "method") || "POST"} onChange={(e) => onChange({ ...value, options: setOption(value.options, "method", e.target.value) })}>
              <option>POST</option><option>PUT</option><option>PATCH</option>
            </select>
          </label>
          <label>
            Envoi
            <select value={getOption(value.options, "mode") || "single"} onChange={(e) => onChange({ ...value, options: setOption(value.options, "mode", e.target.value === "single" ? "" : e.target.value) })}>
              <option value="single">Un appel par enregistrement</option>
              <option value="batch">Un appel par lot (tableau JSON)</option>
            </select>
          </label>
        </>
      )}
    </fieldset>
  );
}

export function MappingEditor({ fields, onChange }: { fields: FieldMapDto[]; onChange: (f: FieldMapDto[]) => void }) {
  return (
    <div>
      <table>
        <thead><tr><th>Champ cible</th><th>Expression</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>
          {fields.map((f, i) => (
            <tr key={i}>
              <td><input aria-label={`Champ cible ${i + 1}`} value={f.target} onChange={(e) => onChange(fields.map((x, j) => (j === i ? { ...x, target: e.target.value } : x)))} /></td>
              <td><input aria-label={`Expression ${i + 1}`} value={f.expression} onChange={(e) => onChange(fields.map((x, j) => (j === i ? { ...x, expression: e.target.value } : x)))} /></td>
              <td><button type="button" onClick={() => onChange(fields.filter((_, j) => j !== i))}>Retirer</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <button type="button" onClick={() => onChange([...fields, { target: "", expression: "" }])}>Ajouter un champ</button>
    </div>
  );
}

function FlowEditor({ flow, onDone }: { flow: FlowSummaryDto | null; onDone: (saved: FlowSummaryDto | null) => void }) {
  const { call } = useEngine();
  const connections = useQuery({ queryKey: ["connections.list"], queryFn: () => call("connections.list") });
  const connectors = useQuery({ queryKey: ["connectors.list"], queryFn: () => call("connectors.list") });
  const loaded = useQuery({
    queryKey: ["flows.get", flow?.id, flow?.draftVersion],
    queryFn: () => call("flows.get", { id: flow?.id ?? "", version: null }),
    enabled: flow !== null,
  });
  const [name, setName] = useState(flow?.name ?? "");
  const [description, setDescription] = useState(flow?.description ?? "");
  const [def, setDef] = useState<FlowDefinitionDto | null>(flow ? null : EMPTY_DEFINITION);
  const definition = def ?? loaded.data?.definition ?? null;
  const save = useMutation({
    mutationFn: (d: FlowDefinitionDto) => call("flows.save", { id: flow?.id ?? null, name, description, definition: d, expectedDraftVersion: flow?.draftVersion ?? 0 }),
    onSuccess: (saved) => onDone(saved),
  });
  const list = connections.data?.connections ?? [];
  const connectorOf = (id: string) => list.find((c) => c.id === id)?.connectorId;
  const fieldErrors = save.error && "fields" in save.error ? (save.error as { fields: { field: string; message: string }[] }).fields : [];
  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (definition) save.mutate(definition);
  };
  if (!definition) return <p>Chargement…</p>;
  const update = (d: Partial<FlowDefinitionDto>) => setDef({ ...definition, ...d });

  return (
    <form onSubmit={onSubmit} aria-label="Éditeur de flux" className="flow-editor">
      <h2>{flow ? `Modifier « ${flow.name} » (nouvelle version ${flow.draftVersion + 1})` : "Nouveau flux"}</h2>
      <label>Nom *<input value={name} onChange={(e) => setName(e.target.value)} /></label>
      <label>Description<input value={description} onChange={(e) => setDescription(e.target.value)} /></label>
      <div className="zones">
        <EndpointEditor title="Source" side="source" value={definition.source} connections={list} formats={connectors.data?.formats ?? []} connectorOf={connectorOf}
          onChange={(v) => update({ source: { connectionId: v.connectionId, resource: v.resource, options: v.options } })} />
        <fieldset className="zone">
          <legend>Correspondance</legend>
          <label>
            Mode
            <select value={definition.mapping.mode} onChange={(e) => update({ mapping: { ...definition.mapping, mode: e.target.value } })}>
              <option value="identity">Copier tous les champs</option>
              <option value="explicit">Champs choisis</option>
            </select>
          </label>
          {definition.mapping.mode === "explicit" && (
            <MappingEditor fields={definition.mapping.fields} onChange={(fields) => update({ mapping: { ...definition.mapping, fields } })} />
          )}
          <label>
            Taille des lots
            <input inputMode="numeric" value={definition.batchSize} onChange={(e) => update({ batchSize: Number(e.target.value) || 0 })} />
          </label>
        </fieldset>
        <EndpointEditor title="Cible" side="target" value={definition.target} connections={list} formats={connectors.data?.formats ?? []} connectorOf={connectorOf}
          onChange={(v) => update({ target: { connectionId: v.connectionId, resource: v.resource, mode: v.mode ?? "createNew", options: v.options } })} />
      </div>
      {fieldErrors.length > 0 && (
        <ul role="alert" className="error">
          {fieldErrors.map((f) => <li key={f.field + f.message}>{f.field} : {f.message}</li>)}
        </ul>
      )}
      {fieldErrors.length === 0 && <ErrorMessage error={save.error ?? loaded.error} />}
      <div className="actions">
        <button type="button" onClick={() => onDone(null)}>Annuler</button>
        <button type="submit" disabled={save.isPending}>Enregistrer</button>
      </div>
    </form>
  );
}

export function Flows() {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const list = useQuery({ queryKey: ["flows.list"], queryFn: () => call("flows.list") });
  const [editing, setEditing] = useState<FlowSummaryDto | "new" | null>(null);
  const [lastRun, setLastRun] = useState<string | null>(null);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["flows.list"] });
  const publish = useMutation({ mutationFn: (f: FlowSummaryDto) => call("flows.publish", { id: f.id, version: f.draftVersion }), onSuccess: () => void refresh() });
  const start = useMutation({
    mutationFn: (p: { flowId: string; useDraft: boolean }) => call("runs.start", p),
    onSuccess: (run) => setLastRun(run.id),
  });

  return (
    <section aria-labelledby="flows-title">
      <h1 id="flows-title">Flux</h1>
      <ErrorMessage error={list.error ?? publish.error ?? start.error} />
      {!editing && (
        <>
          <table>
            <thead><tr><th>Nom</th><th>Version en cours</th><th>Version publiée</th><th>Modifié par</th><th><span className="sr-only">Actions</span></th></tr></thead>
            <tbody>
              {list.data?.flows.map((f) => (
                <tr key={f.id}>
                  <td>{f.name}<br /><small>{f.description}</small></td>
                  <td>v{f.draftVersion}</td>
                  <td>{f.publishedVersion ? `v${f.publishedVersion}` : "non publié"}</td>
                  <td>{f.updatedBy}</td>
                  <td>
                    {can("flows.edit") && <button type="button" onClick={() => setEditing(f)}>Modifier</button>}
                    {can("flows.edit") && f.publishedVersion !== f.draftVersion && <button type="button" onClick={() => publish.mutate(f)}>Publier v{f.draftVersion}</button>}
                    {can("flows.run") && f.publishedVersion && <button type="button" onClick={() => start.mutate({ flowId: f.id, useDraft: false })}>Exécuter</button>}
                    {can("flows.edit") && can("flows.run") && <button type="button" onClick={() => start.mutate({ flowId: f.id, useDraft: true })}>Tester v{f.draftVersion}</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.data?.flows.length === 0 && <p>Aucun flux.</p>}
          {can("flows.edit") && <button type="button" onClick={() => setEditing("new")}>Nouveau flux</button>}
        </>
      )}
      {editing && (
        <FlowEditor
          flow={editing === "new" ? null : editing}
          onDone={() => {
            setEditing(null);
            void refresh();
          }}
        />
      )}
      {lastRun && <RunDetail id={lastRun} onClose={() => setLastRun(null)} />}
    </section>
  );
}

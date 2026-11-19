import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ConnectionDto, FieldMapDto, FlowDefinitionDto, FlowPreviewResult, FlowSummaryDto, FormatDto, KeyValueDto, StepSpecDto } from "@contracts";
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
  steps: [],
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
          <label>
            Archive
            <select value={getOption(value.options, "archive") || "none"} onChange={(e) => onChange({ ...value, options: setOption(value.options, "archive", e.target.value === "none" ? "" : e.target.value) })}>
              <option value="none">Aucune</option>
              <option value="zip">ZIP</option>
            </select>
          </label>
          {getOption(value.options, "archive") === "zip" && (
            <label>
              {side === "source" ? "Entrées lues (motif, ex. *.csv)" : "Nom de l'entrée dans l'archive"}
              <input value={getOption(value.options, side === "source" ? "entryPattern" : "entryName")}
                onChange={(e) => onChange({ ...value, options: setOption(value.options, side === "source" ? "entryPattern" : "entryName", e.target.value) })} />
            </label>
          )}
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

const STEP_KINDS: { kind: string; label: string }[] = [
  { kind: "filter", label: "Filtrer (garder si vrai)" },
  { kind: "set", label: "Calculer un champ" },
  { kind: "rename", label: "Renommer un champ" },
  { kind: "remove", label: "Supprimer des champs" },
  { kind: "validate", label: "Contrôler (rejeter si faux)" },
];

/** Blocs de transformation appliqués dans l'ordre, avant la correspondance. */
export function StepsEditor({ steps, onChange }: { steps: StepSpecDto[]; onChange: (s: StepSpecDto[]) => void }) {
  const patch = (i: number, p: Partial<StepSpecDto>) => onChange(steps.map((x, j) => (j === i ? { ...x, ...p } : x)));
  const move = (i: number, d: number) => {
    const next = [...steps];
    const [item] = next.splice(i, 1);
    if (item) next.splice(i + d, 0, item);
    onChange(next);
  };
  return (
    <div>
      <ol className="steps">
        {steps.map((s, i) => (
          <li key={i}>
            <select aria-label={`Type de bloc ${i + 1}`} value={s.kind} onChange={(e) => patch(i, { kind: e.target.value })}>
              {STEP_KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
            </select>
            {(s.kind === "set" || s.kind === "rename" || s.kind === "remove") && (
              <input aria-label={`Champ ${i + 1}`} placeholder={s.kind === "remove" ? "champ1, champ2" : "champ"} value={s.field ?? ""} onChange={(e) => patch(i, { field: e.target.value })} />
            )}
            {s.kind === "rename" && <input aria-label={`Nouveau nom ${i + 1}`} value={s.target ?? ""} onChange={(e) => patch(i, { target: e.target.value })} />}
            {(s.kind === "filter" || s.kind === "set" || s.kind === "validate") && (
              <input aria-label={`Expression du bloc ${i + 1}`} value={s.expression ?? ""} onChange={(e) => patch(i, { expression: e.target.value })} />
            )}
            {s.kind === "validate" && <input aria-label={`Motif de rejet ${i + 1}`} value={s.message ?? ""} onChange={(e) => patch(i, { message: e.target.value })} />}
            <button type="button" disabled={i === 0} onClick={() => move(i, -1)} aria-label={`Monter le bloc ${i + 1}`}>↑</button>
            <button type="button" disabled={i === steps.length - 1} onClick={() => move(i, 1)} aria-label={`Descendre le bloc ${i + 1}`}>↓</button>
            <button type="button" onClick={() => onChange(steps.filter((_, j) => j !== i))}>Retirer</button>
          </li>
        ))}
      </ol>
      <button type="button" onClick={() => onChange([...steps, { kind: "filter", field: null, expression: "", target: null, message: null }])}>Ajouter un bloc</button>
    </div>
  );
}

const FATE_LABEL: Record<string, string> = { output: "Transmis", filtered: "Filtré", rejected: "Rejeté" };

/** Aperçu sur un échantillon de la source : rien n'est écrit dans la cible. */
export function PreviewPanel({ result }: { result: FlowPreviewResult }) {
  return (
    <section aria-label="Aperçu" className="preview">
      <p>Champs source : {result.sourceFields.length > 0 ? result.sourceFields.map((f) => <code key={f}>[{f}]</code>) : "aucun"}</p>
      <table>
        <thead><tr><th>#</th><th>Résultat</th><th>Source</th><th>Sortie ou motif</th></tr></thead>
        <tbody>
          {result.rows.map((r, i) => (
            <tr key={i} className={r.fate}>
              <td>{i + 1}</td>
              <td>{FATE_LABEL[r.fate] ?? r.fate}</td>
              <td><code>{r.sourceJson}</code></td>
              <td>{r.fate === "output" ? <code>{r.outputJson}</code> : r.reason ?? ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {result.rows.length === 0 && <p>La source ne contient aucun enregistrement.</p>}
    </section>
  );
}

function FunctionHelp() {
  const { call } = useEngine();
  const [open, setOpen] = useState(false);
  const functions = useQuery({ queryKey: ["flows.functions"], queryFn: () => call("flows.functions"), enabled: open });
  return (
    <details onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
      <summary>Fonctions disponibles</summary>
      <p>Champs : <code>[nom]</code> ou <code>source.client.ville</code>. Texte : <code>'l''été'</code>. Opérateurs : + - * / % &amp; = &lt;&gt; &lt; &lt;= &gt; &gt;= and or not.</p>
      <ul>{functions.data?.functions.map((f) => <li key={f.name}><code>{f.signature}</code> {f.help}</li>)}</ul>
    </details>
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
  const preview = useMutation({ mutationFn: (d: FlowDefinitionDto) => call("flows.preview", { definition: d, limit: 20 }) });
  const save = useMutation({
    mutationFn: (d: FlowDefinitionDto) => call("flows.save", { id: flow?.id ?? null, name, description, definition: d, expectedDraftVersion: flow?.draftVersion ?? 0 }),
    onSuccess: (saved) => onDone(saved),
  });
  const list = connections.data?.connections ?? [];
  const connectorOf = (id: string) => list.find((c) => c.id === id)?.connectorId;
  const failure = save.error ?? preview.error;
  const fieldErrors = failure && "fields" in failure ? (failure as { fields: { field: string; message: string }[] }).fields : [];
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
          <legend>Transformation</legend>
          <StepsEditor steps={definition.steps ?? []} onChange={(steps) => update({ steps })} />
          <h3>Correspondance</h3>
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
          <FunctionHelp />
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
      {fieldErrors.length === 0 && <ErrorMessage error={failure ?? loaded.error} />}
      {preview.data && <PreviewPanel result={preview.data} />}
      <div className="actions">
        <button type="button" onClick={() => onDone(null)}>Annuler</button>
        <button type="button" disabled={preview.isPending || !definition.source.connectionId} onClick={() => preview.mutate(definition)}>Aperçu (20 lignes)</button>
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

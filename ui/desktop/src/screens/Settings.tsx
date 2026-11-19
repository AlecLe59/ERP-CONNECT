import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { SettingsDto } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { RpcError } from "../engine/client";
import { ErrorMessage } from "../components/Feedback";

const RETENTION_FIELDS: { key: keyof SettingsDto; label: string }[] = [
  { key: "runHeaderRetentionDays", label: "En-têtes d'exécution (jours)" },
  { key: "succeededLogRetentionDays", label: "Journaux des exécutions réussies (jours)" },
  { key: "failedLogRetentionDays", label: "Journaux des exécutions en échec (jours)" },
  { key: "rejectRetentionDays", label: "Rejets (jours)" },
  { key: "deduplicationRetentionDays", label: "Registre anti-doublons (jours)" },
];

export function Settings() {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: ["settings.get"], queryFn: () => call("settings.get") });
  // Brouillon local : null tant que l'utilisateur n'a rien modifié (on affiche alors la valeur du moteur).
  const [edited, setEdited] = useState<SettingsDto | null>(null);
  const editable = can("settings.edit");

  const save = useMutation({
    mutationFn: (v: { settings: SettingsDto; expectedVersion: number }) => call("settings.update", v),
    onSuccess: (r) => {
      queryClient.setQueryData(["settings.get"], r);
      setEdited(null);
    },
  });

  const draft = edited ?? query.data?.settings;
  if (!draft || !query.data) return <ErrorMessage error={query.error} />;
  const version = query.data.version;
  const setDraft = (d: SettingsDto) => setEdited(d);
  const fieldErrors = save.error instanceof RpcError ? save.error.fields : [];
  const errorFor = (k: string) => fieldErrors.find((f) => f.field.toLowerCase() === k.toLowerCase())?.message;
  const conflict = save.error instanceof RpcError && save.error.errorName === "CONFLICT";

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate({ settings: draft, expectedVersion: version });
  };

  return (
    <section aria-labelledby="settings-title">
      <h1 id="settings-title">Paramètres</h1>
      {!editable && <p className="muted">Lecture seule : la modification exige le rôle Administrateur.</p>}
      <form onSubmit={onSubmit}>
        <fieldset disabled={!editable || save.isPending}>
          <legend>Conservation (minimum 7 jours)</legend>
          {RETENTION_FIELDS.map((f) => (
            <label key={f.key}>
              {f.label}
              <input
                type="number"
                min={7}
                value={draft[f.key] as number}
                onChange={(e) => setDraft({ ...draft, [f.key]: Number(e.target.value) })}
                aria-invalid={errorFor(f.key) ? true : undefined}
              />
              {errorFor(f.key) && <span className="field-error">{errorFor(f.key)}</span>}
            </label>
          ))}
          <label>
            Niveau de journalisation
            <select value={draft.logLevel} onChange={(e) => setDraft({ ...draft, logLevel: e.target.value })}>
              {["Debug", "Information", "Warning", "Error"].map((l) => <option key={l}>{l}</option>)}
            </select>
          </label>
          <label>
            Langue
            <select value={draft.language} onChange={(e) => setDraft({ ...draft, language: e.target.value })}>
              <option value="fr-FR">Français</option>
              <option value="en-US">English</option>
            </select>
          </label>
          <label>
            <input type="checkbox" checked={draft.scriptsEnabled} onChange={(e) => setDraft({ ...draft, scriptsEnabled: e.target.checked })} />
            {" "}Autoriser les scripts JavaScript dans les flux (bac à sable, désactivé par défaut)
          </label>
          {editable && <button type="submit">Enregistrer</button>}
        </fieldset>
      </form>
      {conflict ? (
        <p role="alert" className="error">
          Les paramètres ont été modifiés par quelqu'un d'autre.{" "}
          <button type="button" onClick={() => { save.reset(); setEdited(null); void query.refetch(); }}>Recharger</button>
        </p>
      ) : (
        <ErrorMessage error={save.error} />
      )}
      {save.isSuccess && <p role="status">Paramètres enregistrés (version {save.data.version}).</p>}
      <p className="muted">Dernière modification par {query.data.updatedBy}, le {new Date(query.data.updatedAt).toLocaleString("fr-FR")}.</p>
    </section>
  );
}

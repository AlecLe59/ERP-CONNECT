import { useState, type ChangeEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { LicenseStatusResult } from "@contracts";
import { useEngine } from "../engine/EngineContext";
import { ConfirmDialog, ErrorMessage } from "../components/Feedback";
import { RpcError } from "../engine/client";

const STATES: Record<string, string> = {
  evaluation: "Évaluation",
  evaluationExpired: "Évaluation terminée",
  licensed: "Licence active",
  expired: "Licence expirée",
  invalid: "Licence refusée",
};

const FEATURES: Record<string, string> = { scheduler: "Planification", scripts: "Scripts" };

const date = (d: string | null | undefined) => (d ? new Date(d).toLocaleDateString("fr-FR") : "sans fin");

/** Licence : activation hors ligne (identifiant d'installation → fichier signé par l'éditeur → import). */
export function License() {
  const { call } = useEngine();
  const queryClient = useQueryClient();
  const [content, setContent] = useState("");
  const [confirmRemove, setConfirmRemove] = useState(false);
  const status = useQuery({ queryKey: ["license.get"], queryFn: (): Promise<LicenseStatusResult> => call("license.get") });
  const done = () => { setContent(""); void queryClient.invalidateQueries({ queryKey: ["license.get"] }); };
  const importLicense = useMutation({ mutationFn: () => call("license.import", { content }), onSuccess: done });
  const remove = useMutation({ mutationFn: () => call("license.remove"), onSuccess: () => { setConfirmRemove(false); done(); } });
  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) setContent(await file.text());
  };
  const s = status.data;
  const fieldError = importLicense.error instanceof RpcError ? importLicense.error.fields.find((f) => f.field === "content")?.message : undefined;
  return (
    <section aria-labelledby="license-title">
      <h1 id="license-title">Licence</h1>
      <ErrorMessage error={status.error ?? remove.error} />
      {s && (
        <>
          <dl>
            <dt>État</dt>
            <dd data-testid="license-state" className={s.state === "licensed" || s.state === "evaluation" ? "ok" : "error"}>{STATES[s.state] ?? s.state}</dd>
            {s.state === "evaluation" || s.state === "evaluationExpired" ? (
              <><dt>Fin de l'évaluation</dt><dd>{date(s.evaluationEndsAt)}</dd></>
            ) : null}
            {s.license && (
              <>
                <dt>Client</dt><dd>{s.license.customer}</dd>
                <dt>Édition</dt><dd>{s.license.edition}</dd>
                <dt>Expire le</dt><dd>{date(s.license.expiresAt)}</dd>
                <dt>Fonctions</dt><dd>{s.license.features.map((f) => FEATURES[f] ?? f).join(", ") || "exécution manuelle seule"}</dd>
                <dt>Flux publiés</dt><dd>{s.publishedFlows} / {s.license.maxPublishedFlows ?? "illimité"}</dd>
              </>
            )}
            {s.problem && <><dt>Problème</dt><dd role="alert">{s.problem}</dd></>}
            <dt>Identifiant d'installation</dt>
            <dd><code data-testid="installation-id">{s.installationId}</code> <small>(à transmettre à l'éditeur pour obtenir une licence)</small></dd>
          </dl>
          {!s.canVerify && (
            <p role="alert" className="error">Cette version ne contient pas de clé d'éditeur : aucune licence ne peut être importée.</p>
          )}
          <form onSubmit={(e) => { e.preventDefault(); importLicense.mutate(); }}>
            <fieldset disabled={!s.canVerify}>
              <legend>Importer une licence</legend>
              <label>Fichier de licence <input type="file" accept=".lic,.json,application/json" onChange={(e) => void onFile(e)} /></label>
              <label>
                ou contenu collé
                <textarea className="code" rows={4} value={content} onChange={(e) => setContent(e.target.value)} aria-invalid={fieldError ? true : undefined} />
              </label>
              {fieldError ? <p role="alert" className="error">{fieldError}</p> : <ErrorMessage error={importLicense.error} />}
              <button type="submit" disabled={content.trim() === "" || importLicense.isPending}>Importer</button>
            </fieldset>
          </form>
          {s.importedAt && (
            <p>
              Importée le {new Date(s.importedAt).toLocaleString("fr-FR")} par {s.importedBy}.{" "}
              <button type="button" onClick={() => setConfirmRemove(true)}>Retirer la licence</button>
            </p>
          )}
          <ConfirmDialog open={confirmRemove} title="Retirer la licence" confirmLabel="Retirer"
            onConfirm={() => remove.mutate()} onCancel={() => setConfirmRemove(false)}>
            Sans licence, les exécutions et publications sont bloquées une fois l'évaluation terminée.
          </ConfirmDialog>
        </>
      )}
    </section>
  );
}

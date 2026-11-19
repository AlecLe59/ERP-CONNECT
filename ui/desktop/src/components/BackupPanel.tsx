import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEngine } from "../engine/EngineContext";
import { RpcError } from "../engine/client";
import { ErrorMessage, formatBytes } from "./Feedback";

/** Sauvegardes : création à chaud (avec export des clés si une phrase de passe est saisie) et liste. La restauration se fait service arrêté. */
export function BackupPanel() {
  const { call, can } = useEngine();
  const queryClient = useQueryClient();
  const [passphrase, setPassphrase] = useState("");
  const [withKeys, setWithKeys] = useState(false);
  const list = useQuery({ queryKey: ["backup.list"], queryFn: () => call("backup.list") });
  const create = useMutation({
    mutationFn: () => call("backup.create", { passphrase: withKeys ? passphrase : null }),
    onSuccess: () => {
      setPassphrase("");
      void queryClient.invalidateQueries({ queryKey: ["backup.list"] });
    },
  });
  const fieldError = create.error instanceof RpcError ? create.error.fields.find((f) => f.field === "passphrase")?.message : undefined;
  return (
    <section aria-labelledby="backup-title">
      <h2 id="backup-title">Sauvegardes</h2>
      {can("settings.edit") && (
        <form onSubmit={(e) => { e.preventDefault(); create.mutate(); }}>
          <label>
            <input type="checkbox" checked={withKeys} onChange={(e) => setWithKeys(e.target.checked)} />
            {" "}Inclure les clés (nécessaire pour restaurer les mots de passe sur une autre machine)
          </label>
          {withKeys && (
            <label>
              Phrase de passe (12 caractères minimum, à conserver à part : elle n'est enregistrée nulle part)
              <input type="password" autoComplete="new-password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} aria-invalid={fieldError ? true : undefined} />
            </label>
          )}
          {fieldError ? <p role="alert" className="error">{fieldError}</p> : <ErrorMessage error={create.error} />}
          <button type="submit" disabled={create.isPending || (withKeys && passphrase.length === 0)}>Sauvegarder maintenant</button>
          {create.isSuccess && <p role="status">Sauvegarde créée : {create.data.fileName}</p>}
        </form>
      )}
      <ErrorMessage error={list.error} />
      {list.data && (
        <>
          <table>
            <thead><tr><th>Date</th><th>Fichier</th><th>Taille</th><th>Clés incluses</th></tr></thead>
            <tbody>
              {list.data.backups.map((b) => (
                <tr key={b.fileName}>
                  <td>{new Date(b.createdAt).toLocaleString("fr-FR")}</td>
                  <td>{b.fileName}</td>
                  <td>{formatBytes(b.size)}</td>
                  <td>{b.includesKeys ? "oui" : "non"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.data.backups.length === 0 && <p>Aucune sauvegarde.</p>}
          <p className="muted">
            Dossier : <code>{list.data.folder}</code>. Restauration : arrêter le service, lancer
            {" "}<code>ErpConnect.Service.exe restore &lt;archive&gt;</code> en administrateur, puis redémarrer le service.
          </p>
        </>
      )}
    </section>
  );
}

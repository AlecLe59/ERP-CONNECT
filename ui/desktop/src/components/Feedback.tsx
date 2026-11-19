import { useEffect, useRef, type ReactNode } from "react";
import { RpcError } from "../engine/client";

export function ErrorMessage({ error }: { error: unknown }) {
  if (!error) return null;
  const message = error instanceof Error ? error.message : String(error);
  const correlation = error instanceof RpcError && error.correlationId ? ` (référence ${error.correlationId})` : "";
  return (
    <p role="alert" className="error">
      {message}
      {correlation}
    </p>
  );
}

/** Boîte de confirmation dans la page (jamais de window.confirm). */
export function ConfirmDialog({ open, title, children, confirmLabel, onConfirm, onCancel }: {
  open: boolean;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (open) ref.current?.focus();
  }, [open]);
  if (!open) return null;
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="dialog-title" className="dialog" tabIndex={-1} ref={ref}>
        <h2 id="dialog-title">{title}</h2>
        <div>{children}</div>
        <div className="actions">
          <button type="button" onClick={onCancel}>Annuler</button>
          <button type="button" className="danger" onClick={onConfirm}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}

export function formatBytes(n: number) {
  if (n <= 0) return "inconnu";
  const units = ["o", "Ko", "Mo", "Go", "To"];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toLocaleString("fr-FR", { maximumFractionDigits: 1 })} ${units[i]}`;
}

export function formatDuration(seconds: number) {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d} j ${h} h ${m} min` : h > 0 ? `${h} h ${m} min` : `${m} min ${Math.floor(seconds % 60)} s`;
}

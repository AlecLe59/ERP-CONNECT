export function NotAvailable({ label, phase }: { label: string; phase: number }) {
  return (
    <section aria-labelledby="na-title">
      <h1 id="na-title">{label}</h1>
      <p className="notice">Disponible en Phase {phase}. Cet écran n'affiche aucune donnée tant que la fonction n'est pas développée et testée.</p>
    </section>
  );
}

import { useState } from "react";
import { useEngine, type ConnectionStatus } from "./engine/EngineContext";
import { MENU, type ScreenId } from "./navigation";
import { Dashboard } from "./screens/Dashboard";
import { Settings } from "./screens/Settings";
import { Users } from "./screens/Users";
import { Audit } from "./screens/Audit";
import { NotAvailable } from "./screens/NotAvailable";
import { Schedules } from "./screens/Schedules";
import { Monitoring } from "./screens/Monitoring";
import { Connections } from "./screens/Connections";
import { Flows } from "./screens/Flows";
import { Runs } from "./screens/Runs";
import { Scripts } from "./screens/Scripts";

const BANNER_TEXT: Record<ConnectionStatus, string> = {
  connecting: "Connexion au moteur…",
  unavailable: "Moteur indisponible. Le service ERP Connect Engine est arrêté ou injoignable ; reconnexion automatique en cours.",
  incompatible: "Version incompatible : mettez à jour l'application.",
  denied: "Accès refusé : aucun rôle ERP Connect n'est attribué à votre compte Windows.",
  ready: "",
};

function ConnectionBanner() {
  const { status, lastError } = useEngine();
  if (status === "ready") return null;
  return (
    <div role="alert" className={`banner banner-${status}`}>
      {BANNER_TEXT[status]}
      {lastError && status !== "connecting" && <small> ({lastError})</small>}
    </div>
  );
}

export function App() {
  const { status, can } = useEngine();
  const [screen, setScreen] = useState<ScreenId>("dashboard");
  const visible = MENU.filter((m) => can(m.permission));
  const current = visible.find((m) => m.id === screen) ?? visible[0];

  return (
    <div className="app">
      <ConnectionBanner />
      <div className="layout">
        <nav aria-label="Menu principal" className="sidebar">
          <p className="brand">ERP Connect</p>
          <ul>
            {visible.map((m) => (
              <li key={m.id}>
                <button type="button" aria-current={current?.id === m.id ? "page" : undefined} onClick={() => setScreen(m.id)}>
                  {m.label}
                  {m.availableIn !== null && <small className="phase"> Phase {m.availableIn}</small>}
                </button>
              </li>
            ))}
          </ul>
        </nav>
        <main>
          {status === "ready" && current && (
            current.availableIn !== null ? <NotAvailable label={current.label} phase={current.availableIn} />
              : current.id === "dashboard" ? <Dashboard />
              : current.id === "settings" ? <Settings />
              : current.id === "users" ? <Users />
              : current.id === "audit" ? <Audit />
              : current.id === "connections" ? <Connections />
              : current.id === "flows" ? <Flows />
              : current.id === "runs" ? <Runs />
              : current.id === "schedules" ? <Schedules />
              : current.id === "monitoring" ? <Monitoring />
              : current.id === "scripts" ? <Scripts />
              : null
          )}
        </main>
      </div>
    </div>
  );
}

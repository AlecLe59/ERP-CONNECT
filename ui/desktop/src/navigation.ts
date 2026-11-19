export type ScreenId =
  | "dashboard" | "flows" | "connections" | "runs" | "schedules" | "monitoring"
  | "scripts" | "users" | "audit" | "license" | "settings";

export interface MenuItem {
  id: ScreenId;
  label: string;
  /** Permission nécessaire pour afficher le menu. */
  permission: string;
  /** Phase de livraison prévue ; null = disponible. */
  availableIn: number | null;
}

/** Les 11 menus. Ceux d'une phase future affichent « disponible en Phase X », jamais de fausses données. */
export const MENU: MenuItem[] = [
  { id: "dashboard", label: "Tableau de bord", permission: "dashboard.read", availableIn: null },
  { id: "flows", label: "Flux", permission: "flows.read", availableIn: null },
  { id: "connections", label: "Connexions", permission: "flows.read", availableIn: null },
  { id: "runs", label: "Exécutions", permission: "flows.read", availableIn: null },
  { id: "schedules", label: "Planification", permission: "flows.read", availableIn: null },
  { id: "monitoring", label: "Supervision", permission: "dashboard.read", availableIn: null },
  { id: "scripts", label: "Scripts", permission: "flows.edit", availableIn: null },
  { id: "users", label: "Utilisateurs et droits", permission: "users.read", availableIn: null },
  { id: "audit", label: "Journal d'audit", permission: "audit.read", availableIn: null },
  { id: "license", label: "Licence", permission: "license.manage", availableIn: 7 },
  { id: "settings", label: "Paramètres", permission: "settings.read", availableIn: null },
];

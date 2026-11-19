namespace ErpConnect.Contracts;

public enum PermissionMode
{
    /// <summary>La permission indiquée est exigée à chaque appel.</summary>
    Required = 0,

    /// <summary>Aucune permission : réservé à session.hello (la connexion au pipe est déjà filtrée par l'ACL).</summary>
    None = 1,

    /// <summary>Permission évaluée pour chaque sujet demandé (events.subscribe).</summary>
    PerTopic = 2,
}

public sealed record RpcMethodDescriptor(
    string Name,
    PermissionMode PermissionMode,
    string? Permission,
    Type ParamsType,
    Type ResultType,
    string Description);

public sealed record RpcNotificationDescriptor(string Name, string Permission, Type PayloadType, string Description);

/// <summary>Contrat des Phases 2 et suivantes : source unique pour le moteur et pour le client TypeScript généré.</summary>
public static class RpcMethods
{
    public static readonly RpcMethodDescriptor SessionHello = new(
        "session.hello", PermissionMode.None, null, typeof(HelloParams), typeof(HelloResult), "Ouverture de session");

    public static readonly RpcMethodDescriptor SystemGetStatus = new(
        "system.getStatus", PermissionMode.Required, "dashboard.read", typeof(EmptyParams), typeof(SystemStatusResult),
        "État du moteur, version, uptime, espace disque, état des bases et du coffre");

    public static readonly RpcMethodDescriptor SystemGetInfo = new(
        "system.getInfo", PermissionMode.Required, "dashboard.read", typeof(EmptyParams), typeof(SystemInfoResult),
        "Installation, chemins, compte du service");

    public static readonly RpcMethodDescriptor SettingsGet = new(
        "settings.get", PermissionMode.Required, "settings.read", typeof(EmptyParams), typeof(SettingsResult), "Paramètres généraux");

    public static readonly RpcMethodDescriptor SettingsUpdate = new(
        "settings.update", PermissionMode.Required, "settings.edit", typeof(SettingsUpdateParams), typeof(SettingsResult),
        "Modification des paramètres généraux (concurrence optimiste)");

    public static readonly RpcMethodDescriptor UsersListAssignments = new(
        "users.listAssignments", PermissionMode.Required, "users.read", typeof(EmptyParams), typeof(ListAssignmentsResult),
        "Comptes et groupes ayant un rôle");

    public static readonly RpcMethodDescriptor UsersAssignRole = new(
        "users.assignRole", PermissionMode.Required, "users.manage", typeof(AssignRoleParams), typeof(RoleAssignmentDto),
        "Attribuer un rôle à un compte ou groupe Windows");

    public static readonly RpcMethodDescriptor UsersRevokeRole = new(
        "users.revokeRole", PermissionMode.Required, "users.manage", typeof(RevokeRoleParams), typeof(RevokeRoleResult),
        "Retirer un rôle");

    public static readonly RpcMethodDescriptor UsersResolvePrincipal = new(
        "users.resolvePrincipal", PermissionMode.Required, "users.manage", typeof(ResolvePrincipalParams), typeof(ResolvePrincipalResult),
        "Rechercher un compte ou groupe local ou AD");

    public static readonly RpcMethodDescriptor AuditQuery = new(
        "audit.query", PermissionMode.Required, "audit.read", typeof(AuditQueryParams), typeof(AuditQueryResult),
        "Consultation paginée du journal d'audit");

    public static readonly RpcMethodDescriptor AuditVerifyChain = new(
        "audit.verifyChain", PermissionMode.Required, "audit.read", typeof(EmptyParams), typeof(AuditVerifyResult),
        "Vérification de l'intégrité de la chaîne d'audit");

    public static readonly RpcMethodDescriptor VaultGetStatus = new(
        "vault.getStatus", PermissionMode.Required, "settings.read", typeof(EmptyParams), typeof(VaultStatusResult),
        "État du coffre, jamais son contenu");

    public static readonly RpcMethodDescriptor EventsSubscribe = new(
        "events.subscribe", PermissionMode.PerTopic, null, typeof(EventsSubscribeParams), typeof(EventsSubscribeResult),
        "Abonnement aux notifications");

    public static readonly RpcMethodDescriptor EventsUnsubscribe = new(
        "events.unsubscribe", PermissionMode.PerTopic, null, typeof(EventsSubscribeParams), typeof(EventsSubscribeResult),
        "Désabonnement");

    // Phase 3 : connecteurs, connexions, flux, exécutions (protocole 1.1).
    public static readonly RpcMethodDescriptor ConnectorsList = new(
        "connectors.list", PermissionMode.Required, "flows.read", typeof(EmptyParams), typeof(ConnectorsListResult), "Connecteurs et formats installés");

    public static readonly RpcMethodDescriptor ConnectionsList = new(
        "connections.list", PermissionMode.Required, "flows.read", typeof(EmptyParams), typeof(ConnectionsListResult), "Connexions (sans secrets)");

    public static readonly RpcMethodDescriptor ConnectionsSave = new(
        "connections.save", PermissionMode.Required, "connections.edit", typeof(ConnectionSaveParams), typeof(ConnectionDto), "Créer ou modifier une connexion");

    public static readonly RpcMethodDescriptor ConnectionsDelete = new(
        "connections.delete", PermissionMode.Required, "connections.edit", typeof(IdParams), typeof(DeleteResult), "Supprimer une connexion inutilisée");

    public static readonly RpcMethodDescriptor ConnectionsTest = new(
        "connections.test", PermissionMode.Required, "connections.use", typeof(IdParams), typeof(ConnectionTestResultDto), "Tester une connexion");

    public static readonly RpcMethodDescriptor FlowsList = new(
        "flows.list", PermissionMode.Required, "flows.read", typeof(EmptyParams), typeof(FlowsListResult), "Liste des flux");

    public static readonly RpcMethodDescriptor FlowsGet = new(
        "flows.get", PermissionMode.Required, "flows.read", typeof(FlowGetParams), typeof(FlowDetailResult), "Définition d'une version de flux");

    public static readonly RpcMethodDescriptor FlowsSave = new(
        "flows.save", PermissionMode.Required, "flows.edit", typeof(FlowSaveParams), typeof(FlowSummaryDto), "Enregistrer une nouvelle version de flux");

    public static readonly RpcMethodDescriptor FlowsPublish = new(
        "flows.publish", PermissionMode.Required, "flows.edit", typeof(FlowPublishParams), typeof(FlowSummaryDto), "Publier une version");

    public static readonly RpcMethodDescriptor RunsStart = new(
        "runs.start", PermissionMode.Required, "flows.run", typeof(RunStartParams), typeof(RunDto), "Lancer une exécution manuelle");

    public static readonly RpcMethodDescriptor RunsList = new(
        "runs.list", PermissionMode.Required, "flows.read", typeof(RunsListParams), typeof(RunsListResult), "Historique des exécutions");

    public static readonly RpcMethodDescriptor RunsGet = new(
        "runs.get", PermissionMode.Required, "flows.read", typeof(IdParams), typeof(RunDetailResult), "Détail, rejets et journal d'une exécution");

    public static readonly RpcMethodDescriptor RunsCancel = new(
        "runs.cancel", PermissionMode.Required, "runs.cancel", typeof(IdParams), typeof(RunCancelResult), "Annuler (après le lot en cours)");

    public static readonly RpcMethodDescriptor ConnectionsDiscover = new(
        "connections.discover", PermissionMode.Required, "connections.use", typeof(ConnectionDiscoverParams), typeof(ConnectionDiscoverResult), "Ressources et champs d'une connexion");

    public static readonly RpcMethodDescriptor FlowsPreview = new(
        "flows.preview", PermissionMode.Required, "flows.edit", typeof(FlowPreviewParams), typeof(FlowPreviewResult), "Aperçu d'une transformation sur un échantillon, sans écriture");

    public static readonly RpcMethodDescriptor FlowsFunctions = new(
        "flows.functions", PermissionMode.Required, "flows.read", typeof(EmptyParams), typeof(FunctionsListResult), "Fonctions du langage d'expressions");

    public static readonly RpcMethodDescriptor RunsResume = new(
        "runs.resume", PermissionMode.Required, "flows.run", typeof(IdParams), typeof(RunDto), "Reprendre une exécution suspendue ou en échec transitoire");

    public static readonly RpcMethodDescriptor SchedulesList = new(
        "schedules.list", PermissionMode.Required, "flows.read", typeof(EmptyParams), typeof(SchedulesListResult), "Planifications");

    public static readonly RpcMethodDescriptor SchedulesSave = new(
        "schedules.save", PermissionMode.Required, "flows.edit", typeof(ScheduleSaveParams), typeof(ScheduleDto), "Créer ou modifier la planification d'un flux");

    public static readonly RpcMethodDescriptor SchedulesDelete = new(
        "schedules.delete", PermissionMode.Required, "flows.edit", typeof(IdParams), typeof(DeleteResult), "Supprimer la planification d'un flux");

    public static readonly RpcMethodDescriptor AlertsList = new(
        "alerts.list", PermissionMode.Required, "dashboard.read", typeof(AlertsListParams), typeof(AlertsListResult), "Alertes (échecs, occurrences sautées)");

    public static readonly RpcMethodDescriptor AlertsAcknowledge = new(
        "alerts.acknowledge", PermissionMode.Required, "flows.run", typeof(AlertAcknowledgeParams), typeof(AlertAcknowledgeResult), "Acquitter une alerte");

    public static IReadOnlyList<RpcMethodDescriptor> All { get; } =
    [
        SessionHello, SystemGetStatus, SystemGetInfo, SettingsGet, SettingsUpdate, UsersListAssignments,
        UsersAssignRole, UsersRevokeRole, UsersResolvePrincipal, AuditQuery, AuditVerifyChain, VaultGetStatus,
        EventsSubscribe, EventsUnsubscribe,
        ConnectorsList, ConnectionsList, ConnectionsSave, ConnectionsDelete, ConnectionsTest,
        FlowsList, FlowsGet, FlowsSave, FlowsPublish, RunsStart, RunsList, RunsGet, RunsCancel,
        ConnectionsDiscover, FlowsPreview, FlowsFunctions,
        RunsResume, SchedulesList, SchedulesSave, SchedulesDelete, AlertsList, AlertsAcknowledge,
    ];

    public static RpcMethodDescriptor? Find(string name) => All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
}

public static class RpcNotifications
{
    public static readonly RpcNotificationDescriptor EngineStatusChanged = new(
        "engine.statusChanged", "dashboard.read", typeof(EngineStatusChangedPayload), "Changement d'état du moteur");

    public static readonly RpcNotificationDescriptor SettingsChanged = new(
        "settings.changed", "settings.read", typeof(SettingsChangedPayload), "Paramètres modifiés");

    public static readonly RpcNotificationDescriptor AuditAppended = new(
        "audit.appended", "audit.read", typeof(AuditAppendedPayload), "Nouvelle entrée d'audit");

    public static readonly RpcNotificationDescriptor RunChanged = new(
        "runs.changed", "flows.read", typeof(RunChangedPayload), "Changement de statut d'une exécution");

    public static readonly RpcNotificationDescriptor AlertRaised = new(
        "alerts.raised", "dashboard.read", typeof(AlertRaisedPayload), "Nouvelle alerte");

    public static IReadOnlyList<RpcNotificationDescriptor> All { get; } = [EngineStatusChanged, SettingsChanged, AuditAppended, RunChanged, AlertRaised];

    public static RpcNotificationDescriptor? Find(string name) => All.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
}

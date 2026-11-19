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

/// <summary>Contrat de la Phase 2 : source unique pour le moteur et pour le client TypeScript généré.</summary>
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

    public static IReadOnlyList<RpcMethodDescriptor> All { get; } =
    [
        SessionHello, SystemGetStatus, SystemGetInfo, SettingsGet, SettingsUpdate, UsersListAssignments,
        UsersAssignRole, UsersRevokeRole, UsersResolvePrincipal, AuditQuery, AuditVerifyChain, VaultGetStatus,
        EventsSubscribe, EventsUnsubscribe,
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

    public static IReadOnlyList<RpcNotificationDescriptor> All { get; } = [EngineStatusChanged, SettingsChanged, AuditAppended];

    public static RpcNotificationDescriptor? Find(string name) => All.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal));
}

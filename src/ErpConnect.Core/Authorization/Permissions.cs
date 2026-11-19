namespace ErpConnect.Core.Authorization;

/// <summary>Permissions au format domaine.action. Tout ce qui n'est pas accordé est refusé.</summary>
public static class Permissions
{
    public const string DashboardRead = "dashboard.read";
    public const string SettingsRead = "settings.read";
    public const string SettingsEdit = "settings.edit";
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";
    public const string FlowsRead = "flows.read";
    public const string FlowsEdit = "flows.edit";
    public const string FlowsRun = "flows.run";
    public const string ConnectionsUse = "connections.use";
    public const string ConnectionsEdit = "connections.edit";
    public const string RunsCancel = "runs.cancel";
    public const string LicenseManage = "license.manage";
    public const string RemoteManage = "remote.manage";
    public const string SupportGrant = "support.grant";

    public static IReadOnlyList<string> All { get; } =
    [
        DashboardRead, SettingsRead, SettingsEdit, UsersRead, UsersManage, AuditRead,
        FlowsRead, FlowsEdit, FlowsRun, ConnectionsUse, ConnectionsEdit, RunsCancel,
        LicenseManage, RemoteManage, SupportGrant,
    ];
}

namespace ErpConnect.Core.Authorization;

/// <summary>Rôles fixes. Les valeurs numériques sont persistées.</summary>
public enum Role
{
    Administrator = 0,
    Designer = 1,
    Operator = 2,
    Reader = 3,
    Auditor = 4,
}

public static class RolePermissions
{
    private static readonly Dictionary<Role, IReadOnlySet<string>> Matrix = new Dictionary<Role, IReadOnlySet<string>>
    {
        [Role.Administrator] = new HashSet<string>(Permissions.All, StringComparer.Ordinal),
        [Role.Designer] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.DashboardRead, Permissions.SettingsRead, Permissions.FlowsRead, Permissions.FlowsEdit,
            Permissions.FlowsRun, Permissions.ConnectionsUse, Permissions.ConnectionsEdit, Permissions.RunsCancel,
        },
        [Role.Operator] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.DashboardRead, Permissions.SettingsRead, Permissions.FlowsRead, Permissions.FlowsRun,
            Permissions.RunsCancel,
        },
        [Role.Reader] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.DashboardRead, Permissions.SettingsRead, Permissions.FlowsRead,
        },
        [Role.Auditor] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.DashboardRead, Permissions.UsersRead, Permissions.AuditRead, Permissions.FlowsRead,
        },
    };

    public static IReadOnlySet<string> For(Role role) =>
        Matrix.TryGetValue(role, out var set) ? set : new HashSet<string>();

    public static string ToFrenchLabel(this Role role) => role switch
    {
        Role.Administrator => "Administrateur",
        Role.Designer => "Concepteur",
        Role.Operator => "Opérateur",
        Role.Reader => "Lecteur",
        Role.Auditor => "Auditeur",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}

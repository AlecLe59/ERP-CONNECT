namespace ErpConnect.Core.Authorization;

/// <summary>Identité Windows de l'appelant, issue du jeton du client du named pipe.</summary>
public sealed record CallerIdentity(string UserSid, string UserName, IReadOnlyList<string> GroupSids)
{
    public IEnumerable<string> AllSids => GroupSids.Prepend(UserSid);
}

public enum PrincipalKind
{
    User = 0,
    Group = 1,
}

public enum ScopeKind
{
    Global = 0,
    FlowFolder = 1,
    Connection = 2,
}

/// <summary>Attribution d'un rôle à un compte ou groupe Windows, identifié par son SID.</summary>
public sealed record RoleAssignment(
    long Id,
    string PrincipalSid,
    string PrincipalName,
    PrincipalKind PrincipalKind,
    Role Role,
    ScopeKind ScopeKind,
    string? ScopeId,
    DateTimeOffset CreatedAt,
    string CreatedBy);

/// <summary>SID bien connus utilisés par le produit.</summary>
public static class WellKnownSids
{
    /// <summary>BUILTIN\Administrateurs.</summary>
    public const string BuiltinAdministrators = "S-1-5-32-544";

    /// <summary>NT AUTHORITY\NETWORK : refusé sur le pipe.</summary>
    public const string Network = "S-1-5-2";

    public const string LocalSystem = "S-1-5-18";
}

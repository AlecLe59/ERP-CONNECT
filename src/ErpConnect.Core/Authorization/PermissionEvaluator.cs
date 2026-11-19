namespace ErpConnect.Core.Authorization;

/// <summary>
/// Calcule les permissions effectives : union des rôles attribués au compte et à tous ses groupes.
/// Phase 2 : seule la portée globale est évaluée.
/// </summary>
public static class PermissionEvaluator
{
    public static IReadOnlySet<string> Effective(CallerIdentity caller, IEnumerable<RoleAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(assignments);

        var sids = new HashSet<string>(caller.AllSids, StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in assignments)
        {
            if (a.ScopeKind == ScopeKind.Global && sids.Contains(a.PrincipalSid))
            {
                result.UnionWith(RolePermissions.For(a.Role));
            }
        }

        return result;
    }

    public static IReadOnlySet<Role> EffectiveRoles(CallerIdentity caller, IEnumerable<RoleAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(assignments);
        var sids = new HashSet<string>(caller.AllSids, StringComparer.OrdinalIgnoreCase);
        return assignments.Where(a => a.ScopeKind == ScopeKind.Global && sids.Contains(a.PrincipalSid))
            .Select(a => a.Role).ToHashSet();
    }
}

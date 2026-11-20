using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Authorization;

namespace ErpConnect.Engine;

/// <summary>Permissions effectives, relues à chaque appel pour qu'un retrait de rôle s'applique immédiatement.</summary>
public sealed class AccessService
{
    private readonly IRoleAssignmentRepository _assignments;

    public AccessService(IRoleAssignmentRepository assignments) => _assignments = assignments;

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(CallerIdentity caller, CancellationToken ct) =>
        PermissionEvaluator.Effective(caller, await _assignments.ListAsync(ct).ConfigureAwait(false));

    public async Task<IReadOnlySet<Role>> GetRolesAsync(CallerIdentity caller, CancellationToken ct) =>
        PermissionEvaluator.EffectiveRoles(caller, await _assignments.ListAsync(ct).ConfigureAwait(false));

    /// <summary>Au premier démarrage, le groupe Administrateurs de la machine reçoit le rôle Administrateur.</summary>
    public async Task<bool> EnsureDefaultAdministratorAsync(IClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if ((await _assignments.ListAsync(ct).ConfigureAwait(false)).Count > 0)
        {
            return false;
        }

        await _assignments.AddAsync(new RoleAssignment(
            0, WellKnownSids.BuiltinAdministrators, @"BUILTIN\Administrateurs", PrincipalKind.Group, Role.Administrator,
            ScopeKind.Global, null, clock.UtcNow, "installation"), ct).ConfigureAwait(false);
        return true;
    }
}

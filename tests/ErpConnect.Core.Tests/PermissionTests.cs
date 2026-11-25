using ErpConnect.Core.Authorization;

namespace ErpConnect.Core.Tests;

public class PermissionTests
{
    // Dossier de validation, section 7 : colonnes Admin., Concepteur, Opérateur, Lecteur, Auditeur.
    public static TheoryData<string, bool, bool, bool, bool, bool> Matrix => new()
    {
        { "dashboard.read", true, true, true, true, true },
        { "settings.read", true, true, true, true, false },
        { "settings.edit", true, false, false, false, false },
        { "users.read", true, false, false, false, true },
        { "users.manage", true, false, false, false, false },
        { "audit.read", true, false, false, false, true },
        { "flows.read", true, true, true, true, true },
        { "flows.edit", true, true, false, false, false },
        { "flows.run", true, true, true, false, false },
        { "connections.use", true, true, false, false, false },
        { "connections.edit", true, true, false, false, false },
        { "runs.cancel", true, true, true, false, false },
        { "license.manage", true, false, false, false, false },
        { "remote.manage", true, false, false, false, false },
        { "support.grant", true, false, false, false, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Role_matrix_matches_the_validated_table(string permission, bool admin, bool designer, bool op, bool reader, bool auditor)
    {
        RolePermissions.For(Role.Administrator).Contains(permission).ShouldBe(admin);
        RolePermissions.For(Role.Designer).Contains(permission).ShouldBe(designer);
        RolePermissions.For(Role.Operator).Contains(permission).ShouldBe(op);
        RolePermissions.For(Role.Reader).Contains(permission).ShouldBe(reader);
        RolePermissions.For(Role.Auditor).Contains(permission).ShouldBe(auditor);
    }

    [Fact]
    public void Matrix_covers_every_declared_permission()
    {
        Matrix.Select(r => (string)r[0]).Order().ShouldBe(Permissions.All.Order());
    }

    [Fact]
    public void Effective_permissions_are_the_union_of_user_and_group_assignments()
    {
        var caller = new CallerIdentity("S-1-5-21-1-1-1-500", "u", ["S-1-5-21-1-1-1-900"]);
        var now = DateTimeOffset.UnixEpoch;
        var assignments = new[]
        {
            new RoleAssignment(1, "S-1-5-21-1-1-1-500", "u", PrincipalKind.User, Role.Reader, ScopeKind.Global, null, now, "t"),
            new RoleAssignment(2, "S-1-5-21-1-1-1-900", "g", PrincipalKind.Group, Role.Auditor, ScopeKind.Global, null, now, "t"),
            new RoleAssignment(3, "S-1-5-21-1-1-1-900", "g", PrincipalKind.Group, Role.Administrator, ScopeKind.FlowFolder, "f1", now, "t"),
            new RoleAssignment(4, "S-1-5-21-9-9-9-1", "other", PrincipalKind.User, Role.Administrator, ScopeKind.Global, null, now, "t"),
        };

        var effective = PermissionEvaluator.Effective(caller, assignments);
        effective.ShouldContain("settings.read"); // Lecteur
        effective.ShouldContain("audit.read");    // Auditeur via le groupe
        effective.ShouldNotContain("settings.edit"); // Admin seulement sur une portée dossier, ignorée en Phase 2
        PermissionEvaluator.EffectiveRoles(caller, assignments).ShouldBe([Role.Reader, Role.Auditor], ignoreOrder: true);
    }

    [Fact]
    public void Nothing_is_granted_by_default()
    {
        PermissionEvaluator.Effective(new CallerIdentity("S-1-5-21-1", "x", []), []).ShouldBeEmpty();
    }
}

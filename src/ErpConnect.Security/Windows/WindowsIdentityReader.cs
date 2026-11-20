using System.Runtime.Versioning;
using System.Security.Principal;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Authorization;

namespace ErpConnect.Security.Windows;

/// <summary>Convertit le jeton Windows du client du pipe en identité ERP Connect (ADR 0005).</summary>
[SupportedOSPlatform("windows")]
public static class WindowsIdentityReader
{
    public static CallerIdentity FromWindowsIdentity(WindowsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var userSid = identity.User?.Value ?? throw new InvalidOperationException("Jeton sans SID utilisateur.");
        var groups = identity.Groups?
            .Select(g => g.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        return new CallerIdentity(userSid, identity.Name, groups);
    }
}

/// <summary>Résolution d'un nom de compte ou de groupe (local ou AD) en SID.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrincipalResolver : IPrincipalResolver
{
    public ResolvedPrincipal? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            return null;
        }

        try
        {
            var sid = (SecurityIdentifier)new NTAccount(name.Trim()).Translate(typeof(SecurityIdentifier));
            var canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            var kind = IsGroup(sid) ? PrincipalKind.Group : PrincipalKind.User;
            return new ResolvedPrincipal(sid.Value, canonical, kind);
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    private static bool IsGroup(SecurityIdentifier sid)
    {
        // Heuristique sans appel LSA supplémentaire : les SID BUILTIN (S-1-5-32-*) et les RID de groupes
        // bien connus sont des groupes. Pour les autres, on tente de construire un principal de groupe.
        if (sid.Value.StartsWith("S-1-5-32-", StringComparison.Ordinal))
        {
            return true;
        }

        return sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
            || sid.IsWellKnown(WellKnownSidType.InteractiveSid)
            || GroupProbe.IsGroup(sid);
    }
}

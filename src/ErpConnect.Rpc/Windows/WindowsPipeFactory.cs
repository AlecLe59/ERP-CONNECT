using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ErpConnect.Core.Authorization;
using ErpConnect.Security.Windows;

namespace ErpConnect.Rpc.Windows;

/// <summary>
/// Création du pipe avec son ACL (ADR 0004) et lecture de l'identité du client (ADR 0005).
/// ACL : groupes locaux ERP Connect et Administrateurs en lecture/écriture, compte du service en contrôle total,
/// NETWORK explicitement refusé. Tout autre compte est refusé par défaut.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPipeFactory
{
    /// <summary>Noms des groupes locaux créés par l'installateur, un par rôle.</summary>
    public static IReadOnlyList<string> LocalGroupNames { get; } =
    [
        "ERP Connect Administrateurs",
        "ERP Connect Concepteurs",
        "ERP Connect Opérateurs",
        "ERP Connect Lecteurs",
        "ERP Connect Auditeurs",
    ];

    public static PipeSecurity BuildSecurity(Action<string>? onMissingGroup = null)
    {
        var security = new PipeSecurity();
        using var self = WindowsIdentity.GetCurrent();
        var service = self.User!;
        security.SetOwner(service);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSids.Network), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(service, PipeAccessRights.FullControl, AccessControlType.Allow));
        var clientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSids.BuiltinAdministrators), clientRights, AccessControlType.Allow));
        foreach (var name in LocalGroupNames)
        {
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier));
                security.AddAccessRule(new PipeAccessRule(sid, clientRights, AccessControlType.Allow));
            }
            catch (IdentityNotMappedException)
            {
                onMissingGroup?.Invoke(name);
            }
        }

        return security;
    }

    public static Func<string, bool, NamedPipeServerStream> Factory(PipeSecurity security, int maxConnections) =>
        (name, first) =>
        {
            var options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
            if (first)
            {
                options |= PipeOptions.FirstPipeInstance;
            }

            return NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, maxConnections, PipeTransmissionMode.Byte, options,
                inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, security);
        };

    /// <summary>Lit le jeton du client par emprunt d'identité (niveau Identification suffisant).</summary>
    public static CallerIdentity IdentityOf(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        CallerIdentity? identity = null;
        pipe.RunAsClient(() =>
        {
            using var wi = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            identity = WindowsIdentityReader.FromWindowsIdentity(wi);
        });
        return identity ?? throw new InvalidOperationException("Identité du client indisponible.");
    }
}

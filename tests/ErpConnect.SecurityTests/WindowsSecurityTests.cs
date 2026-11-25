using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ErpConnect.Contracts;
using ErpConnect.Rpc.Dispatch;
using ErpConnect.Rpc.Transport;
using ErpConnect.Rpc.Windows;
using ErpConnect.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpConnect.SecurityTests;

/// <summary>
/// Tests de sécurité propres à Windows. NON EXÉCUTÉS dans l'environnement de développement Linux de cette PR :
/// ils doivent être lancés sur Windows 11 et Windows Server 2022 (voir docs/phases/phase-2-rapport.md).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsSecurityTests
{
    private static string Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    // S4 : un programme crée le pipe avant le service : le service refuse de démarrer.
    [WindowsFact]
    public async Task S4_pipe_squatting_prevents_server_start()
    {
        var name = "erpconnect-squat-" + Guid.NewGuid().ToString("N")[..8];
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var security = WindowsPipeFactory.BuildSecurity();
        await using var server = new PipeServer(
            new PipeServerOptions { PipeName = name, CreatePipe = WindowsPipeFactory.Factory(security, PipeServerOptions.ListeningInstances(32)), IdentityOf = WindowsPipeFactory.IdentityOf },
            new RpcDispatcher(new NoAuth(), new NoAudit(), NullLogger<RpcDispatcher>.Instance, "t"),
            NullLogger<PipeServer>.Instance);
        Should.Throw<PipeSquattingException>(server.Start);
    }

    [WindowsFact]
    public void Pipe_acl_denies_network_and_grants_no_generic_group()
    {
        var security = WindowsPipeFactory.BuildSecurity();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        rules.ShouldContain(r => ((SecurityIdentifier)r.IdentityReference).Value == "S-1-5-2" && r.AccessControlType == AccessControlType.Deny);
        foreach (var generic in new[] { "S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4" }) // Tout le monde, Utilisateurs authentifiés, Utilisateurs, Interactif
        {
            rules.ShouldNotContain(r => ((SecurityIdentifier)r.IdentityReference).Value == generic && r.AccessControlType == AccessControlType.Allow);
        }

        security.AreAccessRulesProtected.ShouldBeTrue();
    }

    // S6 : compte virtuel, jamais SYSTEM.
    [InstalledServiceFact]
    public void S6_service_runs_under_the_virtual_account()
    {
        var qc = Sc("qc", Protocol.ServiceName);
        qc.ShouldContain(@"NT SERVICE\ERPConnectEngine");
        qc.ShouldNotContain("LocalSystem");
        var privs = Sc("qprivs", Protocol.ServiceName);
        privs.ShouldNotContain("SeDebugPrivilege");
        privs.ShouldNotContain("SeTakeOwnershipPrivilege");
        privs.ShouldNotContain("SeBackupPrivilege");
        Sc("qsidtype", Protocol.ServiceName).ShouldContain("RESTRICTED");
    }

    // S5 : aucun port TCP ni UDP ouvert par le processus du service.
    [InstalledServiceFact]
    public void S5_no_listening_port()
    {
        var pid = int.Parse(Sc("queryex", Protocol.ServiceName).Split('\n').First(l => l.Contains("PID", StringComparison.Ordinal)).Split(':')[1].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        pid.ShouldBeGreaterThan(0);
        var psi = new ProcessStartInfo("netstat.exe", "-ano") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n');
        p.WaitForExit();
        lines.Where(l => l.TrimEnd().EndsWith(" " + pid, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    // S7 : ACL de %ProgramData%\ERPConnect et de keys.
    [InstalledServiceFact]
    public void S7_data_folder_acl()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ERPConnect");
        var rootAcl = new DirectoryInfo(root).GetAccessControl();
        rootAcl.AreAccessRulesProtected.ShouldBeTrue("héritage coupé");
        var allowed = rootAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference.Value).ToHashSet();
        allowed.ShouldNotContain("S-1-5-32-545"); // Utilisateurs
        allowed.ShouldNotContain("S-1-5-11");     // Utilisateurs authentifiés
        allowed.ShouldNotContain("S-1-1-0");      // Tout le monde

        var serviceSid = new NTAccount(Protocol.ServiceAccount).Translate(typeof(SecurityIdentifier)).Value;
        var keys = new DirectoryInfo(Path.Combine(root, "keys")).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference.Value).ToHashSet();
        keys.ShouldBe(new HashSet<string> { "S-1-5-18", serviceSid }, ignoreOrder: true);
    }

    // S2 : connexion par le redirecteur réseau (\\<machine>\pipe\...) : jeton NETWORK, refusée.
    [InstalledServiceFact]
    public async Task S2_network_access_to_the_pipe_is_denied()
    {
        await using var client = new NamedPipeClientStream(Environment.MachineName, Protocol.PipeName, PipeDirection.InOut);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => client.ConnectAsync(5000));
    }

    // S1 : compte local sans rôle (ni membre d'un groupe ERP Connect) : refusé par l'ACL.
    // Le compte de test est créé par tools/dev/New-TestAccounts.ps1 et son mot de passe passé par variable d'environnement.
    [InstalledServiceFact]
    public void S1_account_without_role_is_denied_by_acl()
    {
        var user = Environment.GetEnvironmentVariable("ERPCONNECT_TEST_NOROLE_USER");
        var password = Environment.GetEnvironmentVariable("ERPCONNECT_TEST_NOROLE_PASSWORD");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Définir ERPCONNECT_TEST_NOROLE_USER et ERPCONNECT_TEST_NOROLE_PASSWORD (tools/dev/New-TestAccounts.ps1).");
        }

        using var token = NativeLogon.Logon(user, password);
        WindowsIdentity.RunImpersonated(token, () =>
        {
            using var client = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut);
            Should.Throw<UnauthorizedAccessException>(() => client.Connect(5000));
        });
    }

    private sealed class NoAuth : IRpcAuthorizer
    {
        public Task<IReadOnlySet<string>> GetPermissionsAsync(ErpConnect.Core.Authorization.CallerIdentity caller, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<IReadOnlySet<ErpConnect.Core.Authorization.Role>> GetRolesAsync(ErpConnect.Core.Authorization.CallerIdentity caller, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<ErpConnect.Core.Authorization.Role>>(new HashSet<ErpConnect.Core.Authorization.Role>());
    }

    private sealed class NoAudit : IRpcAuditSink
    {
        public Task DeniedAsync(ErpConnect.Core.Authorization.CallerIdentity caller, string method, string correlationId, CancellationToken ct) => Task.CompletedTask;
    }
}

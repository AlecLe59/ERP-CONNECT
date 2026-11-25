using System.Diagnostics;
using ErpConnect.Connectors.Transports;

namespace ErpConnect.Connectors.Tests;

/// <summary>
/// Démarre un vrai serveur OpenSSH (sshd) local sur un port libre, avec clé d'hôte et clé client générées.
/// Si sshd n'est pas installé, les tests SFTP échouent explicitement (pas de faux succès). En CI ils tournent
/// dans le job Linux ; le job Windows les exclut avec le filtre Category!=Sftp.
/// </summary>
public sealed class SftpServerFixture : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Process? _sshd;

    public SftpServerFixture()
    {
        Available = File.Exists("/usr/sbin/sshd") && !OperatingSystem.IsWindows();
        if (!Available)
        {
            throw new InvalidOperationException("sshd introuvable : installez openssh-server ou excluez Category=Sftp.");
        }

        Port = Samples.FreePort();
        Run("ssh-keygen", $"-q -t ed25519 -N \"\" -f {_dir.File("host_key")}");
        Run("ssh-keygen", $"-q -t ed25519 -N \"\" -f {_dir.File("client_key")}");
        File.Copy(_dir.File("client_key.pub"), _dir.File("authorized_keys"));
        Fingerprint = Run("ssh-keygen", $"-l -E sha256 -f {_dir.File("host_key.pub")}").Split(' ')[1];
        ClientKey = File.ReadAllText(_dir.File("client_key"));
        DataPath = _dir.File("data");
        Directory.CreateDirectory(DataPath);
        File.WriteAllText(_dir.File("sshd_config"), $"""
            Port {Port}
            ListenAddress 127.0.0.1
            HostKey {_dir.File("host_key")}
            PidFile {_dir.File("sshd.pid")}
            AuthorizedKeysFile {_dir.File("authorized_keys")}
            PasswordAuthentication no
            KbdInteractiveAuthentication no
            PubkeyAuthentication yes
            PermitRootLogin yes
            StrictModes no
            UsePAM no
            Subsystem sftp internal-sftp
            """);
        if (!Directory.Exists("/run/sshd"))
        {
            throw new InvalidOperationException("Créer /run/sshd (sudo mkdir -p /run/sshd) : requis par sshd.");
        }
        _sshd = Process.Start(new ProcessStartInfo("/usr/sbin/sshd", $"-D -e -f {_dir.File("sshd_config")}") { RedirectStandardError = true, UseShellExecute = false })!;
        _sshd.ErrorDataReceived += (_, _) => { };
        _sshd.BeginErrorReadLine();
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient("127.0.0.1", Port);
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                Thread.Sleep(100);
            }
        }

        throw new InvalidOperationException("sshd n'a pas démarré.");
    }

    public bool Available { get; }

    public int Port { get; }

    public string Fingerprint { get; } = string.Empty;

    public string ClientKey { get; } = string.Empty;

    public string DataPath { get; } = string.Empty;

    public string User => Environment.UserName;

    public SftpOptions Options(string root, string? fingerprint = null) =>
        new("127.0.0.1", Port, User, null, ClientKey, null, fingerprint ?? Fingerprint, root, TimeSpan.FromSeconds(10));

    private static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        if (_sshd is { HasExited: false })
        {
            _sshd.Kill(entireProcessTree: true);
            _sshd.WaitForExit(5000);
        }

        _sshd?.Dispose();
        _dir.Dispose();
    }
}

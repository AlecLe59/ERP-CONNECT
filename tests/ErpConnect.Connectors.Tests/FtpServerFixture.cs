using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ErpConnect.Connectors.Transports;

namespace ErpConnect.Connectors.Tests;

/// <summary>
/// Démarre un vrai serveur FTPS (pyftpdlib, TLS obligatoire sur contrôle et données) sur un port libre, avec un
/// certificat auto-signé et un mot de passe générés à chaque lancement : aucun secret dans le dépôt.
/// Requiert python3 + pyftpdlib + pyopenssl (job CI Linux). Le job Windows exclut Category=Ftp.
/// </summary>
public sealed class FtpServerFixture : IDisposable
{
    private const string Script = """
        import sys
        from pyftpdlib.authorizers import DummyAuthorizer
        from pyftpdlib.handlers import TLS_FTPHandler
        from pyftpdlib.servers import FTPServer
        port, home, cert, key, user, password, implicit = sys.argv[1:8]
        a = DummyAuthorizer()
        a.add_user(user, password, home, perm="elradfmwMT")
        h = TLS_FTPHandler
        h.certfile = cert
        h.keyfile = key
        h.authorizer = a
        h.tls_control_required = True
        h.tls_data_required = True
        h.passive_ports = range(int(port) + 1, int(port) + 40)
        s = FTPServer(("127.0.0.1", int(port)), h)
        print("ready", flush=True)
        s.serve_forever()
        """;

    private readonly TempDir _dir = new();
    private readonly Process _server;

    public FtpServerFixture()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Serveur FTPS de test : Linux uniquement (excluez Category=Ftp).");
        }

        Port = FreePortWithPassiveRange();
        Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        DataPath = _dir.File("data");
        Directory.CreateDirectory(DataPath);
        File.WriteAllText(_dir.File("server.py"), Script);
        Run("openssl", $"req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=erpconnect-test -keyout {_dir.File("key.pem")} -out {_dir.File("cert.pem")}");
        using (var cert = X509CertificateLoader.LoadCertificateFromFile(_dir.File("cert.pem")))
        {
            Fingerprint = FtpTransport.Fingerprint(cert.RawData);
        }

        _server = Process.Start(new ProcessStartInfo("python3",
            $"-I {_dir.File("server.py")} {Port} {DataPath} {_dir.File("cert.pem")} {_dir.File("key.pem")} erp {Password} 0")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("python3 introuvable.");
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginErrorReadLine();
        var ready = _server.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(15)) || ready.Result != "ready")
        {
            _server.Kill(true);
            throw new InvalidOperationException("Le serveur FTPS de test n'a pas démarré (pip install pyftpdlib pyopenssl).");
        }
    }

    public int Port { get; }

    public string Password { get; }

    public string Fingerprint { get; }

    public string DataPath { get; }

    public FtpOptions Options(string root, string? fingerprint = null, string encryption = "explicit", bool allowPlain = false, string? password = null) =>
        new("127.0.0.1", Port, "erp", password ?? Password, encryption, allowPlain, fingerprint ?? Fingerprint, root, TimeSpan.FromSeconds(10));

    private static int FreePortWithPassiveRange()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var p = Samples.FreePort();
            if (p < 65000)
            {
                return p;
            }
        }

        throw new InvalidOperationException("Aucun port libre.");
    }

    private static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} a échoué ({p.ExitCode}).");
        }
    }

    public void Dispose()
    {
        if (!_server.HasExited)
        {
            _server.Kill(entireProcessTree: true);
            _server.WaitForExit(5000);
        }

        _server.Dispose();
        _dir.Dispose();
    }
}

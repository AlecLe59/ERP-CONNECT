using System.Reflection;
using ErpConnect.Contracts;
using ErpConnect.Core.Abstractions;
using ErpConnect.Engine;
using ErpConnect.Security.Logging;
using ErpConnect.Security.Vault;
using ErpConnect.Security.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Display;

using System.Runtime.Versioning;

namespace ErpConnect.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            await Console.Error.WriteLineAsync("ERP Connect Engine ne fonctionne que sous Windows (DPAPI, named pipe avec ACL, service Windows).").ConfigureAwait(false);
            return 2;
        }

        return await RunAsync(args).ConfigureAwait(false);
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web) { MaxDepth = 16 };

    private static readonly byte[] RestoreEntropy = System.Text.Encoding.ASCII.GetBytes("ERPConnect.RestoreRequest.v1");

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "restore")
        {
            return await RequestRestoreAsync(args[1..]).ConfigureAwait(false);
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = Protocol.ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

        // Racine des données : %ProgramData%\ERPConnect, remplaçable en développement par ErpConnect:DataRoot.
        var dataRoot = builder.Configuration["ErpConnect:DataRoot"];
        var paths = string.IsNullOrWhiteSpace(dataRoot) ? EnginePaths.Default() : new EnginePaths(dataRoot);
        paths.EnsureCreated();

        var version = typeof(EngineRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        var redactor = new SecretRedactor();
        var level = new LoggingLevelSwitch();
        const string template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";
        var formatter = new RedactingFormatter(new MessageTemplateTextFormatter(template, System.Globalization.CultureInfo.InvariantCulture), redactor);

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(level)
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(formatter, Path.Combine(paths.Logs, "engine-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30);
        if (!Environment.UserInteractive)
        {
            // La source « ERP Connect » est créée par l'installateur (le compte du service n'a pas le droit de la créer).
            logConfig = logConfig.WriteTo.EventLog(
                new RedactingFormatter(new MessageTemplateTextFormatter("{Message:lj}{NewLine}{Exception}", System.Globalization.CultureInfo.InvariantCulture), redactor),
                source: "ERP Connect", manageEventSource: false, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning);
        }

        Log.Logger = logConfig.CreateLogger();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(redactor);
        builder.Services.AddSingleton(level);
        builder.Services.AddSingleton(_ => new EngineRuntime(
            paths,
            entropy => new DpapiKeyProtector(entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser),
            new WindowsPrincipalResolver(),
            new SystemClock(),
            version)
        {
            UnprotectRestorePassphrase = b => System.Security.Cryptography.ProtectedData.Unprotect(b, RestoreEntropy, System.Security.Cryptography.DataProtectionScope.LocalMachine),
        });
        builder.Services.AddHostedService<EngineWorker>();

        try
        {
            await builder.Build().RunAsync().ConfigureAwait(false);
            return Environment.ExitCode;
        }
        #pragma warning disable CA1031 // Dernier rempart : journaliser puis sortir avec un code d'erreur.
        catch (Exception ex)
        #pragma warning restore CA1031
        {
            Log.Fatal(ex, "Arrêt du service sur erreur");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// « ErpConnect.Service.exe restore &lt;archive.zip&gt; [--discard-secrets] » (administrateur, service arrêté) :
    /// vérifie l'archive et la phrase de passe, puis dépose une demande que le service exécute à son prochain démarrage,
    /// sous son propre compte. Rien n'est modifié par cette commande.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task<int> RequestRestoreAsync(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--discard-secrets"))
        {
            await Console.Error.WriteLineAsync("Usage : ErpConnect.Service.exe restore <archive.zip> [--discard-secrets]").ConfigureAwait(false);
            return 2;
        }

        var archive = Path.GetFullPath(args[0]);
        var paths = EnginePaths.Default();
        BackupManifest manifest;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
            using var s = (zip.GetEntry(BackupService.ManifestName) ?? throw new InvalidDataException("Archive sans manifeste.")).Open();
            manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(s, WebJson)!;
            string? protectedPassphrase = null;
            if (manifest.IncludesKeys)
            {
                Console.Write("Phrase de passe de l'export des clés (vide pour ignorer les clés) : ");
                var passphrase = ReadHidden();
                if (passphrase.Length > 0)
                {
                    using var keys = new StreamReader((zip.GetEntry(BackupService.KeysName) ?? throw new InvalidDataException("Export des clés absent.")).Open());
                    foreach (var k in Security.Backup.KeyEscrow.Import(await keys.ReadToEndAsync().ConfigureAwait(false), passphrase).Values)
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(k); // vérification seulement
                    }

                    protectedPassphrase = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                        System.Text.Encoding.UTF8.GetBytes(passphrase), RestoreEntropy, System.Security.Cryptography.DataProtectionScope.LocalMachine));
                }
            }

            var request = new RestoreRequest(archive, args.Length == 2, protectedPassphrase, DateTimeOffset.UtcNow, Environment.UserDomainName + "\\" + Environment.UserName);
            await File.WriteAllTextAsync(Path.Combine(paths.Data, RestoreRequest.FileName),
                System.Text.Json.JsonSerializer.Serialize(request, WebJson)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            await Console.Error.WriteLineAsync("Restauration impossible : " + ex.Message).ConfigureAwait(false);
            return 1;
        }

        Console.WriteLine($"Sauvegarde du {manifest.CreatedAt:yyyy-MM-dd HH:mm} vérifiée. Démarrez le service pour la restaurer (l'état actuel sera conservé dans {paths.Backup}).");
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}

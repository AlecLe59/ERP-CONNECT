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

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string[] args)
    {
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
            version));
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
}

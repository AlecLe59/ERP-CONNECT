using System.Text;
using ErpConnect.Security.Logging;
using ErpConnect.Security.Vault;
using ErpConnect.Service;
using ErpConnect.Storage;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Formatting.Display;

namespace ErpConnect.SecurityTests;

public sealed class SecretLeakTests : IAsyncLifetime
{
    private const string Witness = "Wit-ness:9f3K#secret-ERP";
    private readonly TempDirectory _dir = new();
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() => _engine = await EngineHarness.StartAsync(_dir.Path);

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Dispose();
    }

    // S8 : le secret témoin est introuvable dans les bases (y compris WAL), sauvegardes, clés et journaux.
    [Fact]
    public async Task S8_witness_secret_is_never_found_in_clear_on_disk()
    {
        await _engine.Runtime.Vault.StoreAsync("conn/witness", "password", Encoding.UTF8.GetBytes(Witness), default);
        await _engine.Runtime.Vault.RotateAsync(default);
        Encoding.UTF8.GetString((await _engine.Runtime.Vault.ResolveAsync("conn/witness", "password", default))!).ShouldBe(Witness);

        // Journal de fichier réel du service, avec le formateur de masquage, alimenté par un message contenant le témoin.
        var redactor = new SecretRedactor();
        redactor.Register(Witness);
        var logFile = Path.Combine(_engine.Runtime.Paths.Logs, "engine-test.log");
        await using (var log = new LoggerConfiguration()
            .WriteTo.File(new RedactingFormatter(new MessageTemplateTextFormatter("{Message:lj}{NewLine}{Exception}"), redactor), logFile)
            .CreateLogger())
        {
            log.Error(new InvalidOperationException($"Connexion refusée pour le mot de passe {Witness}"), "Échec avec {Value}", Witness);
        }

        // Sauvegarde déclenchée par une nouvelle migration de test.
        var embedded = MigrationRunner.EmbeddedMigrations(DatabaseKind.Config);
        var extra = embedded
            .Append(new Migration(embedded.Count + 1, $"{embedded.Count + 1:0000}_t", "CREATE TABLE T (X INT);", MigrationRunner.Hash("CREATE TABLE T (X INT);"))).ToList();
        (await new MigrationRunner(_engine.Runtime.Paths.Backup, _ => extra).MigrateAsync(_engine.Runtime.Config, default)).BackupPath.ShouldNotBeNull();

        var needles = new[]
        {
            Encoding.UTF8.GetBytes(Witness),
            Encoding.Unicode.GetBytes(Witness),
            Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(Witness))),
        };
        // Deux passages : connexions ouvertes (fichiers -wal et -shm présents), puis après fermeture et point de contrôle.
        var scanned = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                SqliteConnection.ClearAllPools();
            }

            foreach (var file in Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories))
            {
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(file);
                }
                catch (FileNotFoundException)
                {
                    continue; // fichier -shm/-wal supprimé entre l'énumération et la lecture
                }

                scanned++;
                foreach (var needle in needles)
                {
                    bytes.AsSpan().IndexOf(needle).ShouldBe(-1, $"Secret témoin trouvé dans {Path.GetFileName(file)}");
                }
            }
        }

        scanned.ShouldBeGreaterThan(10);
    }

    // S9 : secret témoin dans un message d'exception, masqué dans les journaux.
    [Fact]
    public void S9_exception_messages_are_redacted()
    {
        var redactor = new SecretRedactor();
        redactor.Register(Witness);
        var formatter = new RedactingFormatter(new MessageTemplateTextFormatter("{Message:lj} {Exception}"), redactor);
        var writer = new StringWriter();
        var sink = new DelegateSink(e => formatter.Format(e, writer));
        using (var log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger())
        {
            log.Error(new InvalidOperationException("Server=db;Password=Autre1;" + Witness), "Erreur {Detail}", Witness);
        }

        var text = writer.ToString();
        text.ShouldNotContain(Witness);
        text.ShouldNotContain("Autre1");
        text.ShouldContain(SecretRedactor.Mask);
    }

    // S10 : falsification ou déplacement d'un secret chiffré.
    [Fact]
    public async Task S10_tampered_or_moved_ciphertext_is_refused_cleanly()
    {
        var vault = _engine.Runtime.Vault;
        await vault.StoreAsync("conn/a", "password", "value-a"u8.ToArray(), default);
        await vault.StoreAsync("conn/b", "password", "value-b"u8.ToArray(), default);

        await Exec("UPDATE Secret SET Ciphertext = (SELECT Ciphertext FROM Secret WHERE Id = 'conn/a'), Nonce = (SELECT Nonce FROM Secret WHERE Id = 'conn/a'), Tag = (SELECT Tag FROM Secret WHERE Id = 'conn/a') WHERE Id = 'conn/b';");
        var moved = await Should.ThrowAsync<SecretIntegrityException>(() => vault.ResolveAsync("conn/b", "password", default));
        moved.Message.ShouldNotContain("value-a");

        await Exec("UPDATE Secret SET Ciphertext = CAST(zeroblob(length(Ciphertext)) AS BLOB) WHERE Id = 'conn/a';");
        await Should.ThrowAsync<SecretIntegrityException>(() => vault.ResolveAsync("conn/a", "password", default));

        // Le moteur reste utilisable.
        await vault.StoreAsync("conn/c", "password", "value-c"u8.ToArray(), default);
        Encoding.UTF8.GetString((await vault.ResolveAsync("conn/c", "password", default))!).ShouldBe("value-c");
    }

    // S11 : modification ou suppression d'une entrée d'audit détectée par audit.verifyChain.
    [Theory]
    [InlineData("UPDATE AuditEvent SET Outcome = 'tampered' WHERE Sequence = 2;")]
    [InlineData("DELETE FROM AuditEvent WHERE Sequence = 2;")]
    [InlineData("UPDATE AuditEvent SET Details = NULL WHERE Sequence = 3;")]
    public async Task S11_audit_tampering_is_detected(string tamper)
    {
        await using (var admin = await _engine.ConnectAsync(Identities.Admin))
        {
            for (var i = 0; i < 3; i++)
            {
                await admin.CallAsync("users.assignRole", new ErpConnect.Contracts.AssignRoleParams($"S-1-5-21-1-2-3-{i + 10}", $"u{i}", "user", "reader"));
            }

            (await admin.CallAsync("audit.verifyChain")).Result()["valid"]!.GetValue<bool>().ShouldBeTrue();
        }

        // Un attaquant ayant accès au fichier contourne les déclencheurs d'ajout seul.
        await using (var c = new SqliteConnection($"Data Source={_engine.Runtime.AuditDb.Path}"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TRIGGER TR_AuditEvent_NoUpdate; DROP TRIGGER TR_AuditEvent_NoDelete; " + tamper;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var admin2 = await _engine.ConnectAsync(Identities.Admin);
        var v = (await admin2.CallAsync("audit.verifyChain")).Result();
        v["valid"]!.GetValue<bool>().ShouldBeFalse();
        v["firstInvalidSequence"]!.GetValue<long>().ShouldBeLessThanOrEqualTo(3);
    }

    private async Task Exec(string sql)
    {
        await using var c = await _engine.Runtime.Config.OpenAsync(default);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class DelegateSink(Action<Serilog.Events.LogEvent> write) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => write(logEvent);
    }
}

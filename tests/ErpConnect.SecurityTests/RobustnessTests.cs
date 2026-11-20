using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using ErpConnect.Service;
using ErpConnect.Storage;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;
using Microsoft.Data.Sqlite;

namespace ErpConnect.SecurityTests;

public sealed class RobustnessTests : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() => _engine = await EngineHarness.StartAsync(_dir.Path);

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Dispose();
    }

    // S12 : messages malformés et fuzzing contre le moteur complet ; il reste disponible pour les autres clients.
    [Fact]
    public async Task S12_malformed_inputs_and_fuzzing()
    {
        await using var bystander = await _engine.ConnectAsync(Identities.Admin);
        var rng = new Random(20261119);
        var cases = new List<byte[]>
        {
            "{not json"u8.ToArray(),
            System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session.hello\",\"params\":" + new string('[', 1000) + new string(']', 1000) + "}"),
            new byte[] { 0xFF, 0xFE, 0x00, 0x01 },
        };
        for (var i = 0; i < 150; i++)
        {
            var b = new byte[rng.Next(1, 4096)];
            rng.NextBytes(b);
            cases.Add(b);
        }

        foreach (var payload in cases)
        {
            await using var c = await _engine.ConnectAsync(Identities.Admin, hello: false);
            try
            {
                var r = await c.SendRawAsync(payload);
                r["error"].ShouldNotBeNull();
            }
            catch (IOException)
            {
                // fermeture de connexion acceptée
            }
        }

        // Longueur annoncée fausse et en-tête de 5 Mo, sur des connexions brutes.
        foreach (var header in new uint[] { 100, 5 * 1024 * 1024, uint.MaxValue })
        {
            _engine.Server.ActiveSessions.ShouldBeLessThan(33);
            await using var raw = await _engine.ConnectAsync(Identities.Admin, hello: false);
            var h = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(h, header);
            try
            {
                await raw.Stream.WriteAsync(h);
                await raw.Stream.WriteAsync(RandomNumberGenerator.GetBytes(10));
                await raw.Stream.FlushAsync();
            }
            catch (IOException)
            {
            }
        }

        (await bystander.CallAsync("system.getStatus")).Result()["state"]!.GetValue<string>().ShouldBe("running");
    }

    // S13 : 1 000 connexions ouvertes en boucle ; le service reste réactif.
    [Fact]
    public async Task S13_thousand_connections_in_a_loop()
    {
        for (var i = 0; i < 1000; i++)
        {
            await using var c = await _engine.ConnectAsync(Identities.Admin, hello: i % 50 == 0);
        }

        await using var after = await _engine.ConnectAsync(Identities.Admin);
        (await after.CallAsync("system.getStatus"))["result"].ShouldNotBeNull();
    }

    // S16 : migration modifiée après publication, le moteur refuse de démarrer.
    [Fact]
    public async Task S16_modified_migration_prevents_start()
    {
        await _engine.DisposeAsync();
        await using (var c = new SqliteConnection($"Data Source={Path.Combine(_dir.Path, "data", "config.db")}"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE __schema_migrations SET Sha256 = 'deadbeef' WHERE Number = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        var ex = await Should.ThrowAsync<MigrationIntegrityException>(() => EngineHarness.StartAsync(_dir.Path));
        ex.Message.ShouldContain("Démarrage refusé");
        _engine = await EngineHarnessFactory.NoopAsync(_dir.Path); // pour DisposeAsync
    }
}

internal static class EngineHarnessFactory
{
    /// <summary>Après S16, redémarre sur un répertoire vierge pour que le nettoyage du test fonctionne.</summary>
    public static Task<EngineHarness> NoopAsync(string root) => EngineHarness.StartAsync(Path.Combine(root, "clean"));
}

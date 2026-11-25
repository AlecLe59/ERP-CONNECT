using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ErpConnect.Contracts;
using ErpConnect.Engine;
using ErpConnect.Security.Vault;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Service.IntegrationTests;

/// <summary>Protecteur propre à une « machine » (clé d'enveloppe aléatoire) : simule DPAPI, illisible ailleurs.</summary>
internal sealed class MachineProtector : IKeyProtector
{
    private readonly byte[] _wrap = RandomNumberGenerator.GetBytes(32);

    public string Description => "Protecteur de test par machine";

    public byte[] Protect(byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[key.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_wrap, 16);
        aes.Encrypt(nonce, key, ct, tag);
        return [.. nonce, .. tag, .. ct];
    }

    public byte[] Unprotect(byte[] blob)
    {
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_wrap, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return plain;
    }
}

/// <summary>Phase 8 : sauvegarde à chaud, restauration sur la même machine et sur une autre, refus des archives douteuses.</summary>
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private const string Phrase = "une phrase de passe assez longue";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly TempDirectory _dir = new();
    private readonly MachineProtector _machineA = new();
    private readonly MachineProtector _machineB = new();
    private readonly string _token = "v-" + Guid.NewGuid().ToString("N"); // secret généré à l'exécution

    private string RootA => Path.Combine(_dir.Path, "a");

    private string RootB => Path.Combine(_dir.Path, "b");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _dir.Dispose();
        return Task.CompletedTask;
    }

    private Task<EngineHarness> StartAsync(string root, MachineProtector machine) =>
        EngineHarness.StartAsync(root, new FakeClock { UtcNow = DateTimeOffset.UtcNow }, protectorFactory: _ => machine);

    /// <summary>Machine A : une connexion avec secret, un flux, puis une sauvegarde.</summary>
    private async Task<(string Archive, string InstallationId)> BackupOnAAsync(string? passphrase)
    {
        await using var a = await StartAsync(RootA, _machineA);
        await using var c = await a.ConnectAsync(Identities.Admin);
        (await c.CallAsync("connections.save", new ConnectionSaveParams("api", "API", "rest", [new("baseUrl", "https://api.example/")], [new("token", _token)], [], 0))).Result();
        var backup = (await c.CallAsync("backup.create", new BackupCreateParams(passphrase))).Result();
        backup["includesKeys"]!.GetValue<bool>().ShouldBe(passphrase is not null);
        var installation = (await c.CallAsync("license.get")).Result()["installationId"]!.GetValue<string>();
        return (Path.Combine(RootA, "backup", backup["fileName"]!.GetValue<string>()), installation);
    }

    private static async Task RequestRestoreAsync(string root, string archive, string? passphrase, bool discardSecrets = false)
    {
        var request = new RestoreRequest(archive, discardSecrets, passphrase is null ? null : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(passphrase)), DateTimeOffset.UtcNow, @"PC\admin");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        await File.WriteAllTextAsync(Path.Combine(root, "data", RestoreRequest.FileName), JsonSerializer.Serialize(request, Web));
    }

    private static async Task<string?> TokenAsync(EngineHarness h) => (await h.Runtime.Connections.ResolveAsync("api", default)).Settings.GetSecret("token");

    private static async Task<IReadOnlyList<System.Text.Json.Nodes.JsonNode?>> AuditAsync(RpcTestClient c, string action) =>
        (await c.CallAsync("audit.query", new AuditQueryParams(1, 50, action, null, null))).Result()["items"]!.AsArray().ToList();

    [Fact]
    public async Task Backup_with_passphrase_restores_secrets_on_another_machine()
    {
        var (archive, installation) = await BackupOnAAsync(Phrase);
        await (await StartAsync(RootB, _machineB)).DisposeAsync(); // machine B déjà installée (sa propre clé)
        await RequestRestoreAsync(RootB, archive, Phrase);

        await using var b = await StartAsync(RootB, _machineB);
        b.Runtime.RestoreOutcome!.ShouldStartWith("Restauration de la sauvegarde");
        (await TokenAsync(b)).ShouldBe(_token);
        await using var c = await b.ConnectAsync(Identities.Admin);
        (await c.CallAsync("license.get")).Result()["installationId"]!.GetValue<string>().ShouldBe(installation); // même installation : la licence suit
        (await c.CallAsync("audit.verifyChain")).Result()["valid"]!.GetValue<bool>().ShouldBeTrue();
        (await AuditAsync(c, "backup.restore")).Single()!["outcome"]!.GetValue<string>().ShouldBe("success");
        (await AuditAsync(c, "connections.save")).Count.ShouldBe(1); // l'historique de A est revenu (la sauvegarde ne contient pas sa propre entrée « backup.create »)
        Directory.GetDirectories(Path.Combine(RootB, "backup"), "pre-restore-*").Length.ShouldBe(1); // l'état de B est conservé, pas supprimé
        File.Exists(Path.Combine(RootB, "data", RestoreRequest.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Backup_without_keys_is_refused_on_another_machine_unless_secrets_are_discarded()
    {
        var (archive, _) = await BackupOnAAsync(null);
        await RequestRestoreAsync(RootB, archive, null);
        await using (var refused = await StartAsync(RootB, _machineB))
        {
            refused.Runtime.RestoreOutcome!.ShouldContain("ne sont pas disponibles");
            await using var c = await refused.ConnectAsync(Identities.Admin);
            (await c.CallAsync("connections.list")).Result()["connections"]!.AsArray().ShouldBeEmpty(); // rien n'a changé
            (await AuditAsync(c, "backup.restore")).Single()!["outcome"]!.GetValue<string>().ShouldBe("failure");
        }

        await RequestRestoreAsync(RootB, archive, null, discardSecrets: true);
        await using var b = await StartAsync(RootB, _machineB);
        (await TokenAsync(b)).ShouldBeNull(); // connexion restaurée, secret à ressaisir
        (await b.Runtime.Connections.ListAsync(default)).Single().Id.ShouldBe("api");
    }

    [Fact]
    public async Task Same_machine_restore_keeps_local_keys()
    {
        var (archive, _) = await BackupOnAAsync(null);
        await using (var a = await StartAsync(RootA, _machineA))
        {
            await using var c = await a.ConnectAsync(Identities.Admin);
            (await c.CallAsync("connections.delete", new IdParams("api"))).Result();
        }

        await RequestRestoreAsync(RootA, archive, null);
        await using var restored = await StartAsync(RootA, _machineA);
        (await TokenAsync(restored)).ShouldBe(_token);
    }

    [Fact]
    public async Task Wrong_passphrase_or_altered_archive_changes_nothing()
    {
        var (archive, _) = await BackupOnAAsync(Phrase);
        await RequestRestoreAsync(RootB, archive, "mauvaise phrase de passe");
        await using (var b = await StartAsync(RootB, _machineB))
        {
            b.Runtime.RestoreOutcome!.ShouldContain("Phrase de passe incorrecte");
            (await b.Runtime.Connections.ListAsync(default)).ShouldBeEmpty();
        }

        var altered = Path.Combine(_dir.Path, "altered.zip");
        File.Copy(archive, altered);
        using (var zip = ZipFile.Open(altered, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("config.db")!;
            byte[] bytes;
            using (var s = entry.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                bytes = m.ToArray();
            }

            bytes[^1] ^= 0xFF;
            entry.Delete();
            using var w = zip.CreateEntry("config.db").Open();
            w.Write(bytes);
        }

        await RequestRestoreAsync(RootB, altered, Phrase);
        await using var again = await StartAsync(RootB, _machineB);
        again.Runtime.RestoreOutcome!.ShouldContain("Empreinte incorrecte");
        Directory.GetDirectories(Path.Combine(RootB, "backup"), "pre-restore-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task Entry_larger_than_announced_is_refused_without_extracting_it_all()
    {
        var (archive, _) = await BackupOnAAsync(null);
        var bomb = Path.Combine(_dir.Path, "bomb.zip");
        File.Copy(archive, bomb);
        using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonNode manifest;
            using (var s = entry.Open())
            {
                manifest = System.Text.Json.Nodes.JsonNode.Parse(s)!;
            }

            manifest["files"]!.AsArray().First(f => f!["name"]!.GetValue<string>() == "config.db")!["size"] = 100;
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(manifest.ToJsonString());
        }

        await RequestRestoreAsync(RootA, bomb, null);
        await using var a = await StartAsync(RootA, _machineA);
        a.Runtime.RestoreOutcome!.ShouldContain("dépasse la taille annoncée");
        (await TokenAsync(a)).ShouldBe(_token);
    }

    [Fact]
    public async Task Backup_rpc_validates_passphrase_lists_archives_and_never_audits_the_passphrase()
    {
        await using var a = await StartAsync(RootA, _machineA);
        await using var c = await a.ConnectAsync(Identities.Admin);
        (await c.CallAsync("backup.create", new BackupCreateParams("court"))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        (await c.CallAsync("backup.create", new BackupCreateParams(Phrase))).Result();
        (await c.CallAsync("backup.create", new BackupCreateParams(null))).Result();
        var list = (await c.CallAsync("backup.list")).Result()["backups"]!.AsArray();
        list.Count.ShouldBe(2);
        list.Count(b => b!["includesKeys"]!.GetValue<bool>()).ShouldBe(1);
        (await c.CallAsync("audit.query", new AuditQueryParams(1, 100, null, null, null))).ToJsonString().ShouldNotContain(Phrase);

        // L'archive contient les trois bases, un manifeste, et l'export des clés chiffré (jamais la clé en clair).
        var file = Directory.GetFiles(Path.Combine(RootA, "backup"), "*.zip").First(f => ZipFile.OpenRead(f).Entries.Any(e => e.Name == "keys.json"));
        using var zip = ZipFile.OpenRead(file);
        zip.Entries.Select(e => e.Name).Order().ShouldBe(["audit.db", "config.db", "keys.json", "manifest.json", "runs.db"]);
    }
}

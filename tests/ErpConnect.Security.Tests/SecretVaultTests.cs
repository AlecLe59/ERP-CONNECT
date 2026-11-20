using System.Text;
using ErpConnect.Security.Vault;
using ErpConnect.Storage;
using ErpConnect.Storage.Repositories;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Security.Tests;

public sealed class SecretVaultTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<(SecretVault Vault, SecretStore Store, MasterKeyStore Keys)> CreateAsync()
    {
        var db = SqliteDatabase.InDirectory(DatabaseKind.Config, _dir.Path);
        await new MigrationRunner(Path.Combine(_dir.Path, "backup")).MigrateAsync(db, default);
        var store = new SecretStore(db);
        var keys = new MasterKeyStore(Path.Combine(_dir.Path, "keys"), new TestKeyProtector());
        var vault = new SecretVault(store, keys, new FakeClock());
        await vault.InitializeAsync(default);
        return (vault, store, keys);
    }

    [Fact]
    public async Task First_start_creates_key_v1_then_store_and_resolve()
    {
        var (vault, _, keys) = await CreateAsync();
        vault.IsInitialized.ShouldBeTrue();
        vault.ActiveKeyVersion.ShouldBe(1);
        keys.ExistingVersions().ShouldBe([1]);
        File.Exists(Path.Combine(_dir.Path, "keys", "master.v1.key")).ShouldBeTrue();

        await vault.StoreAsync("conn/erp", "password", "Tr3s-S3cret"u8.ToArray(), default);
        Encoding.UTF8.GetString((await vault.ResolveAsync("conn/erp", "password", default))!).ShouldBe("Tr3s-S3cret");
        (await vault.ResolveAsync("absent", "password", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Resolving_with_another_usage_fails()
    {
        var (vault, _, _) = await CreateAsync();
        await vault.StoreAsync("conn/erp", "password", "x1y2z3"u8.ToArray(), default);
        await Should.ThrowAsync<SecretIntegrityException>(() => vault.ResolveAsync("conn/erp", "apiKey", default));
    }

    [Fact]
    public async Task Key_file_never_contains_the_raw_key_material_in_clear()
    {
        var (_, _, keys) = await CreateAsync();
        var raw = keys.Get(1);
        var file = await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "keys", "master.v1.key"));
        Convert.ToHexString(file).ShouldNotContain(Convert.ToHexString(raw));
    }

    [Fact]
    public async Task Rotation_rewraps_every_secret_and_keeps_them_readable()
    {
        var (vault, store, keys) = await CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await vault.StoreAsync($"s{i}", "password", Encoding.UTF8.GetBytes($"value-{i}"), default);
        }

        await vault.RotateAsync(default);
        vault.ActiveKeyVersion.ShouldBe(2);
        keys.ExistingVersions().ShouldBe([1, 2]);
        (await store.ListAllAsync(default)).ShouldAllBe(r => r.KeyVersion == 2);
        (await store.GetActiveKeyVersionAsync(default)).ShouldBe(2);
        for (var i = 0; i < 5; i++)
        {
            Encoding.UTF8.GetString((await vault.ResolveAsync($"s{i}", "password", default))!).ShouldBe($"value-{i}");
        }
    }

    [Fact]
    public async Task Restart_reuses_the_existing_key()
    {
        var (vault, store, _) = await CreateAsync();
        await vault.StoreAsync("a", "password", "persisted"u8.ToArray(), default);
        var vault2 = new SecretVault(store, new MasterKeyStore(Path.Combine(_dir.Path, "keys"), new TestKeyProtector()), new FakeClock());
        await vault2.InitializeAsync(default);
        vault2.ActiveKeyVersion.ShouldBe(1);
        Encoding.UTF8.GetString((await vault2.ResolveAsync("a", "password", default))!).ShouldBe("persisted");
    }

    [Fact]
    public async Task Keys_on_disk_without_database_reference_are_refused()
    {
        var keysDir = Path.Combine(_dir.Path, "keys");
        new MasterKeyStore(keysDir, new TestKeyProtector()).Create(1);
        var db = SqliteDatabase.InDirectory(DatabaseKind.Config, _dir.Path);
        await new MigrationRunner(Path.Combine(_dir.Path, "backup")).MigrateAsync(db, default);
        var vault = new SecretVault(new SecretStore(db), new MasterKeyStore(keysDir, new TestKeyProtector()), new FakeClock());
        await Should.ThrowAsync<InvalidOperationException>(() => vault.InitializeAsync(default));
    }
}

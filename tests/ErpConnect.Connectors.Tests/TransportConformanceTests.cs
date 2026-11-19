using System.Text;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;
using ErpConnect.Connectors.Transports;

namespace ErpConnect.Connectors.Tests;

/// <summary>Conformance commune aux transports de fichiers.</summary>
public abstract class TransportConformanceTests : IAsyncLifetime
{
    protected IFileTransport Transport { get; private set; } = null!;

    protected abstract Task<IFileTransport> CreateAsync();

    public virtual async Task InitializeAsync() => Transport = await CreateAsync();

    public virtual async Task DisposeAsync() => await Transport.DisposeAsync();

    private static Func<Stream, CancellationToken, Task> Content(string s) => (st, ct) => st.WriteAsync(Encoding.UTF8.GetBytes(s), ct).AsTask();

    private async Task<string> Read(string path)
    {
        await using var s = await Transport.OpenReadAsync(path, CancellationToken.None);
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task Write_then_list_and_read()
    {
        await Transport.WriteAtomicAsync("in/a.csv", WriteMode.CreateNew, Content("A"), CancellationToken.None);
        await Transport.WriteAtomicAsync("in/b.csv", WriteMode.CreateNew, Content("B"), CancellationToken.None);
        await Transport.WriteAtomicAsync("in/c.txt", WriteMode.CreateNew, Content("C"), CancellationToken.None);
        var files = await Transport.ListAsync("in/*.csv", CancellationToken.None);
        files.Select(f => f.Name).ShouldBe(["in/a.csv", "in/b.csv"]);
        (await Read("in/b.csv")).ShouldBe("B");
    }

    [Fact]
    public async Task Failed_write_leaves_nothing_visible()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => Transport.WriteAtomicAsync("out/x.json", WriteMode.CreateNew, async (s, ct) =>
        {
            await s.WriteAsync("partiel"u8.ToArray(), ct);
            throw new InvalidOperationException("panne");
        }, CancellationToken.None));
        (await Transport.ExistsAsync("out/x.json", CancellationToken.None)).ShouldBeFalse();
        (await Transport.ListAsync("out/*", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_new_refuses_existing_file_and_overwrite_replaces_it()
    {
        await Transport.WriteAtomicAsync("f.txt", WriteMode.CreateNew, Content("1"), CancellationToken.None);
        (await Should.ThrowAsync<ConnectorException>(() => Transport.WriteAtomicAsync("f.txt", WriteMode.CreateNew, Content("2"), CancellationToken.None)))
            .Kind.ShouldBe(ErrorKind.Permanent);
        await Transport.WriteAtomicAsync("f.txt", WriteMode.Overwrite, Content("3"), CancellationToken.None);
        (await Read("f.txt")).ShouldBe("3");
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("a/./b")]
    [InlineData("nul\0.txt")]
    public async Task Path_escape_is_refused(string path)
    {
        await Should.ThrowAsync<ConnectorException>(() => Transport.WriteAtomicAsync(path, WriteMode.Overwrite, Content("x"), CancellationToken.None));
        await Should.ThrowAsync<ConnectorException>(() => Transport.OpenReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Move_and_delete()
    {
        await Transport.WriteAtomicAsync("m.txt", WriteMode.CreateNew, Content("M"), CancellationToken.None);
        await Transport.MoveAsync("m.txt", "archive/m.txt", CancellationToken.None);
        (await Transport.ExistsAsync("m.txt", CancellationToken.None)).ShouldBeFalse();
        (await Read("archive/m.txt")).ShouldBe("M");
        await Transport.DeleteAsync("archive/m.txt", CancellationToken.None);
        (await Transport.ExistsAsync("archive/m.txt", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_file_is_a_permanent_error()
    {
        (await Should.ThrowAsync<ConnectorException>(() => Transport.OpenReadAsync("absent.csv", CancellationToken.None))).Kind.ShouldBe(ErrorKind.Permanent);
        (await Transport.ListAsync("absent/*.csv", CancellationToken.None)).ShouldBeEmpty();
    }
}

public sealed class LocalFolderConformance : TransportConformanceTests
{
    private readonly TempDir _dir = new();

    protected override Task<IFileTransport> CreateAsync() => Task.FromResult<IFileTransport>(new LocalFolderTransport(_dir.Path));

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public async Task Symbolic_link_leaving_the_root_is_refused()
    {
        using var outside = new TempDir();
        await File.WriteAllTextAsync(outside.File("secret.txt"), "S");
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "lien"), outside.Path);
        File.CreateSymbolicLink(Path.Combine(_dir.Path, "f.txt"), outside.File("secret.txt"));
        await Should.ThrowAsync<ConnectorException>(() => Transport.OpenReadAsync("lien/secret.txt", CancellationToken.None));
        await Should.ThrowAsync<ConnectorException>(() => Transport.OpenReadAsync("f.txt", CancellationToken.None));
        (await Transport.ListAsync("*", CancellationToken.None)).ShouldBeEmpty();
    }
}

[Trait("Category", "Sftp")]
public sealed class SftpConformance : TransportConformanceTests, IClassFixture<SftpServerFixture>
{
    private readonly SftpServerFixture _server;
    private readonly string _root = "/conformance-" + Guid.NewGuid().ToString("N");

    public SftpConformance(SftpServerFixture server) => _server = server;

    protected override Task<IFileTransport> CreateAsync()
    {
        Directory.CreateDirectory(_server.DataPath + _root);
        return Task.FromResult<IFileTransport>(new SftpTransport(_server.Options(_server.DataPath + _root)));
    }
}

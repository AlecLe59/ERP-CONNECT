using System.Text;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Connectors.Transports;

namespace ErpConnect.Connectors.Tests;

[Trait("Category", "Ftp")]
public sealed class FtpSecurityTests : IClassFixture<FtpServerFixture>
{
    private readonly FtpServerFixture _server;

    public FtpSecurityTests(FtpServerFixture server) => _server = server;

    private static Task Probe(FtpTransport t) => t.ListAsync("*", CancellationToken.None);

    [Fact]
    public async Task Pinned_certificate_is_accepted()
    {
        await using var t = new FtpTransport(_server.Options("/"));
        await t.WriteAtomicAsync("pin.txt", WriteMode.Overwrite, (s, ct) => s.WriteAsync(Encoding.UTF8.GetBytes("ok"), ct).AsTask(), CancellationToken.None);
        File.ReadAllText(Path.Combine(_server.DataPath, "pin.txt")).ShouldBe("ok");
    }

    [Fact]
    public async Task Wrong_pin_is_refused()
    {
        await using var t = new FtpTransport(_server.Options("/", fingerprint: new string('A', 64)));
        var ex = await Should.ThrowAsync<ConnectorException>(() => Probe(t));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
    }

    [Fact]
    public async Task Self_signed_certificate_without_pin_is_refused()
    {
        await using var t = new FtpTransport(_server.Options("/", fingerprint: string.Empty));
        await Should.ThrowAsync<ConnectorException>(() => Probe(t));
    }

    [Fact]
    public async Task Bad_password_is_a_permanent_error()
    {
        await using var t = new FtpTransport(_server.Options("/", password: "faux"));
        var ex = await Should.ThrowAsync<ConnectorException>(() => Probe(t));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
    }

    [Fact]
    public void Plain_ftp_is_refused_unless_explicitly_allowed()
    {
        Should.Throw<ConnectorException>(() => new FtpTransport(_server.Options("/", encryption: "none")));
    }

    [Fact]
    public async Task Server_requiring_tls_rejects_a_plain_client_even_when_allowed()
    {
        await using var t = new FtpTransport(_server.Options("/", encryption: "none", allowPlain: true));
        await Should.ThrowAsync<ConnectorException>(() => Probe(t));
    }

    [Theory]
    [InlineData("AB:CD", false)]
    [InlineData("", false)]
    public void Fingerprint_format_is_strict(string pin, bool ok) => FtpTransport.FingerprintMatches(pin, new string('A', 64)).ShouldBe(ok);

    [Fact]
    public void Fingerprint_accepts_colons_and_lowercase()
    {
        var hex = new string('a', 64);
        var colons = string.Join(':', Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2)));
        FtpTransport.FingerprintMatches(colons, hex.ToUpperInvariant()).ShouldBeTrue();
    }
}

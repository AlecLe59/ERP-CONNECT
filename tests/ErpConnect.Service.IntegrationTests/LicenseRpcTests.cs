using System.Security.Cryptography;
using ErpConnect.Contracts;
using ErpConnect.Security.Licensing;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Service.IntegrationTests;

/// <summary>Phase 7 par le pipe : activation hors ligne et code ENTITLEMENT_DENIED.</summary>
public sealed class LicenseRpcTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ECDsa _vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeClock _clock = new() { UtcNow = DateTimeOffset.UtcNow };
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() =>
        _engine = await EngineHarness.StartAsync(_dir.Path, _clock, licenseVerifier: new LicenseVerifier([_vendor.ExportSubjectPublicKeyInfo()]));

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Dispose();
    }

    public void Dispose() => _vendor.Dispose();

    [Fact]
    public async Task Offline_activation_by_installation_id()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var status = (await c.CallAsync("license.get")).Result();
        status["state"]!.GetValue<string>().ShouldBe("evaluation");
        status["canVerify"]!.GetValue<bool>().ShouldBeTrue();
        var installation = status["installationId"]!.GetValue<string>();

        // Évaluation terminée : l'exécution est refusée avec le code dédié.
        _clock.Advance(TimeSpan.FromDays(31));
        (await c.CallAsync("runs.start", new RunStartParams("absent", false))).ErrorCode().ShouldBe(RpcErrorCodes.EntitlementDenied);

        var bad = await c.CallAsync("license.import", new LicenseImportParams("""{"format":1,"payload":"e30=","signature":"AAAA"}"""));
        bad.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);

        var file = LicenseVerifier.Sign(new LicensePayload(1, "L-42", "Client SA", "Standard", installation, _clock.UtcNow, null, ["scheduler", "scripts"], null), _vendor);
        var imported = (await c.CallAsync("license.import", new LicenseImportParams(file))).Result();
        imported["state"]!.GetValue<string>().ShouldBe("licensed");
        imported["license"]!["customer"]!.GetValue<string>().ShouldBe("Client SA");
        (await c.CallAsync("runs.start", new RunStartParams("absent", false))).ErrorCode().ShouldBe(RpcErrorCodes.NotFound); // le droit est revenu : seul le flux manque

        (await c.CallAsync("license.remove")).Result()["deleted"]!.GetValue<bool>().ShouldBeTrue();
        (await c.CallAsync("license.get")).Result()["state"]!.GetValue<string>().ShouldBe("evaluationExpired");
    }

    [Fact]
    public async Task Licence_content_is_never_echoed_in_the_audit_log()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var installation = (await c.CallAsync("license.get")).Result()["installationId"]!.GetValue<string>();
        var file = LicenseVerifier.Sign(new LicensePayload(1, "L-43", "Client SA", "Standard", installation, _clock.UtcNow, null, [], null), _vendor);
        (await c.CallAsync("license.import", new LicenseImportParams(file))).Result();
        var audit = (await c.CallAsync("audit.query", new AuditQueryParams(1, 50, "license.import", null, null))).Result()["items"]!.AsArray();
        audit.Single()!.ToJsonString().ShouldNotContain(System.Text.Json.Nodes.JsonNode.Parse(file)!["signature"]!.GetValue<string>());
    }
}

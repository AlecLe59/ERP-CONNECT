using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ErpConnect.Security.Licensing;

namespace ErpConnect.Security.Tests;

public sealed class LicenseVerifierTests : IDisposable
{
    private readonly ECDsa _vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _vendor.Dispose();
        _attacker.Dispose();
    }

    private LicenseVerifier Verifier() => new([_vendor.ExportSubjectPublicKeyInfo()]);

    private static LicensePayload Payload(string installation = "inst-1") =>
        new(1, "L-1", "Client", "Standard", installation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), ["scheduler"], 10);

    [Fact]
    public void Valid_licence_round_trips()
    {
        var p = Verifier().Verify(LicenseVerifier.Sign(Payload(), _vendor));
        (p.LicenseId, p.InstallationId, p.MaxPublishedFlows).ShouldBe(("L-1", "inst-1", (int?)10));
        p.Features.ShouldBe(["scheduler"]);
    }

    [Fact]
    public void Licence_signed_by_another_key_is_refused()
    {
        Should.Throw<LicenseException>(() => Verifier().Verify(LicenseVerifier.Sign(Payload(), _attacker))).Message.ShouldContain("Signature");
    }

    [Theory]
    [InlineData("installationId", "inst-2")]
    [InlineData("maxPublishedFlows", null)]
    [InlineData("expiresAt", "2099-01-01T00:00:00+00:00")]
    public void Any_change_to_the_signed_content_is_detected(string field, string? value)
    {
        var file = JsonNode.Parse(LicenseVerifier.Sign(Payload(), _vendor))!;
        var payload = JsonNode.Parse(Convert.FromBase64String(file["payload"]!.GetValue<string>()))!;
        payload[field] = value is null ? null : JsonValue.Create(value);
        file["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        Should.Throw<LicenseException>(() => Verifier().Verify(file.ToJsonString())).Message.ShouldContain("Signature");
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("""{"format":1,"payload":"!!!","signature":"AAAA"}""")]
    [InlineData("""{"format":2,"payload":"e30=","signature":"AAAA"}""")]
    [InlineData("""{"format":1}""")]
    public void Garbage_is_refused_cleanly(string content)
    {
        Should.Throw<LicenseException>(() => Verifier().Verify(content));
    }

    [Fact]
    public void Oversized_file_is_refused()
    {
        Should.Throw<LicenseException>(() => Verifier().Verify(new string('a', LicenseVerifier.MaxFileLength + 1)));
    }

    [Fact]
    public void Build_without_vendor_key_cannot_verify_anything()
    {
        var v = new LicenseVerifier([]);
        v.HasTrustedKey.ShouldBeFalse();
        Should.Throw<LicenseException>(() => v.Verify(LicenseVerifier.Sign(Payload(), _vendor))).Message.ShouldContain("aucune clé");
        LicenseVerifier.FromBuild().HasTrustedKey.ShouldBeFalse(); // le dépôt ne contient aucune clé d'éditeur
    }

    [Fact]
    public void Only_p256_keys_are_accepted()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Should.Throw<ArgumentException>(() => new LicenseVerifier([p384.ExportSubjectPublicKeyInfo()]));
    }

    [Fact]
    public void Signature_in_der_format_is_refused()
    {
        var file = JsonNode.Parse(LicenseVerifier.Sign(Payload(), _vendor))!;
        var payload = Convert.FromBase64String(file["payload"]!.GetValue<string>());
        file["signature"] = Convert.ToBase64String(_vendor.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Should.Throw<LicenseException>(() => Verifier().Verify(file.ToJsonString()));
    }
}

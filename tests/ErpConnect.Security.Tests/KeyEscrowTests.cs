using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ErpConnect.Security.Backup;

namespace ErpConnect.Security.Tests;

public class KeyEscrowTests
{
    private const string Phrase = "correct horse battery staple";

    private static Dictionary<int, byte[]> Keys() => new() { [1] = RandomNumberGenerator.GetBytes(32), [2] = RandomNumberGenerator.GetBytes(32) };

    [Fact]
    public void Round_trip_restores_every_key_version()
    {
        var keys = Keys();
        var restored = KeyEscrow.Import(KeyEscrow.Export(keys, Phrase, Argon2Parameters.ForTests), Phrase);
        restored.Keys.Order().ShouldBe([1, 2]);
        restored[2].ShouldBe(keys[2]);
    }

    [Fact]
    public void Export_never_contains_the_keys_or_the_passphrase_in_clear()
    {
        var keys = Keys();
        var text = KeyEscrow.Export(keys, Phrase, Argon2Parameters.ForTests);
        text.ShouldNotContain(Convert.ToBase64String(keys[1]));
        text.ShouldNotContain(Phrase);
        JsonNode.Parse(text)!["kdf"]!.GetValue<string>().ShouldBe("argon2id");
    }

    [Fact]
    public void Wrong_passphrase_is_refused()
    {
        var text = KeyEscrow.Export(Keys(), Phrase, Argon2Parameters.ForTests);
        Should.Throw<CryptographicException>(() => KeyEscrow.Import(text, Phrase + "!")).Message.ShouldContain("Phrase de passe incorrecte");
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("salt")]
    public void Tampering_is_detected(string field)
    {
        var node = JsonNode.Parse(KeyEscrow.Export(Keys(), Phrase, Argon2Parameters.ForTests))!;
        var bytes = Convert.FromBase64String(node[field]!.GetValue<string>());
        bytes[0] ^= 1;
        node[field] = Convert.ToBase64String(bytes);
        Should.Throw<CryptographicException>(() => KeyEscrow.Import(node.ToJsonString(), Phrase));
    }

    [Fact]
    public void Lowering_the_kdf_cost_in_the_file_breaks_authentication()
    {
        // Les paramètres font partie des données associées : les modifier pour accélérer une attaque rend l'export invalide.
        var node = JsonNode.Parse(KeyEscrow.Export(Keys(), Phrase, Argon2Parameters.ForTests with { Iterations = 2 }))!;
        node["iterations"] = 1;
        Should.Throw<CryptographicException>(() => KeyEscrow.Import(node.ToJsonString(), Phrase));
    }

    [Theory]
    [InlineData(4 * 1024 * 1024, 3, 1)] // 4 Gio : refusé avant toute dérivation (déni de service)
    [InlineData(65536, 1000, 1)]
    [InlineData(65536, 3, 64)]
    [InlineData(1024, 3, 1)]
    public void Out_of_bounds_parameters_are_refused_before_deriving(int memory, int iterations, int parallelism)
    {
        var node = JsonNode.Parse(KeyEscrow.Export(Keys(), Phrase, Argon2Parameters.ForTests))!;
        node["memoryKiB"] = memory;
        node["iterations"] = iterations;
        node["parallelism"] = parallelism;
        Should.Throw<CryptographicException>(() => KeyEscrow.Import(node.ToJsonString(), Phrase)).Message.ShouldContain("hors bornes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("court")]
    [InlineData("11 caractèr")]
    public void Short_passphrases_are_refused(string phrase)
    {
        Should.Throw<ArgumentException>(() => KeyEscrow.Export(Keys(), phrase, Argon2Parameters.ForTests));
    }

    [Fact]
    public void Default_parameters_follow_owasp_minimums() =>
        (Argon2Parameters.Default.MemoryKiB >= 19 * 1024 && Argon2Parameters.Default.Iterations >= 2 && Argon2Parameters.Default.WithinBounds).ShouldBeTrue();
}

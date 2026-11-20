using System.Security.Cryptography;
using System.Text;
using ErpConnect.Security.Vault;

namespace ErpConnect.Security.Tests;

public class SecretCipherTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trip()
    {
        var s = SecretCipher.Encrypt(Key, "conn/sql1", "password", 1, "P@ssw0rd!"u8);
        s.Nonce.Length.ShouldBe(12);
        s.Tag.Length.ShouldBe(16);
        Encoding.UTF8.GetString(SecretCipher.Decrypt(Key, "conn/sql1", "password", 1, s)).ShouldBe("P@ssw0rd!");
    }

    [Fact]
    public void Nonce_is_never_reused()
    {
        var nonces = Enumerable.Range(0, 200).Select(_ => Convert.ToHexString(SecretCipher.Encrypt(Key, "a", "u", 1, "x"u8).Nonce)).ToHashSet();
        nonces.Count.ShouldBe(200);
    }

    // S10 : un octet modifié n'importe où est détecté, sans plantage.
    [Fact]
    public void Any_single_byte_change_is_detected()
    {
        var s = SecretCipher.Encrypt(Key, "id", "usage", 1, "secret-value-123"u8);
        foreach (var part in new[] { s.Nonce, s.Ciphertext, s.Tag })
        {
            for (var i = 0; i < part.Length; i++)
            {
                part[i] ^= 0x01;
                Should.Throw<SecretIntegrityException>(() => SecretCipher.Decrypt(Key, "id", "usage", 1, s));
                part[i] ^= 0x01;
            }
        }

        SecretCipher.Decrypt(Key, "id", "usage", 1, s).Length.ShouldBe(16);
    }

    // S10 : un secret déplacé vers une autre ligne, ou relu avec un autre usage ou une autre version de clé, est refusé.
    [Theory]
    [InlineData("other-id", "usage", 1)]
    [InlineData("id", "other-usage", 1)]
    [InlineData("id", "usage", 2)]
    public void Moved_or_repurposed_secret_is_refused(string id, string usage, int version)
    {
        var s = SecretCipher.Encrypt(Key, "id", "usage", 1, "abc"u8);
        var ex = Should.Throw<SecretIntegrityException>(() => SecretCipher.Decrypt(Key, id, usage, version, s));
        ex.Message.ShouldNotContain("abc");
    }

    [Fact]
    public void Wrong_key_is_refused()
    {
        var s = SecretCipher.Encrypt(Key, "id", "u", 1, "abc"u8);
        Should.Throw<SecretIntegrityException>(() => SecretCipher.Decrypt(RandomNumberGenerator.GetBytes(32), "id", "u", 1, s));
    }
}

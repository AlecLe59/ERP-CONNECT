using ErpConnect.Security.Logging;

namespace ErpConnect.Security.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void Registered_secret_is_masked_everywhere()
    {
        var r = new SecretRedactor();
        r.Register("Hunter2-Witness!");
        r.Redact("Connexion refusée avec Hunter2-Witness! (essai Hunter2-Witness!)")
            .ShouldBe("Connexion refusée avec *** (essai ***)");
    }

    [Theory]
    [InlineData("Server=x;Password=abc123;User=y", "Server=x;Password=***;User=y")]
    [InlineData("token: eyJhbGciOi", "token: ***")]
    [InlineData("api_key=\"k-1 2 3\"", "api_key=***")]
    [InlineData("pwd='p w'", "pwd=***")]
    public void Common_patterns_are_masked(string input, string expected) =>
        new SecretRedactor().Redact(input).ShouldBe(expected);

    [Fact]
    public void Ordinary_text_is_left_untouched() =>
        new SecretRedactor().Redact("Base config.db migrée en v1").ShouldBe("Base config.db migrée en v1");
}

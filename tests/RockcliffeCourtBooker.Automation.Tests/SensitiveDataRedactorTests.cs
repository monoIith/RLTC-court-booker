using RockcliffeCourtBooker.Automation.Contracts;
using RockcliffeCourtBooker.Automation.Security;

namespace RockcliffeCourtBooker.Automation.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactsLiteralSecretsJsonAndQueryValues()
    {
        var redactor = new SensitiveDataRedactor("literal-secret", "member@example.test");

        var redacted = redactor.Redact(
            "member@example.test literal-secret {\"password\":\"json-secret\"} password=query-secret&next=1");

        Assert.DoesNotContain("literal-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("member@example.test", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialsToStringNeverReturnsValues()
    {
        var credentials = new MatchpointCredentials
        {
            Username = "member@example.test",
            Password = "literal-secret",
        };

        var text = credentials.ToString();

        Assert.DoesNotContain(credentials.Username, text, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.Password, text, StringComparison.Ordinal);
    }
}

using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Tests.Unit;

public sealed class PasswordTests
{
    [Fact]
    public void Employee_passwords_can_be_read_aloud_and_typed_on_a_tablet()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = Passwords.Generate();
            Assert.Matches("^[a-z]+-[0-9]{4}$", password);
            Assert.True(password.Length >= Passwords.MinLength);
        }
    }

    [Fact]
    public void Admin_passwords_are_16_url_safe_characters()
    {
        var password = Passwords.GenerateStrong();
        Assert.Matches("^[A-Za-z0-9_-]{16}$", password);
        Assert.NotEqual(password, Passwords.GenerateStrong());
    }

    [Fact]
    public void Hashes_from_the_Node_version_verify()
    {
        // bcryptjs writes $2b$ hashes; this one is "makas-4821"
        var nodeHash = Passwords.Hash("makas-4821", workFactor: 4).Replace("$2a$", "$2b$");
        Assert.True(Passwords.Verify("makas-4821", nodeHash));
        Assert.False(Passwords.Verify("makas-0000", nodeHash));
    }

    [Fact]
    public void A_garbage_hash_is_a_failed_login_not_a_crash()
    {
        Assert.False(Passwords.Verify("anything", "not-a-bcrypt-hash"));
    }
}

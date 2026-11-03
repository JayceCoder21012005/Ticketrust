using TicketRush.Application.Features.Auth.Register;
using TicketRush.Application.Features.Auth.ResetPassword;
using TicketRush.Application.Features.Users.UpdateProfile;

namespace TicketRush.Application.Tests.Auth;

public sealed class ValidatorTests
{
    private static readonly RegisterValidator Register = new();

    [Theory]
    [InlineData("short1abc")] // 9 characters
    [InlineData("onlyletterspassword")]
    [InlineData("1234567890123")]
    public void Register_rejects_weak_passwords_with_auth_weak_password(string password)
    {
        var result = Register.Validate(new RegisterCommand("an@example.com", password, "An Nguyen"));

        result.Errors.ShouldContain(e => e.PropertyName == "Password" && e.ErrorCode == "auth.weak_password");
    }

    [Fact]
    public void Register_rejects_a_password_equal_to_the_email()
    {
        var result = Register.Validate(new RegisterCommand("an12345678@example.com", "AN12345678@example.com", "An"));

        result.Errors.ShouldContain(e => e.PropertyName == "Password" && e.ErrorCode == "auth.weak_password");
    }

    [Fact]
    public void Register_accepts_ten_characters_with_letters_and_digits()
    {
        Register.Validate(new RegisterCommand("an@example.com", "matkhau123", "An Nguyen")).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not-an-email", "An")]
    [InlineData("an@example.com", "")]
    [InlineData("an@example.com", "   ")]
    public void Register_requires_an_email_and_a_full_name(string email, string fullName)
    {
        Register.Validate(new RegisterCommand(email, "matkhau123", fullName)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Reset_password_applies_the_same_password_rules()
    {
        var result = new ResetPasswordValidator().Validate(new ResetPasswordCommand("an@example.com", "token", "weak"));

        result.Errors.ShouldContain(e => e.PropertyName == "NewPassword" && e.ErrorCode == "auth.weak_password");
    }

    [Theory]
    [InlineData("0901234567", true)]
    [InlineData("+84901234567", true)]
    [InlineData(null, true)]
    [InlineData("12345", false)]
    [InlineData("09012345ab", false)]
    public void Update_profile_accepts_only_vietnamese_phone_numbers(string? phone, bool valid)
    {
        var result = new UpdateProfileValidator().Validate(new UpdateProfileCommand(Guid.CreateVersion7(), "An Nguyen", phone, null));

        result.IsValid.ShouldBe(valid);
    }

    [Fact]
    public void Update_profile_limits_the_full_name_to_100_characters()
    {
        var result = new UpdateProfileValidator().Validate(new UpdateProfileCommand(Guid.CreateVersion7(), new string('a', 101), null, null));

        result.IsValid.ShouldBeFalse();
    }
}

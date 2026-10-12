namespace VetPlatform.Api.Features.Identity;

/// <summary>
/// Rules for new passwords (NIST SP 800-63B style): length matters, composition rules do not. The upper bound
/// keeps hashing cost bounded.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 1024;

    /// <summary>Returns why the password is unacceptable, or null when it is fine.</summary>
    public static string? Validate(string password, string email)
    {
        if (password.Length < MinLength)
        {
            return $"The password must be at least {MinLength} characters.";
        }

        if (password.Length > MaxLength)
        {
            return $"The password must be at most {MaxLength} characters.";
        }

        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "The password must not be the email address.";
        }

        return null;
    }
}

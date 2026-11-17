using System.Net.Mail;
using System.Text.RegularExpressions;
using Auction.Domain.Errors;

namespace Auction.Domain.Identity;

/// <summary>Field rules for Register (requirement R3.1). Returns one issue per bad field.</summary>
public static partial class RegistrationRules
{
    public const int UsernameMin = 3;
    public const int UsernameMax = 32;
    public const int EmailMax = 254;
    public const int PasswordMin = 12;
    public const int PasswordMax = 128;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex UsernamePattern();

    public static IReadOnlyList<FieldIssue> Validate(string? username, string? email, string? password)
    {
        username ??= "";
        email ??= "";
        password ??= "";

        var issues = new List<FieldIssue>();

        if (username.Length < UsernameMin || username.Length > UsernameMax)
        {
            issues.Add(new FieldIssue("username", "length", $"Username must be {UsernameMin} to {UsernameMax} characters."));
        }
        else if (!UsernamePattern().IsMatch(username))
        {
            issues.Add(new FieldIssue("username", "pattern", "Username can only use letters, digits and _."));
        }

        if (!IsEmail(email))
        {
            issues.Add(new FieldIssue("email", "format", "Enter a valid email address."));
        }

        if (password.Length < PasswordMin)
        {
            issues.Add(new FieldIssue("password", "min_length", $"Password must be at least {PasswordMin} characters."));
        }
        else if (password.Length > PasswordMax)
        {
            issues.Add(new FieldIssue("password", "max_length", $"Password must be at most {PasswordMax} characters."));
        }

        return issues;
    }

    public static bool IsEmail(string email)
    {
        if (email.Length == 0 || email.Length > EmailMax)
        {
            return false;
        }

        try
        {
            var address = new MailAddress(email);
            return address.Address == email && address.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

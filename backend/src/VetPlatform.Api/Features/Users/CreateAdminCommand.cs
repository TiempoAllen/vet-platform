using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Users;

/// <summary>
/// Creates a new clinic and its first administrator from the command line (there is deliberately no public sign-up
/// endpoint). The password is read from standard input, never from arguments, so it does not land in shell history:
/// <code>dotnet run --project src/VetPlatform.Api -- create-admin --clinic "Happy Paws" --name "Jo Vet" --email jo@happypaws.example</code>
/// </summary>
public static class CreateAdminCommand
{
    public const string Name = "create-admin";

    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var options = ParseOptions(args);
        if (options is null
            || !options.TryGetValue("clinic", out var clinicName) || string.IsNullOrWhiteSpace(clinicName)
            || !options.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)
            || !options.TryGetValue("email", out var rawEmail) || string.IsNullOrWhiteSpace(rawEmail))
        {
            await output.WriteLineAsync(
                $"Usage: {Name} --clinic <clinic name> --name <admin name> --email <admin email>" +
                "  (the password is read from stdin; never pass it as an argument)");
            return 2;
        }

        var email = User.NormalizeEmail(rawEmail);
        if (clinicName.Length > Clinic.NameMaxLength || name.Length > User.NameMaxLength
            || email.Length > User.EmailMaxLength || !new EmailAddressAttribute().IsValid(email))
        {
            await output.WriteLineAsync("Error: clinic name, admin name or email is too long or invalid.");
            return 2;
        }

        if (!Console.IsInputRedirected && ReferenceEquals(input, Console.In))
        {
            await output.WriteAsync("Password for the administrator: ");
        }

        var password = (Console.IsInputRedirected || !ReferenceEquals(input, Console.In)
            ? await input.ReadLineAsync(cancellationToken)
            : ReadHiddenLine()) ?? string.Empty;

        if (PasswordPolicy.Validate(password, email) is { } problem)
        {
            await output.WriteLineAsync("Error: " + problem);
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditTrail>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Email is unique across clinics (ADR-003); no clinic is current in a CLI process.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(user => user.Email == email, cancellationToken))
        {
            await output.WriteLineAsync("Error: a user with this email already exists.");
            return 1;
        }

        var clinic = new Clinic { Name = clinicName.Trim() };
        var admin = new User
        {
            ClinicId = clinic.Id,
            Name = name.Trim(),
            Email = email,
            PasswordHash = "",
            Roles = [new UserRole { Role = Role.Admin, CreatedAt = now }],
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);

        db.Clinics.Add(clinic);
        db.Users.Add(admin);
        audit.RecordFor(
            AuditActions.UserCreated, nameof(User), admin.Id,
            new { roles = new[] { nameof(Role.Admin) }, source = "cli" }, actorUserId: null, clinicId: clinic.Id);
        await db.SaveChangesAsync(cancellationToken);

        await output.WriteLineAsync($"Created clinic {clinic.Id} and administrator {admin.Id}.");
        return 0;
    }

    private static readonly string[] KnownOptions = ["clinic", "name", "email"];

    /// <summary>
    /// Parses <c>--key value</c> pairs for the known options; returns null on anything else. Unknown options are
    /// refused rather than ignored, so a mistaken <c>--password</c> fails loudly instead of being silently accepted.
    /// </summary>
    private static Dictionary<string, string>? ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count
                || !KnownOptions.Contains(args[i][2..], StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            options[args[i][2..]] = args[i + 1];
        }

        return options;
    }

    private static string ReadHiddenLine()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}

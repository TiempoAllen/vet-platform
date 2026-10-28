using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace VetPlatform.Api.Common.Storage;

/// <summary>
/// Private storage for uploaded file content (ADR-015). Content is addressed only by a random key that the database
/// holds; nothing here is ever served directly or executed. One provider today (local disk); an S3-compatible one
/// follows when hosting is chosen (X-05).
/// </summary>
public interface IFileStore
{
    /// <summary>Stores new content under <paramref name="key"/>; never overwrites existing content.</summary>
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    /// <summary>The stored content, or null if there is none under the key.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes content (used only to clean up after a failed save); missing content is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Storage keys: 32 random lowercase hex characters, so they can never form a path or be guessed.</summary>
public static partial class StorageKeys
{
    public static string New() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyPattern();
}

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Only <c>Local</c> today.</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>
    /// Directory for the local provider: absolute, or relative to the content root. It must not be served by any web
    /// server and should be backed up with the database (RUNBOOK).
    /// </summary>
    public string? LocalRoot { get; set; }
}

/// <summary>Keeps files under <c>LocalRoot/xx/key</c>, written to a temporary name first and then moved into place.</summary>
public sealed class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(IOptions<FileStorageOptions> options, IHostEnvironment environment)
    {
        _root = Path.GetFullPath(options.Value.LocalRoot!, environment.ContentRootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{StorageKeys.New()}.tmp";
        try
        {
            await using (var file = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary); // no-op after a successful move
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    private string PathOf(string key)
    {
        if (!StorageKeys.IsValid(key))
        {
            throw new ArgumentException("Not a storage key.", nameof(key));
        }

        return Path.Combine(_root, key[..2], key);
    }
}

public static class FileStorageServiceCollectionExtensions
{
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .Validate(o => o.Provider == "Local", "FileStorage:Provider must be 'Local' (the only provider so far).")
            .Validate(o => !string.IsNullOrWhiteSpace(o.LocalRoot), "FileStorage:LocalRoot must be set.")
            .ValidateOnStart();
        services.AddSingleton<IFileStore, LocalFileStore>();
        return services;
    }
}

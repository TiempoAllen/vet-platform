namespace VetPlatform.Api.Common.Persistence;

/// <summary>
/// Base for persistent entities (docs/DATABASE.md section 1, ADR-002): a time-ordered UUIDv7 id generated in the
/// application, and created/updated timestamps maintained by <see cref="TimestampsInterceptor"/>.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

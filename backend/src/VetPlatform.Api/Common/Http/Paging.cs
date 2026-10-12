using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace VetPlatform.Api.Common.Http;

/// <summary>Page query parameters (docs/API.md section 13). Out-of-range values are a 422, not silently clamped.</summary>
public class PageQuery
{
    public const int MaxPageSize = 100;

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = 25;
}

public sealed record PaginationInfo(int Page, int PageSize, int Total, int TotalPages);

/// <summary>The standard list envelope: <c>{ "data": [...], "pagination": {...} }</c>.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Data, PaginationInfo Pagination);

public static class PagingExtensions
{
    /// <summary>Counts, fetches one page of an ordered query, and maps each row in memory.</summary>
    public static async Task<PagedResponse<TResult>> ToPageAsync<T, TResult>(
        this IQueryable<T> query,
        PageQuery page,
        Func<T, TResult> map,
        CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<TResult>(
            rows.Select(map).ToList(),
            new PaginationInfo(page.Page, page.PageSize, total, (int)Math.Ceiling(total / (double)page.PageSize)));
    }

    /// <summary>
    /// Escape character used by <see cref="ContainsPattern"/>. Pass it to <c>EF.Functions.Like/ILike</c>: Npgsql
    /// otherwise emits <c>ESCAPE ''</c>, which disables escaping.
    /// </summary>
    public const string LikeEscape = @"\";

    /// <summary>Escapes LIKE wildcards so user input is matched literally. Use with <see cref="LikeEscape"/>.</summary>
    public static string ContainsPattern(string term) =>
        "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
}

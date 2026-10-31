using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VetPlatform.Api.Common.Authorization;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Tests.TestSupport;

namespace VetPlatform.Api.Tests;

/// <summary>
/// Rolling the B-20 migration back and forward (its own database). Going forward gives the B-19 receipts their running
/// balance; going back is refused once B-20 history exists (ADR-020).
/// </summary>
[Trait("Category", "Database")]
public sealed class StockMovementsMigrationTests(DatabaseApiFactory factory) : IClassFixture<DatabaseApiFactory>
{
    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ForwardBackfillsTheRunningBalance_AndRollbackIsRefusedOnceHistoryExists()
    {
        var (stock, _) = await factory.SignedInAsync(Role.Inventory);
        var product = (await OkAsync(await stock.PostAsJsonAsync("/api/products", new { sku = "SUP", name = "Gauze", type = "Supply", unit = "Pack" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        foreach (var quantity in new[] { 3, 2, 4 })
        {
            await OkAsync(await stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Receive", productId = product, quantity, lotNumber = quantity == 4 ? "B" : "A" }), HttpStatusCode.Created);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<AppDbContext>().GetService<IMigrator>();
        await migrator.MigrateAsync("AddProductsAndStock");
        await migrator.MigrateAsync();

        await factory.UpdateDatabaseAsync(async db => Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = 'ck_stock_movements_witness' AND contype = 'c'").SingleAsync()));

        var register = await stock.GetFromJsonAsync<JsonElement>($"/api/products/{product}/register");
        Assert.Equal([3m, 5m, 4m], register.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("balanceAfter").GetDecimal()));

        var lot = register.GetProperty("data")[0].GetProperty("lot").GetProperty("id").GetGuid();
        await OkAsync(await stock.PostAsJsonAsync("/api/inventory/movements", new { type = "Sell", stockLotId = lot, quantity = 1 }), HttpStatusCode.Created);
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("AddProductsAndStock"));
        await factory.UpdateDatabaseAsync(async db => Assert.Empty(await db.Database.GetPendingMigrationsAsync()));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Billing;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Inventory;

/// <summary>
/// A catalogue entry of the clinic (docs/DATABASE.md section 8, ADR-019): a stocked good (medication, vaccine, supply,
/// retail item) or a non-stocked <see cref="ProductType.Service"/>. Products are deactivated, never deleted. The type
/// and unit are fixed once the product has a stock lot.
/// </summary>
public sealed class Product : Entity, IClinicOwned
{
    public const int SkuMaxLength = 50;
    public const int NameMaxLength = 200;
    public const int CategoryMaxLength = 100;
    public const decimal MaxReorderLevel = 1_000_000m;

    /// <summary>Unique index: one SKU per clinic, ignoring case (inactive products keep theirs).</summary>
    public const string SkuIndex = "ux_products_clinic_id_sku";

    public Guid ClinicId { get; set; }

    /// <summary>Letters, digits and <c>-._/</c>, starting with a letter or digit; compared ignoring case.</summary>
    public required string Sku { get; set; }

    public required string Name { get; set; }

    /// <summary>Optional free text, compared ignoring case.</summary>
    public string? Category { get; set; }

    public ProductType Type { get; set; }

    public ProductUnit Unit { get; set; }

    /// <summary>Stock at or below which the product needs reordering (used by B-22); never set on services.</summary>
    public decimal? ReorderLevel { get; set; }

    /// <summary>Suggested unit price in the clinic currency, for invoice lines; 0 or more, at most 4 decimals.</summary>
    public decimal? DefaultUnitPrice { get; set; }

    /// <summary>Suggested tax rate for invoice lines; active when set (it may be archived later).</summary>
    public Guid? DefaultTaxRateId { get; set; }

    public TaxRate? DefaultTaxRate { get; set; }

    public ProductStatus Status { get; set; } = ProductStatus.Active;

    public bool HoldsStock => Type != ProductType.Service;

    /// <summary>Medications and vaccines are received only with an expiry date.</summary>
    public bool RequiresExpiry => Type is ProductType.Medication or ProductType.Vaccine;
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum ProductType
{
    Medication,
    Vaccine,
    Supply,
    Retail,

    /// <summary>Billed but never stocked (consultation fee, surgery): no lots, no movements.</summary>
    Service,
}

/// <summary>Unit of measure that quantities of the product are counted in. Stored by name.</summary>
public enum ProductUnit
{
    Each,
    Tablet,
    Capsule,
    Millilitre,
    Litre,
    Gram,
    Kilogram,
    Dose,
    Vial,
    Ampoule,
    Bottle,
    Box,
    Pack,
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum ProductStatus
{
    Active,

    /// <summary>Not received into or added to new invoice lines; existing stock and links stay.</summary>
    Inactive,
}

/// <summary>
/// Stock of one product held as one lot: the same lot number, expiry date and location (each optional, compared
/// ignoring case). Its quantity changes only with a <see cref="StockMovement"/> in the same transaction, and the
/// database checks at commit that it equals the sum of the lot's movements. Lots are never deleted.
/// </summary>
public sealed class StockLot : Entity, IClinicOwned
{
    public const int LotNumberMaxLength = 50;
    public const int LocationMaxLength = 100;

    /// <summary>Upper bound for a lot's quantity, well inside numeric(15,3).</summary>
    public const decimal MaxQuantity = 1_000_000_000m;

    /// <summary>Unique index on the lot's identity (nulls not distinct).</summary>
    public const string IdentityIndex = "ux_stock_lots_identity";

    public Guid ClinicId { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public string? LotNumber { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public string? Location { get; set; }

    /// <summary>On hand, in the product's unit; 0 or more, at most 3 decimals.</summary>
    public decimal Quantity { get; set; }
}

/// <summary>
/// A recorded change of a lot's quantity (docs/DATABASE.md section 8). Append-only: never edited or deleted. B-19 records
/// only <see cref="StockMovementType.Receive"/>; B-20 adds the outgoing types and the negative-stock rule (SRS Q-5).
/// </summary>
public sealed class StockMovement : Entity, IClinicOwned
{
    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const decimal MaxQuantity = 1_000_000m;
    public const decimal MaxUnitCost = 1_000_000_000m;

    public Guid ClinicId { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>The lot changed; the composite key makes the database guarantee it is a lot of <see cref="ProductId"/>.</summary>
    public Guid StockLotId { get; set; }

    public StockLot? StockLot { get; set; }

    public StockMovementType Type { get; set; }

    /// <summary>Signed change of the lot's quantity (positive for stock coming in), at most 3 decimals.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Optional purchase cost per unit, in <see cref="Currency"/>.</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>The clinic currency when the cost was recorded; set exactly when <see cref="UnitCost"/> is.</summary>
    public string? Currency { get; set; }

    /// <summary>E.g. the supplier's delivery note or invoice number.</summary>
    public string? Reference { get; set; }

    public string? Notes { get; set; }

    /// <summary>When the stock arrived: now or earlier.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public Guid PerformedByUserId { get; set; }

    public User? PerformedBy { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration. B-20 adds the outgoing types.</summary>
public enum StockMovementType
{
    Receive,
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_sku", "sku COLLATE \"C\" ~ '^[A-Za-z0-9][A-Za-z0-9._/-]*$'"); // regexes need a deterministic collation
            table.HasCheckConstraint("ck_products_name", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_products_category", "category IS NULL OR btrim(category) <> ''");
            table.HasCheckConstraint("ck_products_type", "type IN ('Medication', 'Vaccine', 'Supply', 'Retail', 'Service')");
            table.HasCheckConstraint(
                "ck_products_unit",
                "unit IN ('Each', 'Tablet', 'Capsule', 'Millilitre', 'Litre', 'Gram', 'Kilogram', 'Dose', 'Vial', 'Ampoule', 'Bottle', 'Box', 'Pack')");
            table.HasCheckConstraint("ck_products_status", "status IN ('Active', 'Inactive')");
            table.HasCheckConstraint(
                "ck_products_reorder_level",
                $"reorder_level IS NULL OR (type <> 'Service' AND reorder_level >= 0 AND reorder_level <= {Product.MaxReorderLevel})");
            table.HasCheckConstraint(
                "ck_products_default_unit_price",
                $"default_unit_price IS NULL OR (default_unit_price >= 0 AND default_unit_price <= {InvoiceItem.MaxUnitPrice})");
        });
        builder.Property(p => p.Sku).HasMaxLength(Product.SkuMaxLength).UseCollation(AppDbContext.CaseInsensitiveCollation);
        builder.Property(p => p.Name).HasMaxLength(Product.NameMaxLength);
        builder.Property(p => p.Category).HasMaxLength(Product.CategoryMaxLength).UseCollation(AppDbContext.CaseInsensitiveCollation);
        builder.Property(p => p.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Unit).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.ReorderLevel).HasPrecision(12, 3);
        builder.Property(p => p.DefaultUnitPrice).HasPrecision(19, 4);
        builder.Ignore(p => p.HoldsStock);
        builder.Ignore(p => p.RequiresExpiry);

        builder.HasOne(p => p.DefaultTaxRate).WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.DefaultTaxRateId })
            .HasPrincipalKey(t => new { t.ClinicId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(p => p.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Lots and invoice lines reference a product of their own clinic through this key (ADR-005).
        builder.HasAlternateKey(p => new { p.ClinicId, p.Id });
        builder.HasIndex(p => new { p.ClinicId, p.Sku }).HasDatabaseName(Product.SkuIndex).IsUnique();
        builder.HasIndex(p => new { p.ClinicId, p.Name });
    }
}

internal sealed class StockLotConfiguration : IEntityTypeConfiguration<StockLot>
{
    public void Configure(EntityTypeBuilder<StockLot> builder)
    {
        builder.ToTable("stock_lots", table =>
        {
            table.HasCheckConstraint("ck_stock_lots_quantity", $"quantity >= 0 AND quantity <= {StockLot.MaxQuantity}");
            table.HasCheckConstraint("ck_stock_lots_lot_number", "lot_number IS NULL OR btrim(lot_number) <> ''");
            table.HasCheckConstraint("ck_stock_lots_location", "location IS NULL OR btrim(location) <> ''");
            table.HasCheckConstraint("ck_stock_lots_expires_on", "expires_on IS NULL OR expires_on BETWEEN '2000-01-01' AND '2100-12-31'");
        });
        builder.Property(l => l.LotNumber).HasMaxLength(StockLot.LotNumberMaxLength).UseCollation(AppDbContext.CaseInsensitiveCollation);
        builder.Property(l => l.Location).HasMaxLength(StockLot.LocationMaxLength).UseCollation(AppDbContext.CaseInsensitiveCollation);
        builder.Property(l => l.Quantity).HasPrecision(15, 3);

        builder.HasOne(l => l.Product).WithMany()
            .HasForeignKey(l => new { l.ClinicId, l.ProductId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(l => l.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Movements reference a lot of their own product through this key.
        builder.HasAlternateKey(l => new { l.ClinicId, l.ProductId, l.Id });
        builder.HasIndex(l => new { l.ClinicId, l.ProductId, l.LotNumber, l.ExpiresOn, l.Location })
            .HasDatabaseName(StockLot.IdentityIndex)
            .IsUnique()
            .AreNullsDistinct(false);
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements", table =>
        {
            table.HasCheckConstraint("ck_stock_movements_type", "type IN ('Receive')");
            table.HasCheckConstraint(
                "ck_stock_movements_quantity",
                $"quantity <> 0 AND abs(quantity) <= {StockMovement.MaxQuantity} AND (type <> 'Receive' OR quantity > 0)");
            table.HasCheckConstraint(
                "ck_stock_movements_unit_cost",
                $"(unit_cost IS NULL) = (currency IS NULL) AND (unit_cost IS NULL OR (unit_cost >= 0 AND unit_cost <= {StockMovement.MaxUnitCost}))");
            table.HasCheckConstraint("ck_stock_movements_currency", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_stock_movements_reference", "reference IS NULL OR btrim(reference) <> ''");
            table.HasCheckConstraint("ck_stock_movements_notes", "notes IS NULL OR btrim(notes) <> ''");
        });
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.Quantity).HasPrecision(12, 3);
        builder.Property(m => m.UnitCost).HasPrecision(19, 4);
        builder.Property(m => m.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(m => m.Reference).HasMaxLength(StockMovement.ReferenceMaxLength);
        builder.Property(m => m.Notes).HasMaxLength(StockMovement.NotesMaxLength);

        builder.HasOne(m => m.Product).WithMany()
            .HasForeignKey(m => new { m.ClinicId, m.ProductId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.StockLot).WithMany()
            .HasForeignKey(m => new { m.ClinicId, m.ProductId, m.StockLotId })
            .HasPrincipalKey(l => new { l.ClinicId, l.ProductId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.PerformedBy).WithMany()
            .HasForeignKey(m => new { m.ClinicId, m.PerformedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(m => m.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // A product's or a lot's movements, newest first; the lot key also serves the guard's sums.
        builder.HasIndex(m => new { m.ClinicId, m.ProductId, m.OccurredAt });
        builder.HasIndex(m => new { m.ClinicId, m.ProductId, m.StockLotId, m.OccurredAt }).HasDatabaseName("ix_stock_movements_lot_occurred_at");
        builder.HasIndex(m => new { m.ClinicId, m.OccurredAt, m.Id }); // the unfiltered list, newest first
    }
}

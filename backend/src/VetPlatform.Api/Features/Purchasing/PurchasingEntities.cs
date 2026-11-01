using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Billing;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Inventory;

namespace VetPlatform.Api.Features.Purchasing;

/// <summary>
/// A supplier of the clinic (docs/DATABASE.md section 8, ADR-021). Deactivated, never deleted. Contact details are
/// business data, but audit metadata still names only the changed fields.
/// </summary>
public sealed class Supplier : Entity, IClinicOwned
{
    public const int NameMaxLength = 200;
    public const int AccountNumberMaxLength = 50;
    public const int NotesMaxLength = 2000;

    /// <summary>Unique index: one supplier per name in a clinic, ignoring case (inactive ones included).</summary>
    public const string NameIndex = "ux_suppliers_clinic_id_name";

    public Guid ClinicId { get; set; }

    public required string Name { get; set; }

    public string? ContactName { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    public string? Country { get; set; }

    /// <summary>The clinic's customer number with this supplier.</summary>
    public string? AccountNumber { get; set; }

    public string? Notes { get; set; }

    public SupplierStatus Status { get; set; } = SupplierStatus.Active;
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum SupplierStatus
{
    Active,

    /// <summary>Not used for new orders or movements; existing orders carry on.</summary>
    Inactive,
}

/// <summary>
/// An order to a supplier (ADR-021). A <see cref="PurchaseOrderStatus.Draft"/> is freely editable and has no number;
/// ordering assigns the clinic's next gapless number and freezes the lines; deliveries are received against the lines
/// as stock movements, which move the status between Ordered, PartiallyReceived and Received.
/// </summary>
public sealed class PurchaseOrder : Entity, IClinicOwned
{
    public const int NotesMaxLength = 2000;
    public const int ReasonMaxLength = 500;
    public const int MaxItems = 200;

    /// <summary>Partial unique index: one order per number in a clinic.</summary>
    public const string NumberIndex = "ux_purchase_orders_clinic_number";

    public Guid ClinicId { get; set; }

    public Guid SupplierId { get; set; }

    public Supplier? Supplier { get; set; }

    /// <summary>Gapless per clinic, assigned when ordered; null for drafts.</summary>
    public int? Number { get; set; }

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    /// <summary>ISO 4217, copied from the clinic when the order is created.</summary>
    public required string Currency { get; set; }

    public string? Notes { get; set; }

    /// <summary>The sum of the line totals (costs exclude tax).</summary>
    public decimal Total { get; set; }

    public DateTimeOffset? OrderedAt { get; set; }

    public Guid? OrderedByUserId { get; set; }

    public User? OrderedBy { get; set; }

    /// <summary>When the last outstanding quantity arrived; null unless Received.</summary>
    public DateTimeOffset? ReceivedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public User? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedByUserId { get; set; }

    public User? ClosedBy { get; set; }

    public string? CloseReason { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedBy { get; set; }

    public List<PurchaseOrderItem> Items { get; set; } = [];

    /// <summary>Accepts deliveries.</summary>
    public bool IsOpen => Status is PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>
    /// After a delivery or a reversal: an order still taking deliveries (or fully received) gets the status its lines
    /// give; a closed order stays closed.
    /// </summary>
    public void ApplyReceivingStatus(DateTimeOffset now)
    {
        if (IsOpen || Status == PurchaseOrderStatus.Received)
        {
            var status = ReceivingStatus;
            ReceivedAt = status == PurchaseOrderStatus.Received ? (Status == PurchaseOrderStatus.Received ? ReceivedAt : now) : null;
            Status = status;
        }
    }

    /// <summary>The receiving status the lines' received quantities give (for an order no longer a draft).</summary>
    public PurchaseOrderStatus ReceivingStatus =>
        Items.All(i => i.QuantityReceived == 0) ? PurchaseOrderStatus.Ordered
        : Items.All(i => i.QuantityReceived == i.Quantity) ? PurchaseOrderStatus.Received
        : PurchaseOrderStatus.PartiallyReceived;
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum PurchaseOrderStatus
{
    Draft,
    Ordered,
    PartiallyReceived,
    Received,

    /// <summary>Withdrawn before anything arrived.</summary>
    Cancelled,

    /// <summary>Part arrived and the rest will not come.</summary>
    Closed,
}

/// <summary>
/// One product line of an order (no DbSet: reached through the order). Changed only while the order is a draft, except
/// <see cref="QuantityReceived"/>, which the database keeps equal to the line's receipts minus their reversals.
/// </summary>
public sealed class PurchaseOrderItem : Entity, IClinicOwned
{
    public Guid ClinicId { get; set; }

    public Guid PurchaseOrderId { get; set; }

    public int Position { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>In the product's unit; above 0, at most 3 decimals.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Cost per unit before tax, in the order currency; at most 4 decimals.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>quantity × unit cost, rounded to the currency.</summary>
    public decimal LineTotal { get; set; }

    public decimal QuantityReceived { get; set; }

    public decimal QuantityOutstanding => Quantity - QuantityReceived;
}

/// <summary>The next order number of a clinic; its row is locked while a number is assigned, so numbers are gapless.</summary>
public sealed class PurchaseOrderNumberSequence
{
    public Guid ClinicId { get; set; }

    public int NextNumber { get; set; } = 1;
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("suppliers", table =>
        {
            table.HasCheckConstraint("ck_suppliers_name", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_suppliers_status", "status IN ('Active', 'Inactive')");
        });
        builder.Property(s => s.Name).HasMaxLength(Supplier.NameMaxLength).UseCollation(AppDbContext.CaseInsensitiveCollation);
        builder.Property(s => s.ContactName).HasMaxLength(Supplier.NameMaxLength);
        builder.Property(s => s.Email).HasMaxLength(Client.EmailMaxLength);
        builder.Property(s => s.Phone).HasMaxLength(Client.PhoneMaxLength);
        builder.Property(s => s.AddressLine1).HasMaxLength(Client.AddressLineMaxLength);
        builder.Property(s => s.AddressLine2).HasMaxLength(Client.AddressLineMaxLength);
        builder.Property(s => s.City).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(s => s.Region).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(s => s.PostalCode).HasMaxLength(Client.PostalCodeMaxLength);
        builder.Property(s => s.Country).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(s => s.AccountNumber).HasMaxLength(Supplier.AccountNumberMaxLength);
        builder.Property(s => s.Notes).HasMaxLength(Supplier.NotesMaxLength);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(s => s.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasAlternateKey(s => new { s.ClinicId, s.Id });
        builder.HasIndex(s => new { s.ClinicId, s.Name }).HasDatabaseName(Supplier.NameIndex).IsUnique();
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders", table =>
        {
            table.HasCheckConstraint(
                "ck_purchase_orders_status",
                "status IN ('Draft', 'Ordered', 'PartiallyReceived', 'Received', 'Cancelled', 'Closed')");
            // Each state carries exactly its own fields.
            table.HasCheckConstraint(
                "ck_purchase_orders_lifecycle",
                "(status = 'Draft') = (number IS NULL) " +
                "AND (status = 'Draft') = (ordered_at IS NULL) AND (ordered_at IS NULL) = (ordered_by_user_id IS NULL) " +
                "AND (status = 'Received') = (received_at IS NOT NULL) AND (received_at IS NULL OR received_at >= ordered_at) " +
                "AND (status = 'Cancelled') = (cancelled_at IS NOT NULL) " +
                "AND (cancelled_at IS NULL) = (cancelled_by_user_id IS NULL) AND (cancelled_at IS NULL) = (cancel_reason IS NULL) " +
                "AND (cancelled_at IS NULL OR cancelled_at >= ordered_at) " +
                "AND (status = 'Closed') = (closed_at IS NOT NULL) " +
                "AND (closed_at IS NULL) = (closed_by_user_id IS NULL) AND (closed_at IS NULL) = (close_reason IS NULL) " +
                "AND (closed_at IS NULL OR closed_at >= ordered_at)");
            table.HasCheckConstraint("ck_purchase_orders_number", "number > 0");
            table.HasCheckConstraint("ck_purchase_orders_currency", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_purchase_orders_total", $"total >= 0 AND total <= {Invoice.MaxAmount}");
            table.HasCheckConstraint("ck_purchase_orders_reasons", "btrim(cancel_reason) <> '' AND btrim(close_reason) <> ''");
        });
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(o => o.Notes).HasMaxLength(PurchaseOrder.NotesMaxLength);
        builder.Property(o => o.Total).HasPrecision(19, 4);
        builder.Property(o => o.CancelReason).HasMaxLength(PurchaseOrder.ReasonMaxLength);
        builder.Property(o => o.CloseReason).HasMaxLength(PurchaseOrder.ReasonMaxLength);
        builder.Ignore(o => o.IsOpen);
        builder.Ignore(o => o.ReceivingStatus);

        builder.HasOne(o => o.Supplier).WithMany()
            .HasForeignKey(o => new { o.ClinicId, o.SupplierId })
            .HasPrincipalKey(s => new { s.ClinicId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.OrderedBy).WithMany()
            .HasForeignKey(o => new { o.ClinicId, o.OrderedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.CancelledBy).WithMany()
            .HasForeignKey(o => new { o.ClinicId, o.CancelledByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.ClosedBy).WithMany()
            .HasForeignKey(o => new { o.ClinicId, o.ClosedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(o => o.CreatedBy).WithMany()
            .HasForeignKey(o => new { o.ClinicId, o.CreatedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(o => o.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Lines reference their order of the same clinic through this key (ADR-005).
        builder.HasAlternateKey(o => new { o.ClinicId, o.Id });
        builder.HasMany(o => o.Items).WithOne()
            .HasForeignKey(i => new { i.ClinicId, i.PurchaseOrderId })
            .HasPrincipalKey(o => new { o.ClinicId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => new { o.ClinicId, o.Number }).HasDatabaseName(PurchaseOrder.NumberIndex).IsUnique().HasFilter("number IS NOT NULL");
        builder.HasIndex(o => new { o.ClinicId, o.CreatedAt });
        builder.HasIndex(o => new { o.ClinicId, o.SupplierId, o.CreatedAt });
    }
}

internal sealed class PurchaseOrderItemConfiguration : IEntityTypeConfiguration<PurchaseOrderItem>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderItem> builder)
    {
        builder.ToTable("purchase_order_items", table =>
        {
            table.HasCheckConstraint("ck_purchase_order_items_position", "position > 0");
            table.HasCheckConstraint("ck_purchase_order_items_quantity", $"quantity > 0 AND quantity <= {StockMovement.MaxQuantity}");
            table.HasCheckConstraint("ck_purchase_order_items_unit_cost", $"unit_cost >= 0 AND unit_cost <= {StockMovement.MaxUnitCost}");
            table.HasCheckConstraint("ck_purchase_order_items_line_total", $"line_total >= 0 AND line_total <= {Invoice.MaxAmount}");
            table.HasCheckConstraint("ck_purchase_order_items_quantity_received", "quantity_received >= 0 AND quantity_received <= quantity");
        });
        builder.Property(i => i.Quantity).HasPrecision(12, 3);
        builder.Property(i => i.UnitCost).HasPrecision(19, 4);
        builder.Property(i => i.LineTotal).HasPrecision(19, 4);
        builder.Property(i => i.QuantityReceived).HasPrecision(12, 3);
        builder.Ignore(i => i.QuantityOutstanding);

        builder.HasOne(i => i.Product).WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.ProductId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(i => i.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Movements reference a line of their own product through this key.
        builder.HasAlternateKey(i => new { i.ClinicId, i.ProductId, i.Id }).HasName("ak_purchase_order_items_clinic_id_product_id_id");
        builder.HasIndex(i => new { i.ClinicId, i.PurchaseOrderId, i.Position }).IsUnique();
        builder.HasIndex(i => new { i.ClinicId, i.PurchaseOrderId, i.ProductId }).IsUnique();
    }
}

internal sealed class PurchaseOrderNumberSequenceConfiguration : IEntityTypeConfiguration<PurchaseOrderNumberSequence>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderNumberSequence> builder)
    {
        builder.ToTable("purchase_order_number_sequences", table =>
            table.HasCheckConstraint("ck_purchase_order_number_sequences_next_number", "next_number > 0"));
        builder.HasKey(s => s.ClinicId);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(s => s.ClinicId).OnDelete(DeleteBehavior.Restrict);
    }
}

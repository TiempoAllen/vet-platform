using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Patients;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// A named tax rate of the clinic (e.g. "Standard 20%", "Zero 0%"), managed by administrators (ADR-016). The
/// percentage never changes once created; a line copies it, so later changes never alter an invoice. Rates are
/// archived, not deleted.
/// </summary>
public sealed class TaxRate : Entity, IClinicOwned
{
    public const int NameMaxLength = 100;

    /// <summary>Partial unique index: one active rate per name (ignoring case) in a clinic.</summary>
    public const string ActiveNameIndex = "ux_tax_rates_active_name";

    public Guid ClinicId { get; set; }

    public required string Name { get; set; }

    /// <summary>0 to 100, at most 4 decimals.</summary>
    public decimal RatePercent { get; set; }

    public TaxRateStatus Status { get; set; } = TaxRateStatus.Active;
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum TaxRateStatus
{
    Active,

    /// <summary>No longer offered for new lines; lines that already use it keep their copy.</summary>
    Archived,
}

/// <summary>
/// An invoice to a client, optionally for one patient (docs/DATABASE.md section 7, ADR-016). A <see cref="InvoiceStatus.Draft"/>
/// is freely editable and has no number; issuing assigns the next gapless number of the clinic and freezes it; an
/// issued invoice can only be voided. Totals are always computed by the server from the lines.
/// </summary>
public sealed class Invoice : Entity, IClinicOwned
{
    public const int NotesMaxLength = 2000;
    public const int ReasonMaxLength = 500;
    public const int MaxItems = 200;

    /// <summary>
    /// Upper bound for a line total and an invoice total, well inside numeric(19,4) (15 integer digits) even with 200
    /// lines and a 100% tax rate.
    /// </summary>
    public const decimal MaxAmount = 1_000_000_000_000m;

    /// <summary>Partial unique index: one invoice per number in a clinic.</summary>
    public const string NumberIndex = "ux_invoices_clinic_number";

    public Guid ClinicId { get; set; }

    public Guid ClientId { get; set; }

    public Client? Client { get; set; }

    public Guid? PatientId { get; set; }

    public Patient? Patient { get; set; }

    /// <summary>Gapless per clinic, assigned on issue; null for drafts.</summary>
    public int? Number { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    /// <summary>ISO 4217, copied from the clinic when the invoice is created.</summary>
    public required string Currency { get; set; }

    /// <summary>Printed on the invoice.</summary>
    public string? Notes { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountTotal { get; set; }

    public decimal TaxTotal { get; set; }

    public decimal Total { get; set; }

    /// <summary>
    /// Payments minus refunds (B-17, ADR-017), kept by the server in the same transaction as each payment or refund;
    /// the database checks it against the payment and refund rows.
    /// </summary>
    public decimal AmountPaid { get; set; }

    public decimal BalanceDue => Total - AmountPaid;

    /// <summary>Only issued invoices have one: drafts are not owed yet and void invoices never are.</summary>
    public PaymentStatus? PaymentStatus =>
        Status != InvoiceStatus.Issued ? null
        : AmountPaid == 0 && Total > 0 ? Billing.PaymentStatus.Unpaid
        : AmountPaid < Total ? Billing.PaymentStatus.PartiallyPaid
        : Billing.PaymentStatus.Paid;

    // ---------- Bill-to block, frozen when the invoice is issued (B-18, ADR-018); null on drafts ----------

    /// <summary>The client's name as printed ("First Last").</summary>
    public string? BillToName { get; set; }

    public string? BillToAddressLine1 { get; set; }

    public string? BillToAddressLine2 { get; set; }

    public string? BillToCity { get; set; }

    public string? BillToRegion { get; set; }

    public string? BillToPostalCode { get; set; }

    public string? BillToCountry { get; set; }

    public string? BillToPatientName { get; set; }

    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>The clinic-local day payment is due; set on issue (the issue day unless given).</summary>
    public DateOnly? DueOn { get; set; }

    public Guid? IssuedByUserId { get; set; }

    public User? IssuedBy { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedByUserId { get; set; }

    public User? VoidedBy { get; set; }

    public string? VoidReason { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedBy { get; set; }

    public List<InvoiceItem> Items { get; set; } = [];
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum InvoiceStatus
{
    Draft,
    Issued,

    /// <summary>Issued in error: kept with its number, never counted as owed.</summary>
    Void,
}

/// <summary>
/// One line of an invoice. A child of <see cref="Invoice"/> (no DbSet, SDD section 8): changed only through it and only
/// while it is a draft. The amounts are computed by <see cref="InvoiceCalculator"/> and rounded to the currency.
/// </summary>
public sealed class InvoiceItem : Entity, IClinicOwned
{
    public const int DescriptionMaxLength = 500;
    public const decimal MaxQuantity = 1_000_000m;
    public const decimal MaxUnitPrice = 1_000_000_000m;

    public Guid ClinicId { get; set; }

    public Guid InvoiceId { get; set; }

    /// <summary>Order on the invoice (1, 2, ...).</summary>
    public int Position { get; set; }

    public required string Description { get; set; }

    /// <summary>More than 0, at most 3 decimals.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Before tax and discount; 0 or more, at most 4 decimals.</summary>
    public decimal UnitPrice { get; set; }

    public DiscountType DiscountType { get; set; } = DiscountType.None;

    /// <summary>A percentage (0 to 100) or an amount in the invoice currency; 0 when there is no discount.</summary>
    public decimal DiscountValue { get; set; }

    public Guid TaxRateId { get; set; }

    /// <summary>Copies of the rate when the line was written, so archiving or renaming the rate changes nothing here.</summary>
    public required string TaxRateName { get; set; }

    public decimal TaxRatePercent { get; set; }

    /// <summary>Optional: a consultation of the invoice's patient that this line bills.</summary>
    public Guid? ConsultationId { get; set; }

    /// <summary>
    /// Optional: the catalogue product this line bills (B-19, ADR-019); active when linked. The description and price
    /// are still the line's own, and billing does not change stock.
    /// </summary>
    public Guid? ProductId { get; set; }

    /// <summary>quantity × unit price, rounded.</summary>
    public decimal Net { get; set; }

    public decimal Discount { get; set; }

    public decimal Tax { get; set; }

    /// <summary>net − discount + tax.</summary>
    public decimal Total { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum DiscountType
{
    None,
    Percent,
    Amount,
}

/// <summary>The next invoice number of a clinic; its row is locked while a number is assigned, so numbers are gapless.</summary>
public sealed class InvoiceNumberSequence
{
    public Guid ClinicId { get; set; }

    public int NextNumber { get; set; } = 1;
}

internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> builder)
    {
        builder.ToTable("tax_rates", table =>
        {
            table.HasCheckConstraint("ck_tax_rates_status", "status IN ('Active', 'Archived')");
            table.HasCheckConstraint("ck_tax_rates_name", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_tax_rates_rate_percent", "rate_percent BETWEEN 0 AND 100");
        });
        builder.Property(t => t.Name).HasMaxLength(TaxRate.NameMaxLength);
        builder.Property(t => t.RatePercent).HasPrecision(7, 4);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(t => t.ClinicId).OnDelete(DeleteBehavior.Restrict);

        // Lines reference a rate of their own clinic through this key (ADR-005).
        builder.HasAlternateKey(t => new { t.ClinicId, t.Id });
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices", table =>
        {
            table.HasCheckConstraint("ck_invoices_status", "status IN ('Draft', 'Issued', 'Void')");
            table.HasCheckConstraint(
                "ck_invoices_lifecycle",
                "CASE status " +
                "WHEN 'Draft' THEN number IS NULL AND issued_at IS NULL AND due_on IS NULL AND issued_by_user_id IS NULL " +
                "AND bill_to_name IS NULL AND bill_to_patient_name IS NULL AND bill_to_address_line1 IS NULL " +
                "AND bill_to_address_line2 IS NULL AND bill_to_city IS NULL AND bill_to_region IS NULL " +
                "AND bill_to_postal_code IS NULL AND bill_to_country IS NULL " +
                "AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL " +
                "WHEN 'Issued' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL " +
                "AND bill_to_name IS NOT NULL AND (patient_id IS NULL) = (bill_to_patient_name IS NULL) " +
                "AND voided_at IS NULL AND voided_by_user_id IS NULL AND void_reason IS NULL " +
                "WHEN 'Void' THEN number IS NOT NULL AND issued_at IS NOT NULL AND due_on IS NOT NULL AND issued_by_user_id IS NOT NULL " +
                "AND bill_to_name IS NOT NULL AND (patient_id IS NULL) = (bill_to_patient_name IS NULL) " +
                "AND voided_at IS NOT NULL AND voided_at >= issued_at AND voided_by_user_id IS NOT NULL " +
                "AND void_reason IS NOT NULL AND btrim(void_reason) <> '' " +
                "ELSE false END");
            table.HasCheckConstraint("ck_invoices_number", "number > 0");
            // Only an issued invoice holds money; it is voided only after every payment is refunded (ADR-017).
            table.HasCheckConstraint(
                "ck_invoices_amount_paid", "amount_paid >= 0 AND amount_paid <= total AND (status = 'Issued' OR amount_paid = 0)");
            table.HasCheckConstraint("ck_invoices_currency", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint(
                "ck_invoices_totals",
                "subtotal >= 0 AND discount_total >= 0 AND discount_total <= subtotal AND tax_total >= 0 " +
                $"AND total = subtotal - discount_total + tax_total AND total <= {Invoice.MaxAmount}");
        });
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(i => i.Notes).HasMaxLength(Invoice.NotesMaxLength);
        builder.Property(i => i.VoidReason).HasMaxLength(Invoice.ReasonMaxLength);
        builder.Property(i => i.Subtotal).HasPrecision(19, 4);
        builder.Property(i => i.DiscountTotal).HasPrecision(19, 4);
        builder.Property(i => i.TaxTotal).HasPrecision(19, 4);
        builder.Property(i => i.Total).HasPrecision(19, 4);
        builder.Property(i => i.AmountPaid).HasPrecision(19, 4);
        builder.Property(i => i.BillToName).HasMaxLength(2 * Client.NameMaxLength + 1);
        builder.Property(i => i.BillToAddressLine1).HasMaxLength(Client.AddressLineMaxLength);
        builder.Property(i => i.BillToAddressLine2).HasMaxLength(Client.AddressLineMaxLength);
        builder.Property(i => i.BillToCity).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(i => i.BillToRegion).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(i => i.BillToPostalCode).HasMaxLength(Client.PostalCodeMaxLength);
        builder.Property(i => i.BillToCountry).HasMaxLength(Client.LocalityMaxLength);
        builder.Property(i => i.BillToPatientName).HasMaxLength(Patient.NameMaxLength);
        builder.Ignore(i => i.BalanceDue);
        builder.Ignore(i => i.PaymentStatus);

        builder.HasOne(i => i.Client).WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.ClientId })
            .HasPrincipalKey(c => new { c.ClinicId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.Patient).WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.PatientId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.IssuedBy).WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.IssuedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.VoidedBy).WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.VoidedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.CreatedBy).WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.CreatedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(i => i.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Items).WithOne()
            .HasForeignKey(item => new { item.ClinicId, item.InvoiceId })
            .HasPrincipalKey(i => new { i.ClinicId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.ClinicId, i.Number }).HasDatabaseName(Invoice.NumberIndex).IsUnique().HasFilter("number IS NOT NULL");

        // Lists: by client, by patient, newest first.
        builder.HasIndex(i => new { i.ClinicId, i.ClientId, i.CreatedAt });
        builder.HasIndex(i => new { i.ClinicId, i.PatientId, i.CreatedAt });
        builder.HasIndex(i => new { i.ClinicId, i.CreatedAt });
    }
}

internal sealed class InvoiceItemConfiguration : IEntityTypeConfiguration<InvoiceItem>
{
    public void Configure(EntityTypeBuilder<InvoiceItem> builder)
    {
        builder.ToTable("invoice_items", table =>
        {
            table.HasCheckConstraint("ck_invoice_items_description", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_invoice_items_position", "position > 0");
            table.HasCheckConstraint("ck_invoice_items_quantity", $"quantity > 0 AND quantity <= {InvoiceItem.MaxQuantity}");
            table.HasCheckConstraint("ck_invoice_items_unit_price", $"unit_price >= 0 AND unit_price <= {InvoiceItem.MaxUnitPrice}");
            table.HasCheckConstraint("ck_invoice_items_discount_type", "discount_type IN ('None', 'Percent', 'Amount')");
            table.HasCheckConstraint(
                "ck_invoice_items_discount_value",
                "CASE discount_type WHEN 'None' THEN discount_value = 0 " +
                "WHEN 'Percent' THEN discount_value > 0 AND discount_value <= 100 " +
                "WHEN 'Amount' THEN discount_value > 0 ELSE false END");
            table.HasCheckConstraint("ck_invoice_items_tax_rate_percent", "tax_rate_percent BETWEEN 0 AND 100");
            table.HasCheckConstraint(
                "ck_invoice_items_amounts",
                $"net >= 0 AND discount >= 0 AND discount <= net AND tax >= 0 AND total = net - discount + tax AND total <= {Invoice.MaxAmount}");
        });
        builder.Property(i => i.Description).HasMaxLength(InvoiceItem.DescriptionMaxLength);
        builder.Property(i => i.Quantity).HasPrecision(12, 3);
        builder.Property(i => i.UnitPrice).HasPrecision(19, 4);
        builder.Property(i => i.DiscountType).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.DiscountValue).HasPrecision(19, 4);
        builder.Property(i => i.TaxRateName).HasMaxLength(TaxRate.NameMaxLength);
        builder.Property(i => i.TaxRatePercent).HasPrecision(7, 4);
        builder.Property(i => i.Net).HasPrecision(19, 4);
        builder.Property(i => i.Discount).HasPrecision(19, 4);
        builder.Property(i => i.Tax).HasPrecision(19, 4);
        builder.Property(i => i.Total).HasPrecision(19, 4);

        builder.HasOne<TaxRate>().WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.TaxRateId })
            .HasPrincipalKey(t => new { t.ClinicId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Consultation>().WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.ConsultationId })
            .HasPrincipalKey(c => new { c.ClinicId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Inventory.Product>().WithMany()
            .HasForeignKey(i => new { i.ClinicId, i.ProductId })
            .HasPrincipalKey(p => new { p.ClinicId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(i => i.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.ClinicId, i.InvoiceId, i.Position }).IsUnique();

        // Stock sales reference a line of their own clinic through this key (B-20).
        builder.HasAlternateKey(i => new { i.ClinicId, i.Id });
    }
}

internal sealed class InvoiceNumberSequenceConfiguration : IEntityTypeConfiguration<InvoiceNumberSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceNumberSequence> builder)
    {
        builder.ToTable("invoice_number_sequences", table =>
            table.HasCheckConstraint("ck_invoice_number_sequences_next_number", "next_number > 0"));
        builder.HasKey(s => s.ClinicId);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(s => s.ClinicId).OnDelete(DeleteBehavior.Restrict);
    }
}

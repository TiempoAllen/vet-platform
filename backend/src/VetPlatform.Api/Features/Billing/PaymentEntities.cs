using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// Money received against an issued invoice (docs/DATABASE.md section 7, ADR-017). Never edited, voided or deleted:
/// money returned, or a payment recorded by mistake, is a <see cref="Refund"/> against it. A payment never exceeds the
/// invoice's balance due when it is recorded.
/// </summary>
public sealed class Payment : Entity, IClinicOwned
{
    public const int ReferenceMaxLength = 100;
    public const int MaxPerInvoice = 100;

    public Guid ClinicId { get; set; }

    public Guid InvoiceId { get; set; }

    /// <summary>Above 0, in the invoice currency's minor units.</summary>
    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    /// <summary>E.g. a terminal slip or cheque number; never a card number (refused by the service).</summary>
    public string? Reference { get; set; }

    /// <summary>When the money was received: now, or earlier, but not before the invoice was issued.</summary>
    public DateTimeOffset PaidAt { get; set; }

    public Guid ReceivedByUserId { get; set; }

    public User? ReceivedBy { get; set; }

    public List<Refund> Refunds { get; set; } = [];
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum PaymentMethod
{
    Cash,
    Card,
    BankTransfer,
    Cheque,
    Other,
}

/// <summary>
/// Money returned against one payment (or the correction of a payment recorded by mistake). Never edited or deleted.
/// The refunds of a payment never add up to more than the payment.
/// </summary>
public sealed class Refund : Entity, IClinicOwned
{
    public const int ReasonMaxLength = 500;
    public const int MaxPerPayment = 20;

    public Guid ClinicId { get; set; }

    /// <summary>The payment's invoice; the composite key makes the database guarantee they match.</summary>
    public Guid InvoiceId { get; set; }

    public Guid PaymentId { get; set; }

    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset RefundedAt { get; set; }

    public Guid RefundedByUserId { get; set; }

    public User? RefundedBy { get; set; }
}

/// <summary>Derived from an issued invoice's amount paid; drafts and void invoices have none.</summary>
public enum PaymentStatus
{
    Unpaid,
    PartiallyPaid,
    Paid,
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    private const string Methods = "('Cash', 'Card', 'BankTransfer', 'Cheque', 'Other')";

    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", table =>
        {
            table.HasCheckConstraint("ck_payments_amount", $"amount > 0 AND amount <= {Invoice.MaxAmount}");
            table.HasCheckConstraint("ck_payments_method", $"method IN {Methods}");
            table.HasCheckConstraint("ck_payments_reference", "reference IS NULL OR btrim(reference) <> ''");
        });
        builder.Property(p => p.Amount).HasPrecision(19, 4);
        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Reference).HasMaxLength(Payment.ReferenceMaxLength);

        builder.HasOne<Invoice>().WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.InvoiceId })
            .HasPrincipalKey(i => new { i.ClinicId, i.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.ReceivedBy).WithMany()
            .HasForeignKey(p => new { p.ClinicId, p.ReceivedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(p => p.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Refunds).WithOne()
            .HasForeignKey(r => new { r.ClinicId, r.InvoiceId, r.PaymentId })
            .HasPrincipalKey(p => new { p.ClinicId, p.InvoiceId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // An invoice's payments in order; also serves the invoice key and the guard's sums.
        builder.HasIndex(p => new { p.ClinicId, p.InvoiceId, p.PaidAt });
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds", table =>
        {
            table.HasCheckConstraint("ck_refunds_amount", $"amount > 0 AND amount <= {Invoice.MaxAmount}");
            table.HasCheckConstraint("ck_refunds_method", "method IN ('Cash', 'Card', 'BankTransfer', 'Cheque', 'Other')");
            table.HasCheckConstraint("ck_refunds_reason", "btrim(reason) <> ''");
        });
        builder.Property(r => r.Amount).HasPrecision(19, 4);
        builder.Property(r => r.Method).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Reason).HasMaxLength(Refund.ReasonMaxLength);
        builder.HasOne(r => r.RefundedBy).WithMany()
            .HasForeignKey(r => new { r.ClinicId, r.RefundedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(r => r.ClinicId).OnDelete(DeleteBehavior.Restrict);
    }
}

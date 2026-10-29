using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// The receipt of one payment or one refund (docs/DATABASE.md section 7, ADR-018), created in the same transaction
/// with the next gapless number of the clinic. Everything printed on it is frozen at that moment, so a reprint is
/// always identical; who it is addressed to is the invoice's own frozen bill-to block. Never changed or deleted.
/// </summary>
public sealed class Receipt : Entity, IClinicOwned
{
    public const int NameMaxLength = 200;

    /// <summary>Partial unique index: one receipt per clinic and number.</summary>
    public const string NumberIndex = "ux_receipts_clinic_number";

    public Guid ClinicId { get; set; }

    /// <summary>Gapless per clinic, shared by payment and refund receipts.</summary>
    public int Number { get; set; }

    public ReceiptKind Kind { get; set; }

    public Guid InvoiceId { get; set; }

    public Guid PaymentId { get; set; }

    /// <summary>Set for refund receipts only.</summary>
    public Guid? RefundId { get; set; }

    // ---------- Frozen when the receipt is created ----------

    public required string ClinicName { get; set; }

    public int InvoiceNumber { get; set; }

    public required string Currency { get; set; }

    public decimal InvoiceTotal { get; set; }

    /// <summary>The payment's or refund's amount (always positive; <see cref="Kind"/> says which way it went).</summary>
    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    /// <summary>The payment's reference, if any (never a card number).</summary>
    public string? Reference { get; set; }

    /// <summary>When the money moved: the payment's paidAt or the refund's refundedAt.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public decimal AmountPaidAfter { get; set; }

    public decimal BalanceDueAfter { get; set; }

    public Guid IssuedByUserId { get; set; }

    /// <summary>The cashier's name as printed, not today's.</summary>
    public required string IssuedByName { get; set; }
}

/// <summary>Stored by name, so never rename a member without a data migration.</summary>
public enum ReceiptKind
{
    Payment,
    Refund,
}

/// <summary>The next receipt number of a clinic, updated in the transaction that creates the receipt (gapless).</summary>
public sealed class ReceiptNumberSequence
{
    public Guid ClinicId { get; set; }

    public int NextNumber { get; set; } = 1;
}

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> builder)
    {
        builder.ToTable("receipts", table =>
        {
            table.HasCheckConstraint("ck_receipts_kind", "kind IN ('Payment', 'Refund')");
            table.HasCheckConstraint("ck_receipts_refund", "(kind = 'Refund') = (refund_id IS NOT NULL)");
            table.HasCheckConstraint("ck_receipts_number", "number > 0 AND invoice_number > 0");
            table.HasCheckConstraint("ck_receipts_currency", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_receipts_method", "method IN ('Cash', 'Card', 'BankTransfer', 'Cheque', 'Other')");
            table.HasCheckConstraint(
                "ck_receipts_amounts",
                $"amount > 0 AND amount <= {Invoice.MaxAmount} AND amount_paid_after >= 0 AND amount_paid_after <= invoice_total " +
                "AND balance_due_after = invoice_total - amount_paid_after");
            table.HasCheckConstraint("ck_receipts_names", "btrim(clinic_name) <> '' AND btrim(issued_by_name) <> ''");
        });
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Method).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.ClinicName).HasMaxLength(Clinic.NameMaxLength);
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(r => r.InvoiceTotal).HasPrecision(19, 4);
        builder.Property(r => r.Amount).HasPrecision(19, 4);
        builder.Property(r => r.Reference).HasMaxLength(Payment.ReferenceMaxLength);
        builder.Property(r => r.AmountPaidAfter).HasPrecision(19, 4);
        builder.Property(r => r.BalanceDueAfter).HasPrecision(19, 4);
        builder.Property(r => r.IssuedByName).HasMaxLength(User.NameMaxLength);

        // The payment (and refund) of the same invoice and clinic (ADR-005).
        builder.HasOne<Payment>().WithMany()
            .HasForeignKey(r => new { r.ClinicId, r.InvoiceId, r.PaymentId })
            .HasPrincipalKey(p => new { p.ClinicId, p.InvoiceId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Refund>().WithMany()
            .HasForeignKey(r => new { r.ClinicId, r.PaymentId, r.RefundId })
            .HasPrincipalKey(f => new { f.ClinicId, f.PaymentId, f.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => new { r.ClinicId, r.IssuedByUserId })
            .HasPrincipalKey(u => new { u.ClinicId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(r => r.ClinicId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.ClinicId, r.Number }).HasDatabaseName(Receipt.NumberIndex).IsUnique();

        // One receipt per payment and one per refund.
        builder.HasIndex(r => r.PaymentId).IsUnique().HasFilter("kind = 'Payment'");
        builder.HasIndex(r => r.RefundId).IsUnique().HasFilter("refund_id IS NOT NULL");

        // An invoice's receipts in order.
        builder.HasIndex(r => new { r.ClinicId, r.InvoiceId, r.Number });
    }
}

internal sealed class ReceiptNumberSequenceConfiguration : IEntityTypeConfiguration<ReceiptNumberSequence>
{
    public void Configure(EntityTypeBuilder<ReceiptNumberSequence> builder)
    {
        builder.ToTable("receipt_number_sequences", table =>
            table.HasCheckConstraint("ck_receipt_number_sequences_next_number", "next_number > 0"));
        builder.HasKey(s => s.ClinicId);
        builder.HasOne<Clinic>().WithMany().HasForeignKey(s => s.ClinicId).OnDelete(DeleteBehavior.Restrict);
    }
}

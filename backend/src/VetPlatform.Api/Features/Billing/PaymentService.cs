using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// Payments and refunds (docs/API.md section 10.4, ADR-017). Every change locks the invoice row (after the
/// clinic-filtered lookup), so concurrent payments are applied one after the other and the balance is always exact; the
/// payment or refund row and the invoice's amount paid are saved in one transaction. Nothing here is ever edited or
/// deleted. Audit metadata holds ids, amounts and methods, never references or reasons.
/// </summary>
public sealed partial class PaymentService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<IReadOnlyList<PaymentResponse>> ListAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw NotFound("The invoice was not found.");
        var payments = await WithPeople(db.Payments.AsNoTracking())
            .Where(p => p.InvoiceId == invoiceId)
            .OrderBy(p => p.PaidAt).ThenBy(p => p.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var receipts = new InvoiceReceipts(await db.Receipts.AsNoTracking().Where(r => r.InvoiceId == invoiceId).ToListAsync(cancellationToken));
        return payments.Select(p => PaymentResponse.From(p, invoice, zone, receipts)).ToList();
    }

    public async Task<PaymentResponse> RecordAsync(Guid invoiceId, RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invoice = await LockInvoiceAsync(invoiceId, cancellationToken);
        switch (invoice.Status)
        {
            case InvoiceStatus.Draft:
                throw Conflict("Issue the invoice before recording payments against it.");
            case InvoiceStatus.Void:
                throw Conflict("A void invoice takes no payments.");
        }

        if (await db.Payments.CountAsync(p => p.InvoiceId == invoiceId, cancellationToken) >= Payment.MaxPerInvoice)
        {
            throw Conflict($"An invoice can have at most {Payment.MaxPerInvoice} payments.");
        }

        var now = Now;
        var paidAt = request.PaidAt is { } given ? TimestampsInterceptor.ToDatabasePrecision(given.ToUniversalTime()) : now;
        var problems = new List<ApiErrorDetail>();
        CheckAmount(request.Amount!.Value, invoice, invoice.BalanceDue, "the balance due", problems);
        if (paidAt > now || paidAt < invoice.IssuedAt)
        {
            problems.Add(new ApiErrorDetail("paidAt", "Cannot be in the future or before the invoice was issued."));
        }

        var reference = NullIfBlank(request.Reference);
        if (ContainsCardNumber(reference))
        {
            problems.Add(new ApiErrorDetail("reference", "Never enter a card number; use the terminal slip or authorisation number."));
        }

        ThrowIfAny(problems);
        var payment = new Payment
        {
            ClinicId = invoice.ClinicId,
            InvoiceId = invoice.Id,
            Amount = request.Amount.Value,
            Method = request.Method!.Value,
            Reference = reference,
            PaidAt = paidAt,
            ReceivedByUserId = CallerId,
            ReceivedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
        };
        db.Payments.Add(payment);
        audit.Record(
            AuditActions.PaymentRecorded, nameof(Payment), payment.Id,
            new { invoiceId, amount = payment.Amount, method = payment.Method, currency = invoice.Currency });

        // The row first, then the invoice: the database checks amount_paid against the stored payments and refunds.
        await db.SaveChangesAsync(cancellationToken);
        invoice.AmountPaid += payment.Amount;
        await db.SaveChangesAsync(cancellationToken);
        var receipt = await IssueReceiptAsync(invoice, payment, refund: null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PaymentResponse.From(payment, invoice, zone, new InvoiceReceipts([receipt]));
    }

    /// <summary>Returns money against one payment; the invoice's balance goes up by the same amount.</summary>
    public async Task<PaymentResponse> RefundAsync(Guid invoiceId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invoice = await LockInvoiceAsync(invoiceId, cancellationToken);
        var payment = await WithPeople(db.Payments).AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == paymentId && p.InvoiceId == invoiceId, cancellationToken)
            ?? throw NotFound("The payment was not found.");
        if (payment.Refunds.Count >= Refund.MaxPerPayment)
        {
            throw Conflict($"A payment can have at most {Refund.MaxPerPayment} refunds.");
        }

        var refundable = payment.Amount - payment.Refunds.Sum(r => r.Amount);
        if (refundable == 0)
        {
            throw Conflict("The payment has already been refunded in full.");
        }

        var problems = new List<ApiErrorDetail>();
        CheckAmount(request.Amount!.Value, invoice, refundable, "what remains of the payment", problems);
        if (ContainsCardNumber(request.Reason))
        {
            problems.Add(new ApiErrorDetail("reason", "Never enter a card number; describe the refund without it."));
        }

        ThrowIfAny(problems);

        var refund = new Refund
        {
            ClinicId = invoice.ClinicId,
            InvoiceId = invoice.Id,
            PaymentId = payment.Id,
            Amount = request.Amount.Value,
            Method = request.Method!.Value,
            Reason = request.Reason!.Trim(),
            RefundedAt = Now,
            RefundedByUserId = CallerId,
            RefundedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken),
        };
        payment.Refunds.Add(refund);
        db.Add(refund); // a preset key would otherwise make EF treat it as an existing row
        audit.Record(
            AuditActions.PaymentRefunded, nameof(Payment), payment.Id,
            new { invoiceId, refundId = refund.Id, amount = refund.Amount, method = refund.Method, currency = invoice.Currency });

        await db.SaveChangesAsync(cancellationToken);
        invoice.AmountPaid -= refund.Amount;
        await db.SaveChangesAsync(cancellationToken);
        await IssueReceiptAsync(invoice, payment, refund, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var receipts = await db.Receipts.AsNoTracking().Where(r => r.PaymentId == payment.Id).ToListAsync(cancellationToken);
        return PaymentResponse.From(payment, invoice, zone, new InvoiceReceipts(receipts));
    }

    /// <summary>
    /// Creates the receipt of a payment (or of a refund) in the caller's transaction, with the clinic's next gapless
    /// receipt number (sequence row updated here; lock order: invoice, then receipt sequence) and everything printed
    /// on it frozen now. The invoice's amount paid must already include the change.
    /// </summary>
    private async Task<Receipt> IssueReceiptAsync(Invoice invoice, Payment payment, Refund? refund, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO receipt_number_sequences (clinic_id, next_number) VALUES ({invoice.ClinicId}, 1) ON CONFLICT (clinic_id) DO NOTHING",
            cancellationToken);
        // ToListAsync, not SingleAsync: an UPDATE ... RETURNING cannot be composed into a larger query.
        var number = (await db.Database.SqlQuery<int>(
                $"UPDATE receipt_number_sequences SET next_number = next_number + 1 WHERE clinic_id = {invoice.ClinicId} RETURNING next_number - 1 AS \"Value\"")
            .ToListAsync(cancellationToken)).Single();

        var cashier = refund?.RefundedBy ?? payment.ReceivedBy ?? await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
        var receipt = new Receipt
        {
            ClinicId = invoice.ClinicId,
            Number = number,
            Kind = refund is null ? ReceiptKind.Payment : ReceiptKind.Refund,
            InvoiceId = invoice.Id,
            PaymentId = payment.Id,
            RefundId = refund?.Id,
            ClinicName = await db.Clinics.AsNoTracking().Where(c => c.Id == invoice.ClinicId).Select(c => c.Name).SingleAsync(cancellationToken),
            InvoiceNumber = invoice.Number!.Value,
            Currency = invoice.Currency,
            InvoiceTotal = invoice.Total,
            Amount = refund?.Amount ?? payment.Amount,
            Method = refund?.Method ?? payment.Method,
            Reference = refund is null ? payment.Reference : null,
            OccurredAt = refund?.RefundedAt ?? payment.PaidAt,
            AmountPaidAfter = invoice.AmountPaid,
            BalanceDueAfter = invoice.BalanceDue,
            IssuedByUserId = cashier.Id,
            IssuedByName = cashier.Name,
        };
        db.Receipts.Add(receipt);
        audit.Record(
            AuditActions.ReceiptIssued, nameof(Receipt), receipt.Id,
            new { number, kind = receipt.Kind, invoiceId = invoice.Id, paymentId = payment.Id, refundId = refund?.Id, amount = receipt.Amount });
        await db.SaveChangesAsync(cancellationToken);
        return receipt;
    }

    /// <summary>
    /// True when the text contains 13 to 19 digits, in one group or in consecutive groups joined by single separators
    /// (space, dash, dot, slash), that pass the Luhn check: the shape of a card number, which must never be stored (SECURITY.md section 7).
    /// Applied to payment references and refund reasons.
    /// </summary>
    public static bool ContainsCardNumber(string? text)
    {
        if (text is null)
        {
            return false;
        }

        foreach (System.Text.RegularExpressions.Match run in DigitRuns().Matches(text))
        {
            // Any consecutive digit groups of the run ("123 4111 1111 1111 1111" holds a card in groups 2 to 5).
            var groups = run.Value.Split([' ', '-', '.', '/']);
            for (var start = 0; start < groups.Length; start++)
            {
                var digits = "";
                for (var end = start; end < groups.Length && digits.Length < 19; end++)
                {
                    digits += groups[end];
                    if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits.ToCharArray()))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool PassesLuhn(char[] digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }

    /// <summary>Maximal runs of digits with at most one separator between two digits.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\d(?:[ \-./]?\d)*")]
    private static partial System.Text.RegularExpressions.Regex DigitRuns();

    private static void CheckAmount(decimal amount, Invoice invoice, decimal limit, string limitName, List<ApiErrorDetail> problems)
    {
        var minorUnits = Currencies.MinorUnits(invoice.Currency);
        if (amount <= 0 || decimal.Round(amount, minorUnits) != amount)
        {
            problems.Add(new ApiErrorDetail("amount", $"Above 0 with at most {minorUnits} decimals ({invoice.Currency})."));
        }
        else if (amount > limit)
        {
            problems.Add(new ApiErrorDetail("amount", $"Cannot exceed {limitName} ({Currencies.Shortest(limit)} {invoice.Currency})."));
        }
    }

    private async Task<Invoice> LockInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        if (!await db.Invoices.AsNoTracking().AnyAsync(i => i.Id == invoiceId, cancellationToken))
        {
            throw NotFound("The invoice was not found.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM invoices WHERE id = {invoiceId} FOR NO KEY UPDATE", cancellationToken);
        return await db.Invoices.SingleAsync(i => i.Id == invoiceId, cancellationToken);
    }

    private static IQueryable<Payment> WithPeople(IQueryable<Payment> payments) =>
        payments.Include(p => p.ReceivedBy).Include(p => p.Refunds).ThenInclude(r => r.RefundedBy);

    private Guid CurrentClinicId => tenant.ClinicId ?? throw new InvalidOperationException("Payments require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Payments require a signed-in user.");

    private DateTimeOffset Now => TimestampsInterceptor.ToDatabasePrecision(timeProvider.GetUtcNow());

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) =>
        ClinicTimeZone.Of(await db.Clinics.AsNoTracking().SingleAsync(c => c.Id == CurrentClinicId, cancellationToken));

    private static void ThrowIfAny(List<ApiErrorDetail> problems)
    {
        if (problems.Count > 0)
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationError, "One or more fields are invalid.", problems);
        }
    }

    private static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

    private static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

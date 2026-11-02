using VetPlatform.Api.Features.Billing;

namespace VetPlatform.Api.Features.Reports;

// ---------- Revenue (B-24, ADR-024) ----------

/// <summary>
/// Invoices issued in a period, counted on their clinic-local issue day. Issued invoices make the revenue; those voided
/// since are left out of the amounts and counted under <c>voidedInvoices</c> / <c>voidedTotal</c> instead.
/// </summary>
public sealed record RevenueTotals(
    int Invoices,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Total,
    int VoidedInvoices,
    decimal VoidedTotal);

public sealed record RevenueBucket(DateOnly Start, DateOnly End, RevenueTotals Totals);

/// <summary>The revenue's lines by tax rate (the rate's current name; its percentage never changes).</summary>
public sealed record RevenueTaxRate(InvoiceTaxRateSummary TaxRate, decimal Net, decimal Discount, decimal Tax, decimal Total);

public sealed record RevenueReportResponse(
    DateOnly From,
    DateOnly To,
    ReportGrouping GroupBy,
    string TimeZone,
    string? Currency,
    RevenueTotals Totals,
    IReadOnlyList<RevenueTaxRate> ByTaxRate,
    IReadOnlyList<RevenueBucket> Buckets);

// ---------- Payments and refunds ----------

/// <summary>Money received (payments, on their <c>paidAt</c> day) and returned (refunds, on their day); net = received − refunded.</summary>
public sealed record PaymentTotals(int Payments, decimal Received, int Refunds, decimal Refunded, decimal Net);

public sealed record PaymentMethodTotals(PaymentMethod Method, PaymentTotals Totals);

public sealed record PaymentBucket(DateOnly Start, DateOnly End, PaymentTotals Totals);

public sealed record PaymentsReportResponse(
    DateOnly From,
    DateOnly To,
    ReportGrouping GroupBy,
    string TimeZone,
    string? Currency,
    PaymentTotals Totals,
    IReadOnlyList<PaymentMethodTotals> ByMethod,
    IReadOnlyList<PaymentBucket> Buckets);

// ---------- Aged receivables ----------

/// <summary>
/// Balances due of issued invoices by days past their due date, as of the clinic-local today: <c>current</c> is not
/// yet due (due today or later).
/// </summary>
public sealed record ReceivablesAging(
    int Invoices,
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90Days,
    decimal Total);

/// <summary><c>client</c> is null only if it can no longer be shown.</summary>
public sealed record ReceivablesClient(InvoiceClientSummary? Client, ReceivablesAging Aging, DateOnly OldestDueOn);

public sealed record ReceivablesReportResponse(
    DateOnly AsOf,
    string TimeZone,
    string? Currency,
    ReceivablesAging Totals,
    IReadOnlyList<ReceivablesClient> Clients);

// ---------- Client statement ----------

/// <summary>Stored nowhere; sent as exact names like every enum.</summary>
public enum StatementEntryType
{
    InvoiceIssued,
    Payment,
    Refund,
    InvoiceVoided,
}

public sealed record StatementInvoiceSummary(Guid Id, int Number);

/// <summary>
/// One change of what the client owes: <c>amount</c> is signed (positive raises the balance: an issued invoice or a
/// refund; negative lowers it: a payment or a void) and <c>balance</c> is the running balance after it.
/// </summary>
public sealed record StatementEntry(
    StatementEntryType Type,
    DateTimeOffset OccurredAt,
    StatementInvoiceSummary Invoice,
    DateOnly? DueOn,
    Guid? PaymentId,
    Guid? RefundId,
    PaymentMethod? Method,
    decimal Amount,
    decimal Balance);

public sealed record StatementTotals(decimal Invoiced, decimal Paid, decimal Refunded, decimal Voided);

/// <summary>The client as shown today (an issued invoice keeps its own frozen bill-to).</summary>
public sealed record StatementClient(
    Guid Id,
    string FirstName,
    string LastName,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? Country);

public sealed record ClientStatementResponse(
    StatementClient Client,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    string? Currency,
    decimal OpeningBalance,
    StatementTotals Totals,
    decimal ClosingBalance,
    IReadOnlyList<StatementEntry> Entries);

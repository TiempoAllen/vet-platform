using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Billing;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Identity;

namespace VetPlatform.Api.Features.Reports;

/// <summary>
/// Financial reports and the client statement (docs/API.md sections 12 and 5, ADR-024). Read-only, through the clinic
/// query filters. The clinic currency cannot change once invoices exist (ADR-016), so every amount is in it. Revenue
/// and payments are aggregates and not audited; aged receivables and statements show clients' balances and are.
/// </summary>
public sealed class FinancialReportService(AppDbContext db, ITenantContext tenant, AuditTrail audit, TimeProvider timeProvider)
{
    public async Task<RevenueReportResponse> RevenueAsync(ReportPeriodQuery query, CancellationToken cancellationToken)
    {
        var (period, clinic) = await PeriodAsync(query, cancellationToken);
        var invoices = IssuedIn(period);
        var rows = await invoices
            .Select(i => new { IssuedAt = i.IssuedAt!.Value, i.Status, i.Subtotal, i.DiscountTotal, i.TaxTotal, i.Total })
            .ToListAsync(cancellationToken);
        var byDay = rows.ToLookup(r => period.LocalDay(r.IssuedAt));

        RevenueTotals Count(IEnumerable<(InvoiceStatus Status, decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal Total)> items)
        {
            var list = items.ToList();
            var issued = list.Where(i => i.Status == InvoiceStatus.Issued).ToList();
            var voided = list.Where(i => i.Status == InvoiceStatus.Void).ToList();
            return new RevenueTotals(
                issued.Count, Money(issued.Sum(i => i.Subtotal)), Money(issued.Sum(i => i.DiscountTotal)), Money(issued.Sum(i => i.TaxTotal)),
                Money(issued.Sum(i => i.Total)), voided.Count, Money(voided.Sum(i => i.Total)));
        }

        // Lines of the invoices still issued, summed by rate in the database.
        var lines = await db.Set<InvoiceItem>().AsNoTracking()
            .Where(item => invoices.Where(i => i.Status == InvoiceStatus.Issued).Select(i => i.Id).Contains(item.InvoiceId))
            .GroupBy(item => item.TaxRateId)
            .Select(g => new { TaxRateId = g.Key, Net = g.Sum(i => i.Net), Discount = g.Sum(i => i.Discount), Tax = g.Sum(i => i.Tax), Total = g.Sum(i => i.Total) })
            .ToListAsync(cancellationToken);
        var rateIds = lines.Select(l => l.TaxRateId).ToList();
        var rates = await db.TaxRates.AsNoTracking().Where(t => rateIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);

        var all = rows.Select(r => (r.Status, r.Subtotal, r.DiscountTotal, r.TaxTotal, r.Total));
        return new RevenueReportResponse(
            period.From, period.To, period.Grouping, period.Zone.Id, clinic.Currency, Count(all),
            lines.Select(l => new RevenueTaxRate(
                    new InvoiceTaxRateSummary(l.TaxRateId, rates[l.TaxRateId].Name, Money(rates[l.TaxRateId].RatePercent)),
                    Money(l.Net), Money(l.Discount), Money(l.Tax), Money(l.Total)))
                .OrderBy(r => r.TaxRate.Percent).ThenBy(r => r.TaxRate.Name, StringComparer.Ordinal).ThenBy(r => r.TaxRate.Id)
                .ToList(),
            period.Buckets().Select(b => new RevenueBucket(b.Start, b.End, Count(ReportPeriod.Days(b)
                .SelectMany(d => byDay[d]).Select(r => (r.Status, r.Subtotal, r.DiscountTotal, r.TaxTotal, r.Total))))).ToList());
    }

    public async Task<PaymentsReportResponse> PaymentsAsync(ReportPeriodQuery query, CancellationToken cancellationToken)
    {
        var (period, clinic) = await PeriodAsync(query, cancellationToken);
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.PaidAt >= period.StartsAt && p.PaidAt < period.EndsBefore)
            .Select(p => new { At = p.PaidAt, p.Method, p.Amount })
            .ToListAsync(cancellationToken);
        var refunds = await db.Set<Refund>().AsNoTracking()
            .Where(r => r.RefundedAt >= period.StartsAt && r.RefundedAt < period.EndsBefore)
            .Select(r => new { At = r.RefundedAt, r.Method, r.Amount })
            .ToListAsync(cancellationToken);

        // Payments as positive amounts, refunds as negative ones, each on its clinic-local day.
        var moves = payments.Select(p => (Day: period.LocalDay(p.At), p.Method, Amount: p.Amount))
            .Concat(refunds.Select(r => (Day: period.LocalDay(r.At), r.Method, Amount: -r.Amount)))
            .ToList();

        static PaymentTotals Count(IEnumerable<(DateOnly Day, PaymentMethod Method, decimal Amount)> items)
        {
            var list = items.ToList();
            var received = list.Where(m => m.Amount > 0).Sum(m => m.Amount);
            var refunded = -list.Where(m => m.Amount < 0).Sum(m => m.Amount);
            return new PaymentTotals(
                list.Count(m => m.Amount > 0), Money(received), list.Count(m => m.Amount < 0), Money(refunded), Money(received - refunded));
        }

        var byDay = moves.ToLookup(m => m.Day);
        return new PaymentsReportResponse(
            period.From, period.To, period.Grouping, period.Zone.Id, clinic.Currency, Count(moves),
            moves.GroupBy(m => m.Method).OrderBy(g => g.Key).Select(g => new PaymentMethodTotals(g.Key, Count(g))).ToList(),
            period.Buckets().Select(b => new PaymentBucket(b.Start, b.End, Count(ReportPeriod.Days(b).SelectMany(d => byDay[d])))).ToList());
    }

    /// <summary>Issued invoices with a balance due, aged as of the clinic-local today. Audited (client balances).</summary>
    public async Task<ReceivablesReportResponse> ReceivablesAsync(CancellationToken cancellationToken)
    {
        var clinic = await ReportPeriod.ClinicAsync(db, tenant, cancellationToken);
        var zone = ClinicTimeZone.Of(clinic);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        var open = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Issued && i.AmountPaid < i.Total)
            .Select(i => new { i.ClientId, DueOn = i.DueOn!.Value, Balance = i.Total - i.AmountPaid })
            .ToListAsync(cancellationToken);
        var rows = open.Select(i => (i.ClientId, i.DueOn, i.Balance, DaysPast: today.DayNumber - i.DueOn.DayNumber)).ToList();

        static ReceivablesAging Age(IEnumerable<(Guid ClientId, DateOnly DueOn, decimal Balance, int DaysPast)> items)
        {
            var list = items.ToList();
            decimal Sum(Func<int, bool> days) => Money(list.Where(i => days(i.DaysPast)).Sum(i => i.Balance));
            return new ReceivablesAging(
                list.Count, Sum(d => d <= 0), Sum(d => d is >= 1 and <= 30), Sum(d => d is >= 31 and <= 60), Sum(d => d is >= 61 and <= 90),
                Sum(d => d > 90), Money(list.Sum(i => i.Balance)));
        }

        var clientIds = rows.Select(r => r.ClientId).Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new InvoiceClientSummary(c.Id, c.FirstName, c.LastName), cancellationToken);
        var byClient = rows.GroupBy(r => r.ClientId)
            .Select(g => new ReceivablesClient(clients.GetValueOrDefault(g.Key), Age(g), g.Min(r => r.DueOn)))
            .OrderByDescending(c => c.Aging.Total)
            .ThenBy(c => c.Client?.LastName, StringComparer.Ordinal).ThenBy(c => c.Client?.FirstName, StringComparer.Ordinal)
            .ThenBy(c => c.Client?.Id)
            .ToList();

        audit.Record(AuditActions.ReceivablesViewed, nameof(Clinic), clinic.Id, new { asOf = today, clients = byClient.Count });
        await db.SaveChangesAsync(cancellationToken);
        return new ReceivablesReportResponse(today, zone.Id, clinic.Currency, Age(rows), byClient);
    }

    /// <summary>
    /// What one client owed at the start of the period, every change in it with the running balance, and what was owed
    /// at its end. Audited (client.statement_viewed).
    /// </summary>
    public async Task<ClientStatementResponse> StatementAsync(Guid clientId, ReportDateRange query, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The client was not found.");
        var clinic = await ReportPeriod.ClinicAsync(db, tenant, cancellationToken);
        var period = new ReportPeriod(query.From!.Value, query.To!.Value, ReportGrouping.Day, ClinicTimeZone.Of(clinic));

        // Everything up to the end of the period: the client's invoices are few, so the opening balance is summed here.
        var invoices = db.Invoices.AsNoTracking().Where(i => i.ClientId == clientId && i.Status != InvoiceStatus.Draft);
        var issued = await invoices.Where(i => i.IssuedAt < period.EndsBefore)
            .Select(i => new { i.Id, Number = i.Number!.Value, IssuedAt = i.IssuedAt!.Value, DueOn = i.DueOn!.Value, i.Total, i.VoidedAt })
            .ToListAsync(cancellationToken);
        var invoiceIds = invoices.Select(i => i.Id);
        var payments = await db.Payments.AsNoTracking()
            .Where(p => invoiceIds.Contains(p.InvoiceId) && p.PaidAt < period.EndsBefore)
            .Select(p => new { p.Id, p.InvoiceId, p.PaidAt, p.Method, p.Amount })
            .ToListAsync(cancellationToken);
        var refunds = await db.Set<Refund>().AsNoTracking()
            .Where(r => invoiceIds.Contains(r.InvoiceId) && r.RefundedAt < period.EndsBefore)
            .Select(r => new { r.Id, r.InvoiceId, r.PaymentId, r.RefundedAt, r.Method, r.Amount })
            .ToListAsync(cancellationToken);
        var numbers = issued.ToDictionary(i => i.Id, i => i.Number);

        var events = issued
            .Select(i => (Type: StatementEntryType.InvoiceIssued, At: i.IssuedAt, InvoiceId: i.Id, DueOn: (DateOnly?)i.DueOn, PaymentId: (Guid?)null, RefundId: (Guid?)null, Method: (PaymentMethod?)null, Amount: i.Total))
            .Concat(issued.Where(i => i.VoidedAt < period.EndsBefore)
                .Select(i => (Type: StatementEntryType.InvoiceVoided, At: i.VoidedAt!.Value, InvoiceId: i.Id, DueOn: (DateOnly?)null, PaymentId: (Guid?)null, RefundId: (Guid?)null, Method: (PaymentMethod?)null, Amount: -i.Total)))
            .Concat(payments.Select(p => (Type: StatementEntryType.Payment, At: p.PaidAt, p.InvoiceId, DueOn: (DateOnly?)null, PaymentId: (Guid?)p.Id, RefundId: (Guid?)null, Method: (PaymentMethod?)p.Method, Amount: -p.Amount)))
            .Concat(refunds.Select(r => (Type: StatementEntryType.Refund, At: r.RefundedAt, r.InvoiceId, DueOn: (DateOnly?)null, PaymentId: (Guid?)r.PaymentId, RefundId: (Guid?)r.Id, Method: (PaymentMethod?)r.Method, Amount: r.Amount)))
            .OrderBy(e => e.At).ThenBy(e => e.Type).ThenBy(e => numbers[e.InvoiceId]).ThenBy(e => e.RefundId ?? e.PaymentId)
            .ToList();

        var opening = events.Where(e => e.At < period.StartsAt).Sum(e => e.Amount);
        var inPeriod = events.Where(e => e.At >= period.StartsAt).ToList();
        var balance = opening;
        var entries = new List<StatementEntry>(inPeriod.Count);
        foreach (var e in inPeriod)
        {
            balance += e.Amount;
            entries.Add(new StatementEntry(
                e.Type, TimeZoneInfo.ConvertTime(e.At, period.Zone), new StatementInvoiceSummary(e.InvoiceId, numbers[e.InvoiceId]), e.DueOn,
                e.PaymentId, e.RefundId, e.Method, Money(e.Amount), Money(balance)));
        }

        decimal Of(StatementEntryType type) => Money(Math.Abs(inPeriod.Where(e => e.Type == type).Sum(e => e.Amount)));
        audit.Record(AuditActions.ClientStatementViewed, nameof(Client), clientId, new { from = period.From, to = period.To });
        await db.SaveChangesAsync(cancellationToken);
        return new ClientStatementResponse(
            new StatementClient(client.Id, client.FirstName, client.LastName, client.AddressLine1, client.AddressLine2, client.City, client.Region,
                client.PostalCode, client.Country),
            period.From, period.To, period.Zone.Id, clinic.Currency, Money(opening),
            new StatementTotals(Of(StatementEntryType.InvoiceIssued), Of(StatementEntryType.Payment), Of(StatementEntryType.Refund), Of(StatementEntryType.InvoiceVoided)),
            Money(balance), entries);
    }

    // ---------- Helpers ----------

    /// <summary>Invoices issued within the period (clinic-local days), issued or voided since.</summary>
    private IQueryable<Invoice> IssuedIn(ReportPeriod period) =>
        db.Invoices.AsNoTracking().Where(i => i.Status != InvoiceStatus.Draft && i.IssuedAt >= period.StartsAt && i.IssuedAt < period.EndsBefore);

    private async Task<(ReportPeriod Period, Clinic Clinic)> PeriodAsync(ReportPeriodQuery query, CancellationToken cancellationToken)
    {
        var clinic = await ReportPeriod.ClinicAsync(db, tenant, cancellationToken);
        return (new ReportPeriod(query.From!.Value, query.To!.Value, query.ParsedGroupBy, ClinicTimeZone.Of(clinic)), clinic);
    }

    /// <summary>Amounts are stored rounded to the currency; sums of them need no rounding, only the shortest form.</summary>
    private static decimal Money(decimal value) => Currencies.Shortest(value);
}

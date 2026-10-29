using Microsoft.EntityFrameworkCore;
using VetPlatform.Api.Common.Auditing;
using VetPlatform.Api.Common.Errors;
using VetPlatform.Api.Common.Http;
using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Common.Persistence;
using VetPlatform.Api.Common.Tenancy;
using VetPlatform.Api.Features.Auth;
using VetPlatform.Api.Features.Clients;
using VetPlatform.Api.Features.Consultations;
using VetPlatform.Api.Features.Identity;
using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// Invoices (docs/API.md section 10, ADR-016). Drafts are edited line by line and the server recomputes every amount
/// on each change; client-supplied totals do not exist in the contract. Issuing assigns the clinic's next gapless
/// number and freezes the invoice; an issued invoice can only be voided. Every change runs under the invoice's row
/// lock and is audited with ids, numbers and amounts, never descriptions or notes.
/// </summary>
public sealed class InvoiceService(
    AppDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    TimeProvider timeProvider,
    IHttpContextAccessor httpContextAccessor)
{
    // ---------- Reads ----------

    public async Task<PagedResponse<InvoiceResponse>> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        var invoices = WithDetails(db.Invoices.AsNoTracking());
        if (query.ParsedStatus is { } status)
        {
            invoices = invoices.Where(i => i.Status == status);
        }

        if (query.ClientId is { } clientId)
        {
            invoices = invoices.Where(i => i.ClientId == clientId);
        }

        if (query.PatientId is { } patientId)
        {
            invoices = invoices.Where(i => i.PatientId == patientId);
        }

        // Payment state, translated to SQL (the PaymentStatus property itself is computed in memory).
        invoices = query.ParsedPaymentStatus switch
        {
            PaymentStatus.Unpaid => invoices.Where(i => i.Status == InvoiceStatus.Issued && i.AmountPaid == 0 && i.Total > 0),
            PaymentStatus.PartiallyPaid => invoices.Where(i => i.Status == InvoiceStatus.Issued && i.AmountPaid > 0 && i.AmountPaid < i.Total),
            PaymentStatus.Paid => invoices.Where(i => i.Status == InvoiceStatus.Issued && i.AmountPaid == i.Total),
            _ => invoices,
        };
        if (query.Overdue == true)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
            invoices = invoices.Where(i => i.Status == InvoiceStatus.Issued && i.AmountPaid < i.Total && i.DueOn < today);
        }

        return await invoices.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .AsSplitQuery()
            .ToPageAsync(query, i => InvoiceResponse.From(i, zone), cancellationToken);
    }

    public async Task<InvoiceResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        return InvoiceResponse.From(await FindAsync(id, tracking: false, cancellationToken), zone);
    }

    // ---------- Drafts ----------

    public async Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // FOR SHARE waits for (and blocks) a concurrent change of the clinic currency, which locks the clinic row.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM clinics WHERE id = {CurrentClinicId} FOR SHARE", cancellationToken);
        var currency = await db.Clinics.AsNoTracking().Where(c => c.Id == CurrentClinicId).Select(c => c.Currency).SingleAsync(cancellationToken)
            ?? throw Conflict("Set the clinic currency (PATCH /api/clinic) before creating invoices.");

        var problems = new List<ApiErrorDetail>();
        var client = await ActiveClientAsync(request.ClientId!.Value, problems, cancellationToken);
        if (request.PatientId is { } patientId)
        {
            await CheckPatientAsync(patientId, request.ClientId.Value, problems, cancellationToken);
        }

        ThrowIfAny(problems);
        var invoice = new Invoice
        {
            ClinicId = CurrentClinicId,
            ClientId = client!.Id,
            Client = client,
            PatientId = request.PatientId,
            Currency = currency,
            Notes = NullIfBlank(request.Notes),
            CreatedByUserId = CallerId,
        };
        db.Invoices.Add(invoice);
        audit.Record(AuditActions.InvoiceCreated, nameof(Invoice), invoice.Id, new { clientId = invoice.ClientId, patientId = invoice.PatientId, currency });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return InvoiceResponse.From(await FindAsync(invoice.Id, tracking: false, cancellationToken), zone);
    }

    public Task<InvoiceResponse> UpdateAsync(Guid id, UpdateInvoiceRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.InvoiceUpdated, async invoice =>
        {
            RequireDraft(invoice);
            var changed = new List<string>();
            var problems = new List<ApiErrorDetail>();
            var clientId = invoice.ClientId;
            if (request.ClientId is { } newClientId && newClientId != invoice.ClientId)
            {
                if (await ActiveClientAsync(newClientId, problems, cancellationToken) is { } client)
                {
                    clientId = client.Id;
                    invoice.ClientId = client.Id;
                    invoice.Client = client;
                    changed.Add("clientId");
                }
            }

            var patientId = request.PatientId.IsSet ? request.PatientId.Value : invoice.PatientId;
            if (patientId != invoice.PatientId && invoice.Items.Any(i => i.ConsultationId is not null))
            {
                throw Conflict("Lines bill consultations of the current patient; unlink them before changing the patient.");
            }

            if (patientId is { } pid && (patientId != invoice.PatientId || changed.Contains("clientId")))
            {
                await CheckPatientAsync(pid, clientId, problems, cancellationToken);
            }

            ThrowIfAny(problems);
            if (patientId != invoice.PatientId)
            {
                invoice.PatientId = patientId;
                invoice.Patient = null; // reloaded for the response
                changed.Add("patientId");
            }

            if (request.Notes is not null && NullIfBlank(request.Notes) != invoice.Notes)
            {
                invoice.Notes = NullIfBlank(request.Notes);
                changed.Add("notes");
            }

            return changed.Count == 0 ? null : (object)new { fields = changed };
        }, cancellationToken);

    /// <summary>Drafts were never sent to anyone, so they may be deleted outright (issued invoices never are).</summary>
    public async Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invoice = await LockAsync(id, cancellationToken);
        RequireDraft(invoice);
        db.RemoveRange(invoice.Items);
        db.Invoices.Remove(invoice);
        audit.Record(AuditActions.InvoiceDeleted, nameof(Invoice), id, new { clientId = invoice.ClientId, itemCount = invoice.Items.Count });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------- Lines ----------

    public Task<InvoiceResponse> AddItemAsync(Guid id, CreateInvoiceItemRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.InvoiceItemAdded, async invoice =>
        {
            RequireDraft(invoice);
            if (invoice.Items.Count >= Invoice.MaxItems)
            {
                throw Conflict($"An invoice can have at most {Invoice.MaxItems} lines.");
            }

            var problems = new List<ApiErrorDetail>();
            var rate = await ActiveTaxRateAsync(request.TaxRateId!.Value, problems, cancellationToken);
            if (request.ConsultationId is { } consultationId)
            {
                await CheckConsultationAsync(invoice, consultationId, problems, cancellationToken);
            }

            var item = new InvoiceItem
            {
                ClinicId = invoice.ClinicId,
                InvoiceId = invoice.Id,
                Position = invoice.Items.Count == 0 ? 1 : invoice.Items.Max(i => i.Position) + 1,
                Description = request.Description!.Trim(),
                Quantity = request.Quantity!.Value,
                UnitPrice = request.UnitPrice!.Value,
                DiscountType = request.DiscountType ?? DiscountType.None,
                DiscountValue = request.DiscountValue ?? 0m,
                TaxRateId = rate?.Id ?? Guid.Empty,
                TaxRateName = rate?.Name ?? "",
                TaxRatePercent = rate?.RatePercent ?? 0m,
                ConsultationId = request.ConsultationId,
            };
            CheckLine(item, invoice.Currency, problems);
            ThrowIfAny(problems);

            invoice.Items.Add(item);
            db.Add(item); // a preset key would otherwise make EF treat it as an existing row
            InvoiceCalculator.Apply(invoice);
            CheckAmounts(invoice);
            return (object?)new { itemId = item.Id, total = invoice.Total };
        }, cancellationToken);

    public Task<InvoiceResponse> UpdateItemAsync(Guid id, Guid itemId, UpdateInvoiceItemRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.InvoiceItemUpdated, async invoice =>
        {
            var item = invoice.Items.SingleOrDefault(i => i.Id == itemId) ?? throw NotFound("The invoice line was not found.");
            RequireDraft(invoice);
            var changed = new List<string>();
            var problems = new List<ApiErrorDetail>();

            void Set<T>(string field, T current, T wanted, Action<T> apply)
            {
                if (!EqualityComparer<T>.Default.Equals(current, wanted))
                {
                    apply(wanted);
                    changed.Add(field);
                }
            }

            if (request.Description is { } description)
            {
                if (string.IsNullOrWhiteSpace(description))
                {
                    problems.Add(new ApiErrorDetail("description", "The description must not be blank."));
                }
                else
                {
                    Set("description", item.Description, description.Trim(), v => item.Description = v);
                }
            }

            if (request.Quantity is { } quantity) Set("quantity", item.Quantity, quantity, v => item.Quantity = v);
            if (request.UnitPrice is { } unitPrice) Set("unitPrice", item.UnitPrice, unitPrice, v => item.UnitPrice = v);
            if (request.DiscountType is { } discountType)
            {
                Set("discountType", item.DiscountType, discountType, v => item.DiscountType = v);
                if (discountType == DiscountType.None && request.DiscountValue is null)
                {
                    item.DiscountValue = 0m;
                }
            }

            if (request.DiscountValue is { } discountValue) Set("discountValue", item.DiscountValue, discountValue, v => item.DiscountValue = v);
            if (request.TaxRateId is { } taxRateId && taxRateId != item.TaxRateId
                && await ActiveTaxRateAsync(taxRateId, problems, cancellationToken) is { } rate)
            {
                (item.TaxRateId, item.TaxRateName, item.TaxRatePercent) = (rate.Id, rate.Name, rate.RatePercent);
                changed.Add("taxRateId");
            }

            if (request.ConsultationId.IsSet && request.ConsultationId.Value != item.ConsultationId)
            {
                if (request.ConsultationId.Value is { } consultationId)
                {
                    await CheckConsultationAsync(invoice, consultationId, problems, cancellationToken);
                }

                item.ConsultationId = request.ConsultationId.Value;
                changed.Add("consultationId");
            }

            CheckLine(item, invoice.Currency, problems);
            ThrowIfAny(problems);
            InvoiceCalculator.Apply(invoice);
            CheckAmounts(invoice);
            return changed.Count == 0 ? null : (object)new { itemId, fields = changed, total = invoice.Total };
        }, cancellationToken);

    public Task<InvoiceResponse> RemoveItemAsync(Guid id, Guid itemId, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.InvoiceItemRemoved, invoice =>
        {
            var item = invoice.Items.SingleOrDefault(i => i.Id == itemId) ?? throw NotFound("The invoice line was not found.");
            RequireDraft(invoice);
            invoice.Items.Remove(item);
            db.Remove(item);
            InvoiceCalculator.Apply(invoice);
            return Task.FromResult<object?>(new { itemId, total = invoice.Total });
        }, cancellationToken);

    // ---------- Issue and void ----------

    /// <summary>
    /// Freezes a draft and gives it the clinic's next number. The number comes from the clinic's sequence row, updated
    /// in this transaction (lock order: invoice, then sequence), so numbers are unique and gapless even when two
    /// invoices are issued at once.
    /// </summary>
    public Task<InvoiceResponse> IssueAsync(Guid id, IssueInvoiceRequest? request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.InvoiceIssued, async invoice =>
        {
            RequireDraft(invoice);
            if (invoice.Items.Count == 0)
            {
                throw Conflict("An invoice needs at least one line before it can be issued.");
            }

            var zone = await ZoneAsync(cancellationToken);
            var now = Now;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var dueOn = request?.DueOn ?? today;
            if (dueOn < today || !ScheduleRules.IsInSupportedRange(dueOn))
            {
                ThrowIfAny([new ApiErrorDetail("dueOn", "Must be on or after the issue day (clinic time) and no later than 2100-12-31.")]);
            }

            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO invoice_number_sequences (clinic_id, next_number) VALUES ({invoice.ClinicId}, 1) ON CONFLICT (clinic_id) DO NOTHING",
                cancellationToken);
            // ToListAsync, not SingleAsync: an UPDATE ... RETURNING cannot be composed into a larger query.
            var number = (await db.Database.SqlQuery<int>(
                    $"UPDATE invoice_number_sequences SET next_number = next_number + 1 WHERE clinic_id = {invoice.ClinicId} RETURNING next_number - 1 AS \"Value\"")
                .ToListAsync(cancellationToken)).Single();

            // The stored amounts must already be what the calculator gives: issuing never rewrites them.
            var minorUnits = Currencies.MinorUnits(invoice.Currency);
            if (invoice.Items.Any(i => InvoiceCalculator.Line(i.Quantity, i.UnitPrice, i.DiscountType, i.DiscountValue, i.TaxRatePercent, minorUnits)
                    != new InvoiceCalculator.LineAmounts(i.Net, i.Discount, i.Tax, i.Total))
                || invoice.Subtotal != invoice.Items.Sum(i => i.Net) || invoice.Total != invoice.Items.Sum(i => i.Total))
            {
                throw Conflict("The invoice's stored amounts do not match its lines; edit a line to recompute it, then issue.");
            }

            if (invoice.PatientId is { } patientId
                && !await db.Patients.AnyAsync(p => p.Id == patientId && p.ClientId == invoice.ClientId, cancellationToken))
            {
                throw Conflict("The patient no longer belongs to the invoice's client; change the client or the patient first.");
            }

            invoice.Status = InvoiceStatus.Issued;
            invoice.Number = number;
            invoice.IssuedAt = now;
            invoice.DueOn = dueOn;
            invoice.IssuedByUserId = CallerId;
            invoice.IssuedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
            return (object?)new { number, total = invoice.Total, currency = invoice.Currency, dueOn };
        }, cancellationToken);

    public Task<InvoiceResponse> VoidAsync(Guid id, VoidInvoiceRequest request, CancellationToken cancellationToken) =>
        WriteAsync(id, AuditActions.InvoiceVoided, async invoice =>
        {
            switch (invoice.Status)
            {
                case InvoiceStatus.Draft:
                    throw Conflict("A draft is not voided; delete it instead.");
                case InvoiceStatus.Void:
                    throw Conflict("The invoice is already void.");
            }

            if (invoice.AmountPaid != 0)
            {
                throw Conflict("The invoice still holds payments; refund them before voiding it.");
            }

            invoice.Status = InvoiceStatus.Void;
            invoice.VoidedAt = Now < invoice.IssuedAt!.Value ? invoice.IssuedAt.Value : Now;
            invoice.VoidedByUserId = CallerId;
            invoice.VoidedBy = await db.Users.SingleAsync(u => u.Id == CallerId, cancellationToken);
            invoice.VoidReason = request.Reason!.Trim();
            return (object?)new { number = invoice.Number, from = InvoiceStatus.Issued, to = InvoiceStatus.Void };
        }, cancellationToken);

    // ---------- Helpers ----------

    /// <summary>Locks the invoice row (after the clinic-filtered lookup), applies the change, recomputes and audits it.</summary>
    private async Task<InvoiceResponse> WriteAsync(Guid id, string auditAction, Func<Invoice, Task<object?>> apply, CancellationToken cancellationToken)
    {
        var zone = await ZoneAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invoice = await LockAsync(id, cancellationToken);
        var metadata = await apply(invoice);
        if (metadata is not null)
        {
            audit.Record(auditAction, nameof(Invoice), invoice.Id, metadata);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return InvoiceResponse.From(await FindAsync(id, tracking: false, cancellationToken), zone);
    }

    private async Task<Invoice> LockAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.Invoices.AsNoTracking().AnyAsync(i => i.Id == id, cancellationToken))
        {
            throw NotFound("The invoice was not found.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM invoices WHERE id = {id} FOR NO KEY UPDATE", cancellationToken);
        return await FindAsync(id, tracking: true, cancellationToken);
    }

    private async Task<Client?> ActiveClientAsync(Guid clientId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId && c.Status == ClientStatus.Active, cancellationToken);
        if (client is null)
        {
            problems.Add(new ApiErrorDetail("clientId", "The client was not found or is not active."));
        }

        return client;
    }

    /// <summary>The patient must currently belong to the invoice's client (any patient status: final bills are allowed).</summary>
    private async Task CheckPatientAsync(Guid patientId, Guid clientId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ClientId == clientId, cancellationToken))
        {
            problems.Add(new ApiErrorDetail("patientId", "The patient was not found or does not belong to the client."));
        }
    }

    private async Task<TaxRate?> ActiveTaxRateAsync(Guid taxRateId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        var rate = await db.TaxRates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taxRateId && t.Status == TaxRateStatus.Active, cancellationToken);
        if (rate is null)
        {
            problems.Add(new ApiErrorDetail("taxRateId", "The tax rate was not found or is archived."));
        }

        return rate;
    }

    /// <summary>The shared link rule (locks the consultation <c>FOR SHARE</c>, so a concurrent discard cannot slip in).</summary>
    private async Task CheckConsultationAsync(Invoice invoice, Guid consultationId, List<ApiErrorDetail> problems, CancellationToken cancellationToken)
    {
        if (invoice.PatientId is not { } patientId
            || !await ConsultationLinks.IsLinkableAsync(db, consultationId, patientId, cancellationToken))
        {
            problems.Add(new ApiErrorDetail(
                "consultationId", "The consultation was not found, is discarded, or is not of the invoice's patient."));
        }
    }

    /// <summary>Field rules that depend on each other or on the currency; amounts are computed only after they pass.</summary>
    private static void CheckLine(InvoiceItem item, string currency, List<ApiErrorDetail> problems)
    {
        var minorUnits = Currencies.MinorUnits(currency);
        if (item.Quantity <= 0 || item.Quantity > InvoiceItem.MaxQuantity || decimal.Round(item.Quantity, 3) != item.Quantity)
        {
            problems.Add(new ApiErrorDetail("quantity", $"More than 0 and at most {InvoiceItem.MaxQuantity}, with at most 3 decimals."));
        }

        if (item.UnitPrice < 0 || item.UnitPrice > InvoiceItem.MaxUnitPrice || decimal.Round(item.UnitPrice, 4) != item.UnitPrice)
        {
            problems.Add(new ApiErrorDetail("unitPrice", $"0 or more and at most {InvoiceItem.MaxUnitPrice}, with at most 4 decimals."));
        }

        switch (item.DiscountType)
        {
            case DiscountType.None when item.DiscountValue != 0:
                problems.Add(new ApiErrorDetail("discountValue", "Only with a discountType of Percent or Amount."));
                break;
            case DiscountType.Percent when item.DiscountValue <= 0 || item.DiscountValue > 100 || decimal.Round(item.DiscountValue, 4) != item.DiscountValue:
                problems.Add(new ApiErrorDetail("discountValue", "A percentage above 0 and at most 100, with at most 4 decimals."));
                break;
            case DiscountType.Amount when item.DiscountValue <= 0 || decimal.Round(item.DiscountValue, minorUnits) != item.DiscountValue:
                problems.Add(new ApiErrorDetail("discountValue", $"An amount above 0 with at most {minorUnits} decimals ({currency})."));
                break;
        }

        if (problems.Count == 0 && item.DiscountType == DiscountType.Amount
            && item.DiscountValue > Currencies.Round(item.Quantity * item.UnitPrice, minorUnits))
        {
            problems.Add(new ApiErrorDetail("discountValue", "The discount cannot exceed the line's net amount."));
        }
    }

    private static void CheckAmounts(Invoice invoice)
    {
        if (invoice.Total > Invoice.MaxAmount || invoice.Items.Any(i => i.Total > Invoice.MaxAmount))
        {
            ThrowIfAny([new ApiErrorDetail(null, $"A line or invoice total cannot exceed {Invoice.MaxAmount:N0} {invoice.Currency}.")]);
        }
    }

    private static void RequireDraft(Invoice invoice)
    {
        if (invoice.Status != InvoiceStatus.Draft)
        {
            throw Conflict($"A {invoice.Status} invoice cannot be changed; void it if it was issued in error.");
        }
    }

    private static IQueryable<Invoice> WithDetails(IQueryable<Invoice> invoices) =>
        invoices
            .Include(i => i.Client)
            .Include(i => i.Patient)
            .Include(i => i.Items)
            .Include(i => i.IssuedBy)
            .Include(i => i.VoidedBy)
            .Include(i => i.CreatedBy);

    private async Task<Invoice> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var invoices = WithDetails(tracking ? db.Invoices : db.Invoices.AsNoTracking());
        return await invoices.AsSplitQuery().SingleOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw NotFound("The invoice was not found.");
    }

    private Guid CurrentClinicId => tenant.ClinicId ?? throw new InvalidOperationException("Invoices require a current clinic.");

    private Guid CallerId =>
        AuthClaims.GetUserId(httpContextAccessor.HttpContext!.User)
        ?? throw new InvalidOperationException("Invoices require a signed-in user.");

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

using VetPlatform.Api.Common.Money;

namespace VetPlatform.Api.Features.Billing;

/// <summary>
/// The billing arithmetic (SRS Q-4, ADR-016): prices exclude tax; each line is computed and rounded on its own, half
/// away from zero, to the currency's minor units; the invoice totals are the sums of the lines, so a printed invoice
/// always adds up. Pure: no database, no clock.
/// </summary>
public static class InvoiceCalculator
{
    public sealed record LineAmounts(decimal Net, decimal Discount, decimal Tax, decimal Total);

    /// <summary>
    /// net = round(quantity × unit price); discount = round(net × percent / 100), or the amount itself; tax =
    /// round((net − discount) × rate / 100); total = net − discount + tax. Inputs are validated by the caller; an amount
    /// discount larger than the net is refused there, never clipped here.
    /// </summary>
    public static LineAmounts Line(
        decimal quantity,
        decimal unitPrice,
        DiscountType discountType,
        decimal discountValue,
        decimal taxRatePercent,
        int minorUnits)
    {
        var net = Currencies.Round(quantity * unitPrice, minorUnits);
        var discount = discountType switch
        {
            DiscountType.Percent => Currencies.Round(net * discountValue / 100m, minorUnits),
            DiscountType.Amount => discountValue,
            _ => 0m,
        };
        var tax = Currencies.Round((net - discount) * taxRatePercent / 100m, minorUnits);
        return new LineAmounts(net, discount, tax, net - discount + tax);
    }

    /// <summary>Recomputes every line and the invoice totals.</summary>
    public static void Apply(Invoice invoice)
    {
        var minorUnits = Currencies.MinorUnits(invoice.Currency);
        foreach (var item in invoice.Items)
        {
            var amounts = Line(item.Quantity, item.UnitPrice, item.DiscountType, item.DiscountValue, item.TaxRatePercent, minorUnits);
            (item.Net, item.Discount, item.Tax, item.Total) = (amounts.Net, amounts.Discount, amounts.Tax, amounts.Total);
        }

        invoice.Subtotal = invoice.Items.Sum(i => i.Net);
        invoice.DiscountTotal = invoice.Items.Sum(i => i.Discount);
        invoice.TaxTotal = invoice.Items.Sum(i => i.Tax);
        invoice.Total = invoice.Items.Sum(i => i.Total);
    }
}

using VetPlatform.Api.Common.Money;
using VetPlatform.Api.Features.Billing;

namespace VetPlatform.Api.Tests;

/// <summary>The billing arithmetic and currency rules (B-16, SRS Q-4): pure, no database.</summary>
public sealed class InvoiceCalculatorTests
{
    [Fact]
    public void TaxIsAddedOnTopOfTheNet()
    {
        var line = InvoiceCalculator.Line(2m, 10m, DiscountType.None, 0m, 20m, 2);

        Assert.Equal(new InvoiceCalculator.LineAmounts(20m, 0m, 4m, 24m), line);
    }

    [Fact]
    public void EachStepRoundsHalfAwayFromZero_ToTheCurrency()
    {
        // 3 × 3.335 = 10.005 → 10.01; 10% off = 1.001 → 1.00; tax 20% of 9.01 = 1.802 → 1.80.
        var line = InvoiceCalculator.Line(3m, 3.335m, DiscountType.Percent, 10m, 20m, 2);

        Assert.Equal(new InvoiceCalculator.LineAmounts(10.01m, 1.00m, 1.80m, 10.81m), line);
    }

    [Fact]
    public void MidpointsRoundAwayFromZero_NotToEven()
    {
        Assert.Equal(0.13m, Currencies.Round(0.125m, 2));
        Assert.Equal(1m, Currencies.Round(0.5m, 0));
        Assert.Equal(3m, Currencies.Round(2.5m, 0));
    }

    [Fact]
    public void ZeroDecimalCurrencies_RoundToWholeUnits()
    {
        // JPY: 3 × 333.5 = 1000.5 → 1001; tax 10% = 100.1 → 100.
        var line = InvoiceCalculator.Line(3m, 333.5m, DiscountType.None, 0m, 10m, Currencies.MinorUnits("JPY"));

        Assert.Equal(new InvoiceCalculator.LineAmounts(1001m, 0m, 100m, 1101m), line);
    }

    [Fact]
    public void AnAmountDiscount_IsTakenAsGiven_AndTaxAppliesAfterIt()
    {
        var line = InvoiceCalculator.Line(1m, 50m, DiscountType.Amount, 5m, 20m, 2);

        Assert.Equal(new InvoiceCalculator.LineAmounts(50m, 5m, 9m, 54m), line);
    }

    [Fact]
    public void InvoiceTotals_AreTheSumsOfTheRoundedLines()
    {
        var invoice = new Invoice
        {
            Currency = "EUR",
            Items =
            [
                new InvoiceItem { Description = "a", TaxRateName = "S", Quantity = 1, UnitPrice = 0.333m, TaxRatePercent = 20m },
                new InvoiceItem { Description = "b", TaxRateName = "S", Quantity = 1, UnitPrice = 0.333m, TaxRatePercent = 20m },
                new InvoiceItem { Description = "c", TaxRateName = "S", Quantity = 1, UnitPrice = 0.333m, TaxRatePercent = 20m },
            ],
        };

        InvoiceCalculator.Apply(invoice);

        // Each line: net 0.33, tax 0.07 (0.066), total 0.40. The invoice adds the printed lines: 0.99 + 0.21 = 1.20.
        Assert.Equal(0.99m, invoice.Subtotal);
        Assert.Equal(0.21m, invoice.TaxTotal);
        Assert.Equal(1.20m, invoice.Total);
        Assert.Equal(invoice.Subtotal - invoice.DiscountTotal + invoice.TaxTotal, invoice.Total);
    }

    [Theory]
    [InlineData("EUR", true, 2)]
    [InlineData("JPY", true, 0)]
    [InlineData("KWD", true, 3)]
    [InlineData("CLF", true, 4)]
    [InlineData("eur", false, 0)]
    [InlineData("XXX", false, 0)]
    [InlineData("EURO", false, 0)]
    public void Currencies_AreIso4217CodesWithTheirMinorUnits(string code, bool known, int minorUnits)
    {
        Assert.Equal(known, Currencies.IsKnown(code));
        if (known)
        {
            Assert.Equal(minorUnits, Currencies.MinorUnits(code));
        }
    }
}

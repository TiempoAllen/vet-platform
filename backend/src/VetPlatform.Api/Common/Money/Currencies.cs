using System.Collections.Frozen;

namespace VetPlatform.Api.Common.Money;

/// <summary>
/// Active ISO 4217 currency codes and their minor units (ADR-016). Kept as data rather than read from the operating
/// system's culture tables, which differ between platforms and are missing in invariant-globalization containers.
/// </summary>
public static class Currencies
{
    private static readonly FrozenSet<string> NoMinorUnits = new[]
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> ThreeMinorUnits = new[]
    {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
    }.ToFrozenSet();

    private static readonly FrozenSet<string> FourMinorUnits = new[] { "CLF", "UYW" }.ToFrozenSet();

    private static readonly FrozenSet<string> Codes = (
        "AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BHD BIF BMD BND BOB BRL BSD BTN BWP BYN BZD CAD CDF CHF " +
        "CLF CLP CNY COP CRC CUP CVE CZK DJF DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GNF GTQ GYD HKD HNL " +
        "HTG HUF IDR ILS INR IQD IRR ISK JMD JOD JPY KES KGS KHR KMF KPW KRW KWD KYD KZT LAK LBP LKR LRD LSL LYD MAD MDL " +
        "MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO NOK NPR NZD OMR PAB PEN PGK PHP PKR PLN PYG QAR RON " +
        "RSD RUB RWF SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS TMT TND TOP TRY TTD TWD TZS UAH " +
        "UGX USD UYI UYU UYW UZS VED VES VND VUV WST XAF XCD XOF XPF YER ZAR ZMW ZWG")
        .Split(' ').ToFrozenSet();

    /// <summary>Upper-case three-letter codes only; "usd" is not accepted.</summary>
    public static bool IsKnown(string? code) => code is not null && Codes.Contains(code);

    /// <summary>Digits after the decimal point that amounts in <paramref name="code"/> are rounded to.</summary>
    public static int MinorUnits(string code) =>
        NoMinorUnits.Contains(code) ? 0
        : ThreeMinorUnits.Contains(code) ? 3
        : FourMinorUnits.Contains(code) ? 4
        : 2;

    /// <summary>The billing rounding rule: half away from zero, to the currency's minor units.</summary>
    public static decimal Round(decimal amount, int minorUnits) =>
        decimal.Round(amount, minorUnits, MidpointRounding.AwayFromZero);

    /// <summary>numeric columns come back with their full scale (12.5000); responses use the shortest form.</summary>
    public static decimal Shortest(decimal value) => value / 1.000000000000000000000000000000000m;
}

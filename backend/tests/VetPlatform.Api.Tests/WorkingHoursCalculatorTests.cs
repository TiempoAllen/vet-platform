using VetPlatform.Api.Features.Scheduling;

namespace VetPlatform.Api.Tests;

/// <summary>The working-hours computation (B-08), including daylight-saving changes. No database.</summary>
public sealed class WorkingHoursCalculatorTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static WorkingHoursBlock Block(DayOfWeek day, int startMinute, int endMinute, DateOnly? from = null, DateOnly? to = null) => new()
    {
        DayOfWeek = day,
        StartMinute = startMinute,
        EndMinute = endMinute,
        EffectiveFrom = from ?? new DateOnly(2000, 1, 1),
        EffectiveTo = to,
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void WeeklyBlocks_BecomeIntervalsOnTheirDays()
    {
        // 2026-11-02 is a Monday.
        var intervals = WorkingHoursCalculator.Compute(
            [Block(DayOfWeek.Monday, 9 * 60, 17 * 60), Block(DayOfWeek.Wednesday, 8 * 60, 12 * 60)],
            [], TimeZoneInfo.Utc, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8));

        Assert.Equal(
            [new Interval(Utc(2026, 11, 2, 9), Utc(2026, 11, 2, 17)), new Interval(Utc(2026, 11, 4, 8), Utc(2026, 11, 4, 12))],
            intervals);
    }

    [Fact]
    public void LocalTimes_UseTheClinicsOffset()
    {
        // Summer: London is UTC+1.
        var intervals = WorkingHoursCalculator.Compute(
            [Block(DayOfWeek.Monday, 9 * 60, 17 * 60)], [], London, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1));

        Assert.Equal([new Interval(Utc(2026, 6, 1, 8), Utc(2026, 6, 1, 16))], intervals);
    }

    [Fact]
    public void SpringForward_ShortensABlockSpanningTheGap_AndDropsOneInsideIt()
    {
        // 2026-03-29 (Sunday): London clocks go 01:00 → 02:00.
        var date = new DateOnly(2026, 3, 29);

        var spanning = WorkingHoursCalculator.Compute([Block(DayOfWeek.Sunday, 30, 3 * 60)], [], London, date, date);
        var inside = WorkingHoursCalculator.Compute([Block(DayOfWeek.Sunday, 60, 90)], [], London, date, date);

        Assert.Equal([new Interval(Utc(2026, 3, 29, 0, 30), Utc(2026, 3, 29, 2))], spanning); // 1.5 h of real time
        Assert.Empty(inside);
    }

    [Fact]
    public void FallBack_LengthensABlockSpanningTheRepeatedHour()
    {
        // 2026-10-25 (Sunday): London clocks go 02:00 → 01:00, so 00:00–03:00 local lasts four hours.
        var date = new DateOnly(2026, 10, 25);

        var intervals = WorkingHoursCalculator.Compute([Block(DayOfWeek.Sunday, 0, 3 * 60)], [], London, date, date);

        var interval = Assert.Single(intervals);
        Assert.Equal(TimeSpan.FromHours(4), interval.End - interval.Start);
    }

    [Fact]
    public void ExceptionsAndClosures_AreSubtracted()
    {
        var monday = new DateOnly(2026, 11, 2);
        var intervals = WorkingHoursCalculator.Compute(
            [Block(DayOfWeek.Monday, 9 * 60, 17 * 60), Block(DayOfWeek.Tuesday, 9 * 60, 17 * 60)],
            [
                new Interval(Utc(2026, 11, 2, 12), Utc(2026, 11, 2, 13)), // lunch-time leave
                new Interval(Utc(2026, 11, 3, 0), Utc(2026, 11, 4, 0)), // whole-day closure
                new Interval(Utc(2026, 11, 2, 16, 30), Utc(2026, 11, 2, 18)), // overlaps the end
            ],
            TimeZoneInfo.Utc, monday, monday.AddDays(1));

        Assert.Equal(
            [new Interval(Utc(2026, 11, 2, 9), Utc(2026, 11, 2, 12)), new Interval(Utc(2026, 11, 2, 13), Utc(2026, 11, 2, 16, 30))],
            intervals);
    }

    [Fact]
    public void EffectiveDates_LimitWhenABlockApplies_AndAdjacentBlocksMerge()
    {
        var intervals = WorkingHoursCalculator.Compute(
            [
                Block(DayOfWeek.Monday, 9 * 60, 12 * 60, to: new DateOnly(2026, 11, 2)),
                Block(DayOfWeek.Monday, 12 * 60, 17 * 60, to: new DateOnly(2026, 11, 2)),
                Block(DayOfWeek.Monday, 10 * 60, 14 * 60, from: new DateOnly(2026, 11, 9)),
            ],
            [], TimeZoneInfo.Utc, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9));

        Assert.Equal(
            [new Interval(Utc(2026, 11, 2, 9), Utc(2026, 11, 2, 17)), new Interval(Utc(2026, 11, 9, 10), Utc(2026, 11, 9, 14))],
            intervals);
    }

    [Fact]
    public void ABlockEndingAt2400_EndsAtTheNextMidnight()
    {
        var intervals = WorkingHoursCalculator.Compute(
            [Block(DayOfWeek.Friday, 20 * 60, 24 * 60), Block(DayOfWeek.Saturday, 0, 2 * 60)],
            [], TimeZoneInfo.Utc, new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 7));

        Assert.Equal([new Interval(Utc(2026, 11, 6, 20), Utc(2026, 11, 7, 2))], intervals); // overnight shift, merged
    }
}

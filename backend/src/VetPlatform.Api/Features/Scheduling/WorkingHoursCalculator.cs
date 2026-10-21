namespace VetPlatform.Api.Features.Scheduling;

/// <summary>A half-open period [Start, End) between two instants.</summary>
public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Turns a veterinarian's weekly blocks (clinic local time) and exceptions (instants) into actual working periods
/// (ADR-008). Pure, so it is tested directly, including daylight-saving changes. B-09 reuses it to validate bookings.
/// </summary>
public static class WorkingHoursCalculator
{
    /// <summary>
    /// Working periods for the local calendar days <paramref name="from"/> to <paramref name="to"/> (inclusive), as
    /// instants in UTC, sorted, merged where they touch, with every exception and closure removed.
    /// </summary>
    public static List<Interval> Compute(
        IEnumerable<WorkingHoursBlock> blocks,
        IEnumerable<Interval> unavailable,
        TimeZoneInfo zone,
        DateOnly from,
        DateOnly to)
    {
        var blockList = blocks.ToList();
        var working = new List<Interval>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            foreach (var block in blockList.Where(block => block.AppliesOn(date)))
            {
                var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                var start = ToInstant(midnight.AddMinutes(block.StartMinute), zone);
                var end = ToInstant(midnight.AddMinutes(block.EndMinute), zone);
                if (end > start) // a block entirely inside a spring-forward gap collapses to nothing
                {
                    working.Add(new Interval(start, end));
                }
            }
        }

        return Subtract(Merge(working), Merge(unavailable.Select(i => new Interval(i.Start.ToUniversalTime(), i.End.ToUniversalTime()))));
    }

    /// <summary>
    /// The instant of a local wall-clock time. A time skipped by a spring-forward change moves forward to the first
    /// valid time; a time repeated by a fall-back change is read as standard time (the later of the two).
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 24 * 60 / ScheduleRules.GranularityMinutes)
        {
            local = local.AddMinutes(ScheduleRules.GranularityMinutes);
        }

        // For an ambiguous time ConvertTimeToUtc assumes standard time.
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
    }

    private static List<Interval> Merge(IEnumerable<Interval> intervals)
    {
        var merged = new List<Interval>();
        foreach (var interval in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = interval.End > merged[^1].End ? interval.End : merged[^1].End };
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    /// <summary>Both inputs sorted and merged, so one forward pass over each is enough (linear, not quadratic).</summary>
    private static List<Interval> Subtract(List<Interval> working, List<Interval> unavailable)
    {
        var result = new List<Interval>();
        var next = 0;
        foreach (var period in working)
        {
            while (next < unavailable.Count && unavailable[next].End <= period.Start)
            {
                next++;
            }

            var start = period.Start;
            for (var i = next; i < unavailable.Count && unavailable[i].Start < period.End; i++)
            {
                if (unavailable[i].Start > start)
                {
                    result.Add(new Interval(start, unavailable[i].Start));
                }

                if (unavailable[i].End > start)
                {
                    start = unavailable[i].End;
                }
            }

            if (start < period.End)
            {
                result.Add(new Interval(start, period.End));
            }
        }

        return result;
    }
}

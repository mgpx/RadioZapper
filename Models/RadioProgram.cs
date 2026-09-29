namespace RadioZapper.Models;

public sealed record RadioProgram(int Id, string Title, string? Detail, DayOfWeek Day, TimeOnly Start, TimeOnly End);

public sealed record RadioContact(string Type, string Title, string? Detail, string? Value);

public sealed record RadiosNetStationDetails(
    RadioStation Station,
    string? Description,
    string? Segments,
    IReadOnlyList<RadioContact> Contacts,
    IReadOnlyList<RadioProgram> Schedule);

public static class ProgramSchedule
{
    public static IReadOnlyList<RadioProgram> GetCurrent(IEnumerable<RadioProgram> schedule, DateTime localNow)
    {
        var today = localNow.DayOfWeek;
        var yesterday = localNow.AddDays(-1).DayOfWeek;
        var time = TimeOnly.FromDateTime(localNow);
        return schedule.Where(program =>
        {
            var endsNextDay = program.End <= program.Start;
            return (program.Day == today && time >= program.Start && (!endsNextDay && time < program.End || endsNextDay))
                || (program.Day == yesterday && endsNextDay && time < program.End);
        })
        .DistinctBy(program => (program.Title, program.Start, program.End))
        .OrderBy(program => program.Start)
        .ToArray();
    }
}

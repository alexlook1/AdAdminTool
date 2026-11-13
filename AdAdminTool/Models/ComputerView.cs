namespace AdAdminTool.Models;

public class ComputerView
{
    public AdComputer Computer { get; }
    public AdUser? MatchedUser { get; }

    public ComputerView(AdComputer computer, AdUser? matchedUser)
    {
        Computer = computer;
        MatchedUser = matchedUser;
    }

    public string DisplayTitle =>
        MatchedUser is { DisplayName.Length: > 0 } u
            ? u.DisplayName
            : MatchedUser?.SamAccountName ?? Computer.Name;

    public string Subtitle =>
        MatchedUser != null
            ? $"{Computer.Name} · {MatchedUser.SamAccountName}"
            : Computer.Name;

    public string SearchText => string.Join(' ',
        new[]
        {
            DisplayTitle,
            Computer.Name,
            Computer.DnsHostName,
            MatchedUser?.SamAccountName ?? "",
            MatchedUser?.Mail ?? ""
        }.Where(s => !string.IsNullOrEmpty(s)))
        .ToLowerInvariant();
}
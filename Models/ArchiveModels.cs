namespace RefertoPallamano_Blazor.Models;

public sealed class ArchiveDatabase
{
    public int SchemaVersion { get; set; } = 2;
    public List<ArchiveChampionship> Championships { get; set; } = [];
    public List<ArchiveSeason> Seasons { get; set; } = [];
    public List<ArchiveTeam> Teams { get; set; } = [];
    public List<ArchivePerson> People { get; set; } = [];
    public List<ArchiveTeamEntry> TeamEntries { get; set; } = [];
}

public sealed class ArchiveChampionship
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Gender { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class ArchiveSeason
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; } = new(2026, 7, 1);
    public DateOnly EndDate { get; set; } = new(2027, 6, 30);
    public bool IsActive { get; set; } = true;
}

public sealed class ArchiveTeam
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class ArchivePerson
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FullName { get; set; } = "";
    public string PersonType { get; set; } = "PLAYER";
    public bool IsActive { get; set; } = true;
}

// Associa una persona a una squadra, a un campionato e a una stagione.
// Numero e lettera sono attributi dell'iscrizione, non dell'anagrafica personale.
public sealed class ArchiveTeamEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TeamId { get; set; } = "";
    public string ChampionshipId { get; set; } = "";
    public string SeasonId { get; set; } = "";
    public string PersonId { get; set; } = "";
    public string EntryType { get; set; } = "PLAYER";
    public string ShirtNumber { get; set; } = "";
    public string OfficialLetter { get; set; } = "";
    public bool IsSelected { get; set; } = false;
    public bool IsActive { get; set; } = true;
}


public sealed class ArchivePdfRosterImport
{
    public string Championship { get; set; } = "";
    public string Season { get; set; } = "";
    public List<ArchivePdfRosterTeam> Teams { get; set; } = [];
}
public sealed class ArchivePdfRosterTeam
{
    public string DetectedName { get; set; } = "";
    public string TeamId { get; set; } = "";
    public List<ArchivePdfRosterPerson> People { get; set; } = [];
}
public sealed class ArchivePdfRosterPerson
{
    public string NumberOrLetter { get; set; } = "";
    public string Name { get; set; } = "";
    public string PersonType { get; set; } = "PLAYER";
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "";
}

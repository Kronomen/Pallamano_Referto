using System.Text.Json;

namespace RefertoPallamano_Blazor.Models;

public sealed class ArchiveDatabase
{
    // Unisce due snapshot senza cancellare record: gli ID sono l'identità stabile.
    // Se lo stesso ID contiene valori diversi, il merge si interrompe per non perdere modifiche.
    public int MergeFrom(ArchiveDatabase other)
    {
        var championships = MergeById(Championships, other.Championships, x => x.Id, "campionati");
        var seasons = MergeById(Seasons, other.Seasons, x => x.Id, "stagioni");
        var teams = MergeById(Teams, other.Teams, x => x.Id, "squadre");
        var people = MergeById(People, other.People, x => x.Id, "persone");
        var entries = MergeById(TeamEntries, other.TeamEntries, x => x.Id, "iscrizioni");

        DeletedChampionshipIds = MergeIds(DeletedChampionshipIds, other.DeletedChampionshipIds);
        DeletedSeasonIds = MergeIds(DeletedSeasonIds, other.DeletedSeasonIds);
        DeletedTeamIds = MergeIds(DeletedTeamIds, other.DeletedTeamIds);
        DeletedPersonIds = MergeIds(DeletedPersonIds, other.DeletedPersonIds);
        DeletedTeamEntryIds = MergeIds(DeletedTeamEntryIds, other.DeletedTeamEntryIds);

        var added = championships.Added + seasons.Added + teams.Added + people.Added + entries.Added;
        Championships = championships.Items.Where(x => !DeletedChampionshipIds.Contains(x.Id, StringComparer.Ordinal)).ToList();
        Seasons = seasons.Items.Where(x => !DeletedSeasonIds.Contains(x.Id, StringComparer.Ordinal)).ToList();
        Teams = teams.Items.Where(x => !DeletedTeamIds.Contains(x.Id, StringComparer.Ordinal)).ToList();
        People = people.Items.Where(x => !DeletedPersonIds.Contains(x.Id, StringComparer.Ordinal)).ToList();
        TeamEntries = entries.Items.Where(x =>
            !DeletedTeamEntryIds.Contains(x.Id, StringComparer.Ordinal) &&
            !DeletedTeamIds.Contains(x.TeamId, StringComparer.Ordinal) &&
            !DeletedPersonIds.Contains(x.PersonId, StringComparer.Ordinal) &&
            !DeletedChampionshipIds.Contains(x.ChampionshipId, StringComparer.Ordinal) &&
            !DeletedSeasonIds.Contains(x.SeasonId, StringComparer.Ordinal)).ToList();
        SchemaVersion = Math.Max(SchemaVersion, other.SchemaVersion);
        return added;

    private static List<string> MergeIds(IEnumerable<string>? current, IEnumerable<string>? incoming) =>
        (current ?? []).Concat(incoming ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    private static (List<T> Items, int Added) MergeById<T>(
        IEnumerable<T> current, IEnumerable<T> incoming, Func<T, string> getId, string label)
    {
        var result = current.ToList();
        var byId = result.ToDictionary(getId, StringComparer.Ordinal);
        var added = 0;
        foreach (var item in incoming)
        {
            var id = getId(item);
            if (!byId.TryGetValue(id, out var existing))
            {
                result.Add(item);
                byId.Add(id, item);
                added++;
                continue;
            }

            if (!string.Equals(JsonSerializer.Serialize(existing), JsonSerializer.Serialize(item), StringComparison.Ordinal))
                throw new InvalidOperationException($"Modifica concorrente rilevata su {label} (ID {id}). I dati sono stati conservati: serve una verifica prima di sincronizzare.");
        }
        return (result, added);
    }

    public int SchemaVersion { get; set; } = 3;
    public List<ArchiveChampionship> Championships { get; set; } = [];
    public List<ArchiveSeason> Seasons { get; set; } = [];
    public List<ArchiveTeam> Teams { get; set; } = [];
    public List<ArchivePerson> People { get; set; } = [];
    public List<ArchiveTeamEntry> TeamEntries { get; set; } = [];

    // Tombstone replicati tra dispositivi: impediscono al merge di far ricomparire
    // record cancellati su un altro dispositivo.
    public List<string> DeletedChampionshipIds { get; set; } = [];
    public List<string> DeletedSeasonIds { get; set; } = [];
    public List<string> DeletedTeamIds { get; set; } = [];
    public List<string> DeletedPersonIds { get; set; } = [];
    public List<string> DeletedTeamEntryIds { get; set; } = [];
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
    // Scelta esplicita quando esiste già un omonimo attivo in un'altra squadra:
    // NEW crea una distinta anagrafica; EXISTING:<id> trasferisce la persona selezionata.
    public string ImportChoice { get; set; } = "";
}

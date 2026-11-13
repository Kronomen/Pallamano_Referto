using System.Text.Json;
using Microsoft.JSInterop;
using RefertoPallamano_Blazor.Models;

namespace RefertoPallamano_Blazor.Services;

public sealed class ArchiveService(IJSRuntime js, CloudArchiveService cloud)
{
    private const string StorageKey = "refertoPallamanoArchiveV1";
    private const string BackupKey = "refertoPallamanoArchiveBackupV1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public ArchiveDatabase Data { get; private set; } = new();
    public bool IsLoaded { get; private set; }
    public event Action? Changed;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public string? LoadError { get; private set; }

    public async Task InitializeAsync()
    {
        if (IsLoaded || !string.IsNullOrWhiteSpace(LoadError)) return;
        try
        {
            var json = await js.InvokeAsync<string?>("archiveStorage.get", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                // Un archivio locale presente ma non leggibile non deve mai essere
                // sostituito con i dati iniziali: si interrompe il caricamento.
                Data = JsonSerializer.Deserialize<ArchiveDatabase>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Il contenuto locale è vuoto o non rappresenta un archivio valido.");
                Data.Championships ??= [];
                Data.Seasons ??= [];
                Data.Teams ??= [];
                Data.People ??= [];
                Data.TeamEntries ??= [];
            }
            else
            {
                var seedJson = await js.InvokeAsync<string?>("archiveStorage.getSeed");
                if (!string.IsNullOrWhiteSpace(seedJson))
                    Data = JsonSerializer.Deserialize<ArchiveDatabase>(seedJson, JsonOptions)
                        ?? throw new InvalidOperationException("Il catalogo iniziale non è valido.");
            }
        }
        catch (Exception ex)
        {
            LoadError = "L'archivio locale non è stato modificato. Per evitare perdite di dati, il caricamento e il salvataggio automatico sono stati bloccati. Dettaglio: " + ex.Message;
            return;
        }

        UpgradeSchema();
        if (Data.Seasons.Count == 0)
            Data.Seasons.Add(new ArchiveSeason { Name = "2026/2027", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2027, 6, 30), IsActive = true });
        if (Data.Championships.Count == 0)
        {
            Data.Championships.Add(new ArchiveChampionship { Name = "Serie A1 Femminile", Category = "Senior", Gender = "Femminile" });
            Data.Championships.Add(new ArchiveChampionship { Name = "Serie B Maschile", Category = "Senior", Gender = "Maschile" });
            Data.Championships.Add(new ArchiveChampionship { Name = "Serie Giovanile", Category = "Giovanile", Gender = "" });
        }
        IsLoaded = true;
        await SaveAsync(syncCloud: false);
    }

    public async Task<string?> ExportStoredArchiveRawAsync()
        => await js.InvokeAsync<string?>("archiveStorage.get", StorageKey);


    private void UpgradeSchema()
    {
        if (Data.SchemaVersion < 2)
        {
            // In schema 1 IsSelected existed but was not used by the application;
            // its default value must not make every athlete appear already called up.
            foreach (var entry in Data.TeamEntries.Where(x => x.EntryType != "TEAM"))
                entry.IsSelected = false;
            Data.SchemaVersion = 2;
        }
    }

    public async Task SaveAsync(bool syncCloud = true)
    {
        await _saveLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(Data, JsonOptions);
            await js.InvokeVoidAsync("archiveStorage.saveArchive", StorageKey, BackupKey, json);
            // Il salvataggio locale resta prioritario; il cloud viene aggiornato solo
            // dopo il collegamento e la sincronizzazione iniziale scelti dall'utente.
            if (syncCloud) await cloud.TrySyncAsync(Data);
            Changed?.Invoke();
        }
        finally { _saveLock.Release(); }
    }

    public async Task<bool> ApplyRemoteUpdateAsync(RemoteArchiveUpdate update)
    {
        await _saveLock.WaitAsync();
        try
        {
            // Non sostituire mai dati locali che nel frattempo sono diventati pendenti
            // o una versione cloud diversa da quella appena controllata.
            if (LoadError is not null || !IsLoaded || cloud.HasPendingChanges ||
                !string.Equals(cloud.RemoteSha, update.PreviousSha, StringComparison.Ordinal))
                return false;

            var backup = JsonSerializer.Serialize(Data, JsonOptions);
            await js.InvokeVoidAsync("archiveStorage.set", BackupKey, backup);

            var remote = update.Data;
            remote.Championships ??= [];
            remote.Seasons ??= [];
            remote.Teams ??= [];
            remote.People ??= [];
            remote.TeamEntries ??= [];
            Data = remote;
            UpgradeSchema();

            var json = JsonSerializer.Serialize(Data, JsonOptions);
            await js.InvokeVoidAsync("archiveStorage.saveArchive", StorageKey, BackupKey, json);
            await cloud.AcceptRemoteUpdateAsync(update.Sha);
            Changed?.Invoke();
            return true;
        }
        finally { _saveLock.Release(); }
    }


    public async Task<string> ExportJsonAsync()
    {
        return JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> CreateBackupAsync()
    {
        var json = await ExportJsonAsync();
        await js.InvokeVoidAsync("archiveStorage.set", BackupKey, json);
        return json;
    }

    public async Task ReplaceFromJsonAsync(string json, bool syncCloud = true)
    {
        var imported = JsonSerializer.Deserialize<ArchiveDatabase>(json, JsonOptions)
            ?? throw new InvalidOperationException("Il file JSON non contiene un archivio valido.");
        imported.Championships ??= [];
        imported.Seasons ??= [];
        imported.Teams ??= [];
        imported.People ??= [];
        imported.TeamEntries ??= [];
        if (imported.SchemaVersion < 2)
        {
            foreach (var entry in imported.TeamEntries.Where(x => x.EntryType != "TEAM")) entry.IsSelected = false;
            imported.SchemaVersion = 2;
        }
        if (imported.Championships.Count == 0 && imported.Teams.Count == 0 && imported.People.Count == 0)
            throw new InvalidOperationException("L'archivio selezionato è vuoto. L'archivio attuale non è stato modificato.");

        await CreateBackupAsync();
        Data = imported;
        IsLoaded = true;
        await SaveAsync(syncCloud);
    }

    public async Task<bool> RestoreBackupAsync()
    {
        var json = await js.InvokeAsync<string?>("archiveStorage.get", BackupKey);
        if (string.IsNullOrWhiteSpace(json)) return false;
        var restored = JsonSerializer.Deserialize<ArchiveDatabase>(json, JsonOptions);
        if (restored is null) return false;
        Data = restored;
        UpgradeSchema();
        IsLoaded = true;
        await SaveAsync();
        return true;
    }

    public async Task<bool> HasBackupAsync()
    {
        var json = await js.InvokeAsync<string?>("archiveStorage.get", BackupKey);
        return !string.IsNullOrWhiteSpace(json);
    }

    public async Task ImportWorkbookAsync(ArchiveWorkbookImport import)
    {
        var seasonName = string.IsNullOrWhiteSpace(import.Season) ? "2026/2027" : import.Season.Trim();
        var season = Data.Seasons.FirstOrDefault(x => Same(x.Name, seasonName));
        if (season is null)
        {
            season = new ArchiveSeason { Name = seasonName };
            Data.Seasons.Add(season);
        }

        foreach (var categoryName in import.Championships.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            GetOrAddChampionship(categoryName);

        foreach (var teamRow in import.Teams)
        {
            if (string.IsNullOrWhiteSpace(teamRow.Championship) || string.IsNullOrWhiteSpace(teamRow.Team)) continue;
            var championship = GetOrAddChampionship(teamRow.Championship);
            var team = Data.Teams.FirstOrDefault(x => Same(x.Name, teamRow.Team));
            if (team is null)
            {
                team = new ArchiveTeam { Name = teamRow.Team.Trim() };
                Data.Teams.Add(team);
            }
            if (!Data.TeamEntries.Any(x => x.TeamId == team.Id && x.ChampionshipId == championship.Id &&
                    x.SeasonId == season.Id && x.EntryType == "TEAM" && x.IsActive))
                Data.TeamEntries.Add(new ArchiveTeamEntry { TeamId = team.Id, ChampionshipId = championship.Id, SeasonId = season.Id, EntryType = "TEAM" });
        }

        foreach (var personRow in import.People)
        {
            if (string.IsNullOrWhiteSpace(personRow.Championship) || string.IsNullOrWhiteSpace(personRow.Team) || string.IsNullOrWhiteSpace(personRow.Name)) continue;
            var championship = GetOrAddChampionship(personRow.Championship);
            var team = Data.Teams.FirstOrDefault(x => Same(x.Name, personRow.Team));
            if (team is null)
            {
                team = new ArchiveTeam { Name = personRow.Team.Trim() };
                Data.Teams.Add(team);
            }
            if (!Data.TeamEntries.Any(x => x.TeamId == team.Id && x.ChampionshipId == championship.Id &&
                    x.SeasonId == season.Id && x.EntryType == "TEAM" && x.IsActive))
                Data.TeamEntries.Add(new ArchiveTeamEntry { TeamId = team.Id, ChampionshipId = championship.Id, SeasonId = season.Id, EntryType = "TEAM" });

            var rawNumber = (personRow.Number ?? "").Trim();
            var isOfficial = rawNumber.Length == 1 && "ABCDE".Contains(rawNumber.ToUpperInvariant());
            var personType = isOfficial ? "OFFICIAL" : "PLAYER";
            var person = Data.People.FirstOrDefault(x => Same(x.FullName, personRow.Name) && x.PersonType == personType);
            if (person is null)
            {
                person = new ArchivePerson { FullName = personRow.Name.Trim(), PersonType = personType };
                Data.People.Add(person);
            }

            var number = isOfficial ? "" : rawNumber;
            var letter = isOfficial ? rawNumber.ToUpperInvariant() : "";
            var entry = Data.TeamEntries.FirstOrDefault(x => x.TeamId == team.Id && x.ChampionshipId == championship.Id &&
                x.SeasonId == season.Id && x.PersonId == person.Id && x.EntryType == personType);
            // Una persona può avere una sola squadra attiva per lo stesso campionato e stagione.
            // Le associazioni precedenti vengono conservate, ma disattivate nello storico.
            foreach (var previous in Data.TeamEntries.Where(x => x.PersonId == person.Id && x.ChampionshipId == championship.Id &&
                x.SeasonId == season.Id && x.EntryType == personType && x.TeamId != team.Id && x.IsActive))
                previous.IsActive = false;
            if (entry is null)
                Data.TeamEntries.Add(new ArchiveTeamEntry { TeamId = team.Id, ChampionshipId = championship.Id, SeasonId = season.Id,
                    PersonId = person.Id, EntryType = personType, ShirtNumber = number, OfficialLetter = letter });
            else
            {
                entry.IsActive = true;
                entry.ShirtNumber = number;
                entry.OfficialLetter = letter;
            }
        }
        await SaveAsync();
    }

    public ArchiveChampionship GetOrAddChampionship(string name)
    {
        // Il modello Excel usa "Giovanile"; nell'archivio manteniamo la denominazione estesa concordata.
        if (Same(name, "Giovanile")) name = "Serie Giovanile";
        var found = Data.Championships.FirstOrDefault(x => Same(x.Name, name));
        if (found is not null) return found;
        var item = new ArchiveChampionship { Name = name.Trim() };
        Data.Championships.Add(item);
        return item;
    }

    public static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class ArchiveWorkbookImport
{
    public string Season { get; set; } = "";
    public List<string> Championships { get; set; } = [];
    public List<ArchiveWorkbookTeam> Teams { get; set; } = [];
    public List<ArchiveWorkbookPerson> People { get; set; } = [];
}
public sealed class ArchiveWorkbookTeam { public string Championship { get; set; } = ""; public string Team { get; set; } = ""; }
public sealed class ArchiveWorkbookPerson { public string Championship { get; set; } = ""; public string Team { get; set; } = ""; public string Name { get; set; } = ""; public string Number { get; set; } = ""; }

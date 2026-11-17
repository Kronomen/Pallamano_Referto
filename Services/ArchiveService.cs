using System.Text.Json;
using System.Globalization;
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

    private static readonly TimeSpan LocalStorageTimeout = TimeSpan.FromSeconds(10);

    private async Task<T?> InvokeArchiveStorageAsync<T>(string identifier, params object?[] args)
    {
        using var timeout = new CancellationTokenSource(LocalStorageTimeout);
        return await js.InvokeAsync<T?>(identifier, timeout.Token, args);
    }

    private async Task InvokeArchiveStorageVoidAsync(string identifier, params object?[] args)
    {
        using var timeout = new CancellationTokenSource(LocalStorageTimeout);
        await js.InvokeVoidAsync(identifier, timeout.Token, args);
    }

    public async Task<string> SynchronizeSharedArchiveAsync()
    {
        if (!cloud.IsAuthenticated || !cloud.HasCheckedRemote)
            return "Archivio locale disponibile. Accesso cloud non attivo: sincronizzazione condivisa non eseguita.";

        // Prima recupera le modifiche locali pendenti: non vengono mai scartate
        // per fare spazio alla copia centrale.
        if (cloud.HasPendingChanges)
        {
            await cloud.RetryPendingSyncAsync(Data);
            if (cloud.HasPendingChanges || cloud.HasSyncConflict)
                throw new InvalidOperationException(cloud.Status);
            await SaveAsync(syncCloud: false);
        }

        if (cloud.HasRemoteArchive)
        {
            var remote = await cloud.DownloadRemoteAsync();
            if (remote is null)
                throw new InvalidOperationException("Il cloud segnala un archivio presente ma non ha restituito i dati.");

            // Il cloud è la fonte centrale unica. La copia locale precedente
            // è già salvata nel backup prima di essere sostituita.
            await ReplaceWithRemoteArchiveAsync(remote);
            await cloud.EnableAutoSyncAsync();
            // Pubblica la normalizzazione dei nominativi anche nell'archivio centrale.
            await cloud.TrySyncAsync(Data);

            return "Archivio centrale caricato sul dispositivo. Backup locale conservato e sincronizzazione automatica attiva.";
        }

        await cloud.UploadInitialAsync(Data);
        return "Prima copia dell'archivio condiviso creata automaticamente.";
    }

    public async Task InitializeAsync()
    {
        if (IsLoaded || !string.IsNullOrWhiteSpace(LoadError)) return;
        var shouldPersist = false;
        try
        {
            var json = await InvokeArchiveStorageAsync<string>("archiveStorage.get", StorageKey);
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
                Data.DeletedChampionshipIds ??= [];
                Data.DeletedSeasonIds ??= [];
                Data.DeletedTeamIds ??= [];
                Data.DeletedPersonIds ??= [];
                Data.DeletedTeamEntryIds ??= [];
            }
            else
            {
                var seedJson = await InvokeArchiveStorageAsync<string>("archiveStorage.getSeed");
                shouldPersist = true;
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

        var previousSchemaVersion = Data.SchemaVersion;
        UpgradeSchema();
        if (Data.SchemaVersion != previousSchemaVersion) shouldPersist = true;
        if (NormalizeNames(Data)) shouldPersist = true;
        if (Data.Seasons.Count == 0)
        {
            Data.Seasons.Add(new ArchiveSeason { Name = "2026/2027", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2027, 6, 30), IsActive = true });
            shouldPersist = true;
        }
        if (Data.Championships.Count == 0)
        {
            Data.Championships.Add(new ArchiveChampionship { Name = "Serie A1 Femminile", Category = "Senior", Gender = "Femminile" });
            Data.Championships.Add(new ArchiveChampionship { Name = "Serie B Maschile", Category = "Senior", Gender = "Maschile" });
            Data.Championships.Add(new ArchiveChampionship { Name = "Serie Giovanile", Category = "Giovanile", Gender = "" });
            shouldPersist = true;
        }
        // Non riscrivere l'archivio IndexedDB a ogni apertura: basta leggere
        // i dati esistenti. Persistiamo solo seed, valori predefiniti o migrazioni.
        if (shouldPersist) await SaveAsync(syncCloud: false);
        IsLoaded = true;
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
            // Regola unica applicata a ogni salvataggio: inserimenti manuali,
            // importazioni e modifiche passano tutti da qui.
            NormalizeNames(Data);
            var json = JsonSerializer.Serialize(Data, JsonOptions);
            await InvokeArchiveStorageVoidAsync("archiveStorage.saveArchive", StorageKey, BackupKey, json);
            // Il salvataggio locale resta prioritario. Dopo il tentativo cloud
            // risalviamo anche l'eventuale merge automatico ricevuto dal server.
            if (syncCloud)
            {
                await cloud.TrySyncAsync(Data);
                var reconciledJson = JsonSerializer.Serialize(Data, JsonOptions);
                await InvokeArchiveStorageVoidAsync("archiveStorage.saveArchive", StorageKey, BackupKey, reconciledJson);
            }
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


    public async Task ReplaceWithRemoteArchiveAsync(ArchiveDatabase remote)
    {
        if (remote is null) throw new ArgumentNullException(nameof(remote));
        remote.Championships ??= [];
        remote.Seasons ??= [];
        remote.Teams ??= [];
        remote.People ??= [];
        remote.TeamEntries ??= [];

        await _saveLock.WaitAsync();
        try
        {
            // La copia locale viene conservata prima di adottare la versione centrale.
            // Questo evita collisioni artificiali con il vecchio seed e permette
            // di recuperare eventuali dati locali non ancora sincronizzati.
            var backup = JsonSerializer.Serialize(Data, JsonOptions);
            await InvokeArchiveStorageVoidAsync("archiveStorage.set", BackupKey, backup);

            Data = remote;
            UpgradeSchema();
            NormalizeNames(Data);
            IsLoaded = true;

            var json = JsonSerializer.Serialize(Data, JsonOptions);
            await InvokeArchiveStorageVoidAsync("archiveStorage.saveArchive", StorageKey, BackupKey, json);
            Changed?.Invoke();
        }
        finally { _saveLock.Release(); }
    }

    public async Task<int> MergeRemoteArchiveAsync(ArchiveDatabase remote)
    {
        if (remote is null) throw new ArgumentNullException(nameof(remote));
        remote.Championships ??= [];
        remote.Seasons ??= [];
        remote.Teams ??= [];
        remote.People ??= [];
        remote.TeamEntries ??= [];

        // Serializza il merge con i normali salvataggi: altrimenti una modifica
        // locale concorrente potrebbe essere esclusa dalla copia appena preparata.
        await _saveLock.WaitAsync();
        try
        {
            // Il cloud resta la base; il merge lavora su una copia e non tocca Data
            // finché tutte le collezioni non hanno superato i controlli di conflitto.
            var merged = JsonSerializer.Deserialize<ArchiveDatabase>(JsonSerializer.Serialize(remote), JsonOptions)
                ?? throw new InvalidOperationException("Impossibile preparare la copia cloud per il merge.");
            var added = merged.MergeFrom(Data);

            var backup = JsonSerializer.Serialize(Data, JsonOptions);
            await js.InvokeVoidAsync("archiveStorage.set", BackupKey, backup);
            Data = merged;
            NormalizeNames(Data);
            IsLoaded = true;
            var json = JsonSerializer.Serialize(Data, JsonOptions);
            await js.InvokeVoidAsync("archiveStorage.saveArchive", StorageKey, BackupKey, json);
            Changed?.Invoke();
            return added;
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
        NormalizeNames(imported);
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

    public async Task<(int AddedPeople, int AddedEntries, int UpdatedEntries, int Skipped)> ImportPdfRosterAsync(ArchivePdfRosterImport import)
    {
        // Esegue l'importazione su una copia di lavoro: se una riga successiva
        // non supera i controlli, nessuna modifica parziale resta nell'archivio in memoria.
        var originalData = Data;
        var workingData = JsonSerializer.Deserialize<ArchiveDatabase>(
            JsonSerializer.Serialize(originalData, JsonOptions), JsonOptions)
            ?? throw new InvalidOperationException("Impossibile preparare una copia di sicurezza dell'archivio.");

        Data = workingData;
        (int AddedPeople, int AddedEntries, int UpdatedEntries, int Skipped) result;
        try
        {
        var championship = Data.Championships.FirstOrDefault(x => Same(x.Name, import.Championship))
            ?? throw new InvalidOperationException("Il campionato selezionato non esiste più nell'archivio.");
        var season = Data.Seasons.FirstOrDefault(x => Same(x.Name, import.Season))
            ?? throw new InvalidOperationException("La stagione selezionata non esiste più nell'archivio.");
        var addedPeople = 0; var addedEntries = 0; var updatedEntries = 0; var skipped = 0;
        var resolvedTeamIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var teamRow in import.Teams)
        {
            ArchiveTeam? team = string.IsNullOrWhiteSpace(teamRow.TeamId) ? null : Data.Teams.FirstOrDefault(x => x.Id == teamRow.TeamId);
            if (!string.IsNullOrWhiteSpace(teamRow.TeamId) && team is null)
                throw new InvalidOperationException("Una delle squadre selezionate non è presente nell'archivio.");
            if (team is null)
            {
                var detectedName = (teamRow.DetectedName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(detectedName)) throw new InvalidOperationException("Il nome della nuova squadra non è valido.");
                team = Data.Teams.FirstOrDefault(x => Same(x.Name, detectedName));
                if (team is null) { team = new ArchiveTeam { Name = detectedName, IsActive = true }; Data.Teams.Add(team); }
            }
            if (!resolvedTeamIds.Add(team.Id))
                throw new InvalidOperationException("Le due rose risultano associate alla stessa squadra. Controlla l'abbinamento prima di confermare.");
            var teamRegistration = Data.TeamEntries.FirstOrDefault(x => x.TeamId == team.Id &&
                x.ChampionshipId == championship.Id && x.SeasonId == season.Id && x.EntryType == "TEAM");
            if (teamRegistration is null)
                Data.TeamEntries.Add(new ArchiveTeamEntry { TeamId = team.Id, ChampionshipId = championship.Id, SeasonId = season.Id, EntryType = "TEAM", IsActive = true });
            else teamRegistration.IsActive = true;

            foreach (var row in teamRow.People.Where(x => x.Selected))
            {
                var name = (row.Name ?? "").Trim();
                var code = (row.NumberOrLetter ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code)) { skipped++; continue; }
                var isOfficial = row.PersonType == "OFFICIAL";
                var type = isOfficial ? "OFFICIAL" : "PLAYER";
                if (isOfficial ? ! "ABCDE".Contains(code) : !(code == "0" || code == "00" || (int.TryParse(code, out var n) && n >= 1 && n <= 99)))
                    throw new InvalidOperationException($"Numero/lettera non valido per {name}: {code}.");

                ArchivePerson? person = null;
                if (row.ImportChoice.StartsWith("EXISTING:", StringComparison.Ordinal))
                {
                    var selectedId = row.ImportChoice.Substring("EXISTING:".Length);
                    person = Data.People.FirstOrDefault(x => x.Id == selectedId && Same(x.FullName, name) && x.PersonType == type);
                    if (person is null) throw new InvalidOperationException($"La persona selezionata per {name} non è più disponibile.");
                }
                else if (row.ImportChoice == "NEW")
                {
                    person = new ArchivePerson { FullName = name, PersonType = type };
                    Data.People.Add(person); addedPeople++;
                }
                else
                {
                    person = Data.People.FirstOrDefault(x => Same(x.FullName, name) && x.PersonType == type);
                    if (person is null) { person = new ArchivePerson { FullName = name, PersonType = type }; Data.People.Add(person); addedPeople++; }
                }

                var entry = Data.TeamEntries.FirstOrDefault(x => x.PersonId == person.Id && x.TeamId == team.Id &&
                    x.ChampionshipId == championship.Id && x.SeasonId == season.Id && x.EntryType == type);
                var activeElsewhere = Data.TeamEntries.Where(x => x.PersonId == person.Id && x.ChampionshipId == championship.Id &&
                    x.SeasonId == season.Id && x.EntryType == type && x.IsActive && x.TeamId != team.Id).ToList();
                if (activeElsewhere.Count > 0)
                {
                    if (!row.ImportChoice.StartsWith("EXISTING:", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Per {name} devi scegliere se trasferire la persona già registrata oppure creare un nuovo omonimo.");
                    foreach (var previous in activeElsewhere) previous.IsActive = false;
                }
                if (entry is null)
                {
                    Data.TeamEntries.Add(new ArchiveTeamEntry { PersonId = person.Id, TeamId = team.Id, ChampionshipId = championship.Id,
                        SeasonId = season.Id, EntryType = type, ShirtNumber = isOfficial ? "" : code, OfficialLetter = isOfficial ? code : "" });
                    addedEntries++;
                }
                else
                {
                    var existing = isOfficial ? entry.OfficialLetter : entry.ShirtNumber;
                    if (string.IsNullOrWhiteSpace(existing))
                    {
                        if (isOfficial) entry.OfficialLetter = code; else entry.ShirtNumber = code;
                    }
                    else if (!Same(existing, code)) skipped++;
                    if (!entry.IsActive) updatedEntries++;
                    entry.IsActive = true;
                }
            }
        }
        result = (addedPeople, addedEntries, updatedEntries, skipped);
        }
        catch
        {
            Data = originalData;
            throw;
        }

        // Il commit locale/cloud avviene solo dopo che tutte le righe sono state elaborate.
        // Se fallisce anche il salvataggio, ripristina l'istanza precedente in memoria.
        try
        {
            await SaveAsync();
        }
        catch
        {
            Data = originalData;
            throw;
        }
        return result;
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

    // Uniforma i nomi senza cambiare gli ID o le associazioni dell'archivio.
    // 1 parola: tutta maiuscola; 2 parole: cognome maiuscolo + nome normale;
    // oltre 2 parole: prime due maiuscole, successive in formato Nome.
    public static string FormatPersonName(string? value)
    {
        var words = (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        if (words.Length == 1) return words[0].ToUpperInvariant();

        var textInfo = CultureInfo.GetCultureInfo("it-IT").TextInfo;
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = i < 2
                ? words[i].ToUpperInvariant()
                : textInfo.ToTitleCase(words[i].ToLowerInvariant());
        }
        return string.Join(" ", words);
    }

    public static bool NormalizeNames(ArchiveDatabase archive)
    {
        var changed = false;
        foreach (var team in archive.Teams)
        {
            var normalized = (team.Name ?? "").Trim().ToUpperInvariant();
            if (team.Name != normalized) { team.Name = normalized; changed = true; }
            var shortName = (team.ShortName ?? "").Trim().ToUpperInvariant();
            if (team.ShortName != shortName) { team.ShortName = shortName; changed = true; }
        }
        foreach (var person in archive.People)
        {
            var normalized = FormatPersonName(person.FullName);
            if (person.FullName != normalized) { person.FullName = normalized; changed = true; }
        }
        return changed;
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

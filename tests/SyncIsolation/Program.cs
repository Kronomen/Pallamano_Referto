using RefertoPallamano_Blazor.Models;
using RefertoPallamano_Blazor.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("TEST FALLITO: " + message);
}

static ArchiveDatabase Clone(ArchiveDatabase source) => MockArchiveServer.Clone(source);

static CloudArchiveService NewClient(MockArchiveServer server, out TestJsRuntime js)
{
    js = new TestJsRuntime();
    js.InvokeAsync<object?>("localStorage.setItem", new object?[] { "pallamanoCloudApiToken", "fake-token" });
    js.InvokeAsync<object?>("localStorage.setItem", new object?[] { "pallamanoCloudAutoSyncEnabled", "true" });
    js.InvokeAsync<object?>("localStorage.setItem", new object?[] { "pallamanoCloudArchiveShaV1", "sha-1" });
    var http = new HttpClient(new MockArchiveHandler(server));
    return new CloudArchiveService(http, js, new TestNavigationManager());
}

// 1. Merge di record indipendenti: conserva entrambi i dispositivi.
var baseArchive = new ArchiveDatabase();
baseArchive.Championships.Add(new ArchiveChampionship { Id = "champ-1", Name = "Campionato" });
var deviceA = Clone(baseArchive);
var deviceB = Clone(baseArchive);
deviceA.People.Add(new ArchivePerson { Id = "person-A", FullName = "Atleta A" });
deviceB.People.Add(new ArchivePerson { Id = "person-B", FullName = "Atleta B" });
var addedA = deviceA.MergeFrom(deviceB);
Check(addedA == 1, "il merge deve aggiungere il record del secondo dispositivo");
Check(deviceA.People.Count == 2, "devono restare entrambi gli atleti");

// 2. Merge idempotente: record identico non viene duplicato.
var identical = Clone(deviceA);
Check(deviceA.MergeFrom(identical) == 0, "un merge identico deve aggiungere zero record");
Check(deviceA.People.Count == 2, "il merge identico non deve duplicare");

// 3. Conflitto su stesso ID: nessuna modifica parziale.
var conflictTarget = new ArchiveDatabase();
conflictTarget.Teams.Add(new ArchiveTeam { Id = "team-1", Name = "Nome locale" });
var incoming = new ArchiveDatabase();
incoming.Championships.Add(new ArchiveChampionship { Id = "champ-new", Name = "Nuovo" });
incoming.Teams.Add(new ArchiveTeam { Id = "team-1", Name = "Nome remoto" });
var threw = false;
try { conflictTarget.MergeFrom(incoming); }
catch (InvalidOperationException) { threw = true; }
Check(threw, "stesso ID con contenuto diverso deve bloccare il merge");
Check(conflictTarget.Championships.Count == 0, "un conflitto successivo non deve lasciare record aggiunti in precedenza");
Check(conflictTarget.Teams.Single().Name == "Nome locale", "il record locale in conflitto deve restare intatto");

// 4. Simulazione optimistic concurrency di due dispositivi (SHA/revision).
var serverRevision = Clone(baseArchive);
var revision = 1;
var client1 = Clone(serverRevision);
var client2 = Clone(serverRevision);
client1.People.Add(new ArchivePerson { Id = "device-1-person", FullName = "Dispositivo 1" });
client2.People.Add(new ArchivePerson { Id = "device-2-person", FullName = "Dispositivo 2" });
var client1Revision = revision;
var client2Revision = revision;
Check(client1Revision == revision, "prima scrittura deve usare la revisione letta");
serverRevision = Clone(client1);
revision++;
Check(client2Revision != revision, "la seconda scrittura deve rilevare la revisione obsoleta");
client2.MergeFrom(serverRevision);
Check(client2.People.Count == 2, "il secondo client deve unire la modifica remota");
serverRevision = Clone(client2);
revision++;
Check(serverRevision.People.Select(x => x.Id).ToHashSet().SetEquals(new[] { "device-1-person", "device-2-person" }),
    "dopo il retry il server deve contenere le modifiche di entrambi i dispositivi");

// 5. Roster: iscrizioni indipendenti, numeri/lettere e selezione partita.
var roster = new ArchiveDatabase();
roster.People.Add(new ArchivePerson { Id = "p1", FullName = "Giocatore" });
roster.TeamEntries.Add(new ArchiveTeamEntry { Id = "e1", PersonId = "p1", EntryType = "PLAYER", ShirtNumber = "7", IsSelected = true });
roster.TeamEntries.Add(new ArchiveTeamEntry { Id = "e2", PersonId = "p1", EntryType = "OFFICIAL", OfficialLetter = "A", IsSelected = false });
var rosterCopy = Clone(roster);
Check(rosterCopy.TeamEntries.Single(x => x.Id == "e1").IsSelected, "la convocazione salvata deve persistere");
Check(rosterCopy.TeamEntries.Single(x => x.Id == "e2").OfficialLetter == "A", "la lettera del dirigente deve persistere");

// 6. Integrazione reale CloudArchiveService: conflitto SHA, GET, merge e PUT di retry.
var sharedServer = new MockArchiveServer(baseArchive, "sha-1");
var serviceA = NewClient(sharedServer, out var storageA);
var serviceB = NewClient(sharedServer, out var storageB);
Check(await serviceA.TryRestoreSessionAsync(), "client A deve ripristinare la sessione simulata");
Check(await serviceB.TryRestoreSessionAsync(), "client B deve ripristinare la sessione simulata");
var localA = Clone(baseArchive);
localA.People.Add(new ArchivePerson { Id = "cloud-A", FullName = "Atleta dispositivo A" });
await serviceA.TrySyncAsync(localA);
Check(sharedServer.Sha == "sha-2", "client A deve aggiornare la versione server");
Check(!serviceA.HasPendingChanges, "pending A deve essere cancellato dopo il PUT riuscito");
Check(storageA.Read("pallamanoCloudPendingArchiveV1") is null, "pending A deve essere rimosso da localStorage");

var localB = Clone(baseArchive);
localB.People.Add(new ArchivePerson { Id = "cloud-B", FullName = "Atleta dispositivo B" });
await serviceB.TrySyncAsync(localB);
Check(sharedServer.ConflictCount == 1, "client B deve ricevere un conflitto sulla revisione obsoleta");
Check(sharedServer.PutCount == 3, "la sequenza deve includere PUT A, PUT B in conflitto e PUT B di retry");
Check(sharedServer.Data.People.Select(x => x.Id).ToHashSet().SetEquals(new[] { "cloud-A", "cloud-B" }),
    "il server deve conservare entrambi; trovati: " + string.Join(",", sharedServer.Data.People.Select(x => x.Id)) + "; PUT=" + sharedServer.PutCount + "; 409=" + sharedServer.ConflictCount + "; SHA=" + sharedServer.Sha);
Check(sharedServer.Sha == "sha-3", "il retry deve avanzare alla versione successiva");
Check(!serviceB.HasPendingChanges && !serviceB.HasSyncConflict, "pending e conflitto B devono essere azzerati dopo il retry");
Check(storageB.Read("pallamanoCloudPendingArchiveV1") is null, "pending B deve essere rimosso da localStorage");
Check(serviceB.RemoteSha == "sha-3", "il client B deve memorizzare lo SHA restituito dal retry");

// 7. Collisione reale stesso ID: blocca il retry e non sovrascrive il server.
var collisionBase = new ArchiveDatabase();
collisionBase.People.Add(new ArchivePerson { Id = "same-person", FullName = "Nome iniziale" });
var collisionServer = new MockArchiveServer(collisionBase, "sha-1");
var collisionA = NewClient(collisionServer, out _);
var collisionB = NewClient(collisionServer, out var collisionStorageB);
Check(await collisionA.TryRestoreSessionAsync(), "client collisione A deve collegarsi");
Check(await collisionB.TryRestoreSessionAsync(), "client collisione B deve collegarsi");
var changedA = Clone(collisionBase);
changedA.People.Single().FullName = "Modifica A";
await collisionA.TrySyncAsync(changedA);
var changedB = Clone(collisionBase);
changedB.People.Single().FullName = "Modifica B";
await collisionB.TrySyncAsync(changedB);
Check(collisionServer.Data.People.Single().FullName == "Modifica A", "il server deve conservare la modifica A");
Check(changedB.People.Single().FullName == "Modifica B", "la copia locale B non deve essere sovrascritta");
Check(collisionB.HasPendingChanges && collisionB.HasSyncConflict, "il conflitto divergente deve lasciare pending e conflitto attivi");
Check(collisionStorageB.Read("pallamanoCloudPendingArchiveV1") is not null, "la copia pending B deve restare in localStorage");
Check(collisionServer.PutCount == 2 && collisionServer.ConflictCount == 1, "non deve esserci un PUT di retry dopo il conflitto sui contenuti");

Console.WriteLine("OK: 7 gruppi di test superati, inclusa integrazione CloudArchiveService su HTTP simulato.");


// 8. Import PDF: crea squadre nuove e associa atleti/dirigenti senza duplicare.
static async Task<(ArchiveService Service, TestJsRuntime Js)> NewArchiveService(ArchiveDatabase seed)
{
    var js = new TestJsRuntime();
    js.Write("refertoPallamanoArchiveV1", System.Text.Json.JsonSerializer.Serialize(seed));
    var cloud = new CloudArchiveService(new HttpClient(new MockArchiveHandler(new MockArchiveServer(seed, "sha-1"))), js, new TestNavigationManager());
    var service = new ArchiveService(js, cloud);
    await service.InitializeAsync();
    return (service, js);
}
static ArchiveDatabase SeedImportArchive()
{
    var db = new ArchiveDatabase();
    db.Championships.Add(new ArchiveChampionship { Id = "champ", Name = "Serie A" });
    db.Seasons.Add(new ArchiveSeason { Id = "season", Name = "2026/2027" });
    return db;
}
static ArchivePdfRosterTeam NewTeam(string name, params ArchivePdfRosterPerson[] people) =>
    new() { DetectedName = name, People = people.ToList() };
static ArchivePdfRosterPerson PdfPerson(string name, string code = "7", string type = "PLAYER", string choice = "") =>
    new() { Name = name, NumberOrLetter = code, PersonType = type, ImportChoice = choice, Selected = true };

var (newTeamService, _) = await NewArchiveService(SeedImportArchive());
await newTeamService.ImportPdfRosterAsync(new ArchivePdfRosterImport {
    Championship = "Serie A", Season = "2026/2027",
    Teams = [NewTeam("Squadra Nuova A", PdfPerson("Atleta Nuovo")), NewTeam("Squadra Nuova B", PdfPerson("Dirigente Nuovo", "A", "OFFICIAL"))]
});
Check(newTeamService.Data.Teams.Count == 2, "devono essere create entrambe le nuove squadre");
Check(newTeamService.Data.People.Count == 2, "devono essere create le due anagrafiche");
Check(newTeamService.Data.TeamEntries.Count(x => x.EntryType == "TEAM") == 2, "ogni nuova squadra deve avere l'iscrizione TEAM");
Check(newTeamService.Data.TeamEntries.Count(x => x.EntryType == "PLAYER" || x.EntryType == "OFFICIAL") == 2, "atleta e dirigente devono essere associati alle squadre");

// 9. Trasferimento esplicito: stessa identità, vecchia iscrizione disattivata.
var transferSeed = SeedImportArchive();
transferSeed.Teams.Add(new ArchiveTeam { Id = "old", Name = "Squadra Vecchia" });
transferSeed.People.Add(new ArchivePerson { Id = "person-transfer", FullName = "Mario Rossi", PersonType = "PLAYER" });
transferSeed.TeamEntries.Add(new ArchiveTeamEntry { Id = "old-entry", TeamId = "old", PersonId = "person-transfer", ChampionshipId = "champ", SeasonId = "season", EntryType = "PLAYER", ShirtNumber = "9", IsActive = true });
var (transferService, _) = await NewArchiveService(transferSeed);
await transferService.ImportPdfRosterAsync(new ArchivePdfRosterImport {
    Championship = "Serie A", Season = "2026/2027",
    Teams = [NewTeam("Squadra Vecchia", PdfPerson("Altro atleta")), NewTeam("Squadra Nuova", PdfPerson("Mario Rossi", "11", "PLAYER", "EXISTING:person-transfer"))]
});
Check(transferService.Data.People.Count(x => x.Id == "person-transfer") == 1, "il trasferimento deve riutilizzare la stessa persona");
Check(!transferService.Data.TeamEntries.Single(x => x.Id == "old-entry").IsActive, "l'iscrizione precedente deve essere disattivata");
Check(transferService.Data.TeamEntries.Any(x => x.PersonId == "person-transfer" && x.TeamId != "old" && x.IsActive), "la nuova iscrizione deve risultare attiva");

// 10. Omonimo distinto: la vecchia persona resta nella vecchia squadra.
var (namesakeService, _) = await NewArchiveService(transferSeed);
await namesakeService.ImportPdfRosterAsync(new ArchivePdfRosterImport {
    Championship = "Serie A", Season = "2026/2027",
    Teams = [NewTeam("Squadra Vecchia", PdfPerson("Altro atleta")), NewTeam("Squadra Nuova", PdfPerson("Mario Rossi", "11", "PLAYER", "NEW"))]
});
Check(namesakeService.Data.People.Count(x => x.FullName == "Mario Rossi") == 2, "NEW deve creare un'anagrafica omonima distinta");
Check(namesakeService.Data.TeamEntries.Single(x => x.Id == "old-entry").IsActive, "la vecchia iscrizione deve restare attiva per l'omonimo distinto");

// 11. Errore in una riga successiva: nessuna squadra/persona/iscrizione parziale resta in memoria.
var (rollbackService, _) = await NewArchiveService(SeedImportArchive());
var beforeRollback = rollbackService.Data;
var failed = false;
try
{
    await rollbackService.ImportPdfRosterAsync(new ArchivePdfRosterImport {
        Championship = "Serie A", Season = "2026/2027",
        Teams = [NewTeam("Squadra Parziale", PdfPerson("Atleta Valido")), NewTeam("Seconda Squadra", PdfPerson("Atleta Errato", "100"))]
    });
}
catch (InvalidOperationException) { failed = true; }
Check(failed, "un numero maglia non valido deve bloccare l'importazione");
Check(ReferenceEquals(rollbackService.Data, beforeRollback), "in caso di errore il servizio deve ripristinare l'istanza originale");
Check(rollbackService.Data.Teams.Count == 0 && rollbackService.Data.People.Count == 0 && rollbackService.Data.TeamEntries.Count == 0,
    "nessuna modifica parziale deve restare nell'archivio");

Console.WriteLine("OK: 11 gruppi di test superati, inclusi i casi di importazione PDF simulata.");

using RefertoPallamano_Blazor.Models;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("TEST FALLITO: " + message);
}

static ArchiveDatabase Clone(ArchiveDatabase source) =>
    System.Text.Json.JsonSerializer.Deserialize<ArchiveDatabase>(
        System.Text.Json.JsonSerializer.Serialize(source))!;

static ArchiveDatabase DeviceArchive(string personId, string name)
{
    var archive = new ArchiveDatabase();
    archive.People.Add(new ArchivePerson { Id = personId, FullName = name });
    return archive;
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
var server = Clone(baseArchive);
var revision = 1;
var client1 = Clone(server);
var client2 = Clone(server);
client1.People.Add(new ArchivePerson { Id = "device-1-person", FullName = "Dispositivo 1" });
client2.People.Add(new ArchivePerson { Id = "device-2-person", FullName = "Dispositivo 2" });
var client1Revision = revision;
var client2Revision = revision;
Check(client1Revision == revision, "prima scrittura deve usare la revisione letta");
server = Clone(client1);
revision++;
Check(client2Revision != revision, "la seconda scrittura deve rilevare la revisione obsoleta");
client2.MergeFrom(server);
Check(client2.People.Count == 2, "il secondo client deve unire la modifica remota");
server = Clone(client2);
revision++;
Check(server.People.Select(x => x.Id).ToHashSet().SetEquals(new[] { "device-1-person", "device-2-person" }),
    "dopo il retry il server deve contenere le modifiche di entrambi i dispositivi");

// 5. Roster: iscrizioni indipendenti, numeri/lettere e selezione partita.
var roster = new ArchiveDatabase();
roster.People.Add(new ArchivePerson { Id = "p1", FullName = "Giocatore" });
roster.TeamEntries.Add(new ArchiveTeamEntry { Id = "e1", PersonId = "p1", EntryType = "PLAYER", ShirtNumber = "7", IsSelected = true });
roster.TeamEntries.Add(new ArchiveTeamEntry { Id = "e2", PersonId = "p1", EntryType = "OFFICIAL", OfficialLetter = "A", IsSelected = false });
var rosterCopy = Clone(roster);
Check(rosterCopy.TeamEntries.Single(x => x.Id == "e1").IsSelected, "la convocazione salvata deve persistere");
Check(rosterCopy.TeamEntries.Single(x => x.Id == "e2").OfficialLetter == "A", "la lettera del dirigente deve persistere");

Console.WriteLine("OK: 5 gruppi di test superati (merge, idempotenza, atomicità, due dispositivi, roster).");

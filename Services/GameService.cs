using System.Text.Json;
using RefertoPallamano_Blazor.Models;

namespace RefertoPallamano_Blazor.Services;

public sealed class GameService : IAsyncDisposable
{
    private CancellationTokenSource? _clockCts;
    private Task? _clockTask;
    private GameState? _undoState;

    public GameState State { get; private set; } = CreateNewGame();
    public EventRecord? PendingEvent { get; private set; }
    public (string Team, int Index)? PendingPenalty { get; private set; }
    public (string Team, string Number, bool IsStaff)? PendingYellow { get; private set; }
    public (string Team, int Index)? PendingStaff { get; private set; }
    public bool ShowPeriodEnd { get; private set; }
    public bool ShowShootoutStart { get; private set; }
    public bool ShowShootoutFirstTeam { get; private set; }
    public bool ShowShootoutWinner { get; private set; }
    public bool ShowMatchEnd { get; private set; }
    public (string Team, int Index)? PendingShootout { get; private set; }
    public bool ShowConfiguration { get; private set; } = true;
    public bool PeriodStartConfirmed { get; private set; } = true;
    public string? ValidationError { get; private set; }
    public bool CanUndo => _undoState is not null;
    public string ShootoutWinnerName => State.ShootoutScoreA > State.ShootoutScoreB ? State.Casa.Name : State.Ospiti.Name;
    public bool CanEditEvents => !State.Running && State.TimeoutRemainingSeconds <= 0 && PendingEvent is null && !State.ShootoutStarted;
    public event Action? Changed;

    public void OpenConfiguration()
    {
        if (!State.MatchStarted) { ValidationError = null; ShowConfiguration = true; Notify(); }
    }

    public void CloseConfigurationWithoutStarting()
    {
        if (!State.MatchStarted) { ValidationError = null; ShowConfiguration = false; Notify(); }
    }

    public bool CloseConfiguration()
    {
        // La distinta è parte integrante del referto: numeri e posizioni vengono
        // sempre mantenuti, mentre il nome è facoltativo.
        ValidationError = null;
        EnsureRosterArrays(State.Casa);
        EnsureRosterArrays(State.Ospiti);
        if (!ValidateRoster(out var error)) { ValidationError = error; Notify(); return false; }
        ShowConfiguration = false;
        Notify();
        return true;
    }

    public void SetValidationMessage(string? message) { ValidationError = message; Notify(); }

    public void SetPlayerName(string team, int index, string? value)
    {
        var roster = Team(team);
        EnsureRosterArrays(roster);
        if (index < 0 || index >= 16) return;
        roster.PlayerNames[index] = value ?? string.Empty;
    }

    public void SetStaffName(string team, int index, string? value)
    {
        var roster = Team(team);
        EnsureRosterArrays(roster);
        if (index < 0 || index >= 5) return;
        roster.StaffNames[index] = value ?? string.Empty;
    }

    public void SetTeamColor(string team, string color)
    {
        var roster = Team(team);
        roster.Color = color;
        Notify();
    }

    public void Reset()
    {
        StopClock(); State = CreateNewGame(); State.MiniTimerSeconds = 0; State.MiniTimerRunning = false; ClearPending(); _undoState = null;
        PeriodStartConfirmed = true; ShowPeriodEnd = false; ShowShootoutStart = false; ShowShootoutFirstTeam = false; ShowShootoutWinner = false; ShowMatchEnd = false; PendingShootout = null; ShowConfiguration = true; ValidationError = null; Notify();
    }

    public bool SelectEvent(string type)
    {
        if (State.MatchFinished || State.ShootoutStarted || PendingEvent is not null) return false;
        if (!new[] { "GOAL", "PENALTY_GOAL", "PENALTY_MISS", "YELLOW", "TWO", "RED" }.Contains(type)) return false;
        Snapshot();
        PendingEvent = new EventRecord { Time = FormatTime(State.TimerSeconds), Team = "", Number = "—", Type = type, Text = EventDescription(type), Result = ScoreText() };
        State.Events.Add(PendingEvent);
        if (type is "TWO" or "RED") StopClockForDisciplinary();
        Notify(); return true;
    }

    public bool CompletePendingPlayer(string team, int index)
    {
        if (PendingEvent is null || State.MatchFinished || State.ShootoutStarted) return false;
        if (!TryGetPlayer(team, index, out var roster, out var number, out var numberLabel)) return false;

        PendingEvent.Team = team;
        PendingEvent.Number = numberLabel;

        if ((PendingEvent.Type is "GOAL" or "PENALTY_GOAL" or "PENALTY_MISS") && GetSuspensions(team, numberLabel).Any())
        {
            State.Events.Remove(PendingEvent);
            ClearPending(false);
            ValidationError = "2' in corso: GOAL e 7m non consentiti per questo giocatore.";
            Notify();
            return false;
        }

        switch (PendingEvent.Type)
        {
            case "GOAL":
                IncrementScore(team);
                PendingEvent.Text = "GOAL";
                PendingEvent.Result = ScoreText();
                FinishPendingEvent();
                return true;
            case "PENALTY_GOAL":
                IncrementScore(team);
                PendingEvent.Text = "7m GOAL";
                PendingEvent.Type = "PENALTY_GOAL";
                PendingEvent.Result = ScoreText();
                FinishPendingEvent();
                return true;
            case "PENALTY_MISS":
                PendingEvent.Text = "7m MISS";
                PendingEvent.Type = "PENALTY_MISS";
                PendingEvent.Result = ScoreText();
                FinishPendingEvent();
                return true;
            case "YELLOW":
                // Il PendingEvent è già nella lista State.Events. Deve essere escluso
                // dai conteggi: altrimenti la stessa prima ammonizione viene vista
                // come già assegnata al giocatore.
                var playerNumber = numberLabel;
                var alreadyYellow = State.Events.Any(e =>
                    !ReferenceEquals(e, PendingEvent) &&
                    e.Team == team && e.Number == playerNumber && e.Type == "YELLOW");
                var teamYellowCount = State.Events.Count(e =>
                    !ReferenceEquals(e, PendingEvent) &&
                    e.Team == team && e.Type == "YELLOW");

                if (alreadyYellow || teamYellowCount >= 3)
                {
                    PendingYellow = (team, playerNumber, false);
                    Notify();
                    return true;
                }
                PendingEvent.Text = "AMMONIZIONE";
                PendingEvent.Result = ScoreText();
                FinishPendingEvent();
                return true;
            case "TWO":
            {
                var previousTwos = State.Events.Count(e =>
                    !ReferenceEquals(e, PendingEvent) &&
                    e.Team == team && e.Number == numberLabel && e.Type == "TWO" &&
                    !e.Text.Contains("3x2", StringComparison.OrdinalIgnoreCase));
                if (previousTwos >= 2)
                {
                    // 3° 2' dello stesso giocatore: nel Registro Gara devono
                    // comparire DUE eventi distinti, nello stesso identico secondo:
                    // 1) ESCLUSIONE 2 MINUTI
                    // 2) SQUALIFICA 3° X 2'
                    // Il primo evento resta una normale esclusione 2' e genera
                    // il countdown; il secondo è solo la squalifica automatica
                    // e NON genera un secondo countdown.
                    PendingEvent.Type = "TWO";
                    PendingEvent.Text = "ESCLUSIONE 2 MINUTI";
                    PendingEvent.SuspensionStartSeconds = State.TimerSeconds;
                    PendingEvent.SuspensionEndSeconds = State.TimerSeconds + 120;
                    PendingEvent.Result = ScoreText();

                    var disqualification = new EventRecord
                    {
                        Time = PendingEvent.Time,
                        Team = team,
                        Number = numberLabel,
                        Type = "DISQUALIFICATION_3X2",
                        Text = "SQUALIFICA 3° X 2'",
                        Result = PendingEvent.Result
                    };

                    // Add subito dopo il TWO: stesso tempo e ordine obbligatorio.
                    var pendingIndex = State.Events.IndexOf(PendingEvent);
                    if (pendingIndex >= 0)
                        State.Events.Insert(pendingIndex + 1, disqualification);
                    else
                        State.Events.Add(disqualification);

                    StartSuspension(team, numberLabel);
                    StopClockForDisciplinary();
                    FinishPendingEvent();
                    return true;
                }
                CommitDisciplinaryPending(team, numberLabel, "TWO", "ESCLUSIONE 2 MINUTI", roster.PlayerNames[index], "2MIN");
                return true;
            }
            case "RED":
                CommitDisciplinaryPending(team, numberLabel, "RED", "ESPULSIONE DIRETTA", roster.PlayerNames[index], "RED");
                return true;
            default: return false;
        }
    }

    public bool SelectStaff(int teamIndex, int index)
    {
        if (PendingEvent is null || State.MatchFinished || State.ShootoutStarted) return false;
        var team = teamIndex == 0 ? "A" : "B";
        var roster = Team(team);
        EnsureRosterArrays(roster);
        if (index < 0 || index >= 5 || string.IsNullOrWhiteSpace(roster.StaffLetters[index])) return false;

        if (PendingEvent.Type is not ("YELLOW" or "TWO" or "RED")) return false;

        PendingStaff = (team, index);
        var letter = roster.StaffLetters[index];
        PendingEvent.Team = team;
        PendingEvent.Number = letter;

        if (PendingEvent.Type == "YELLOW")
        {
            // Il PendingEvent è già in State.Events: non deve contare come
            // ammonizione già assegnata. La panchina può ricevere una sola
            // ammonizione; questa sanzione è separata dal limite di 3 dei giocatori.
            // Regola panchina: per ciascuna squadra (A/B) i dirigenti
            // identificati con A, B, C, D, E possono avere COMPLESSIVAMENTE
            // una sola ammonizione. Quindi, se ad esempio A ha già ricevuto
            // l'ammonizione, una nuova ammonizione a B/C/D/E deve passare
            // dal popup disciplinare.
            var benchAlreadyYellow = State.Events.Any(e =>
                !ReferenceEquals(e, PendingEvent) &&
                e.Team == team && IsStaffIdentifier(e.Number) && e.Type == "YELLOW");
            // La prima ammonizione della panchina conta nel totale 0/3 della squadra.
            // Una seconda ammonizione alla panchina, anche a un dirigente diverso,
            // non può essere assegnata direttamente: deve passare dal popup.
            var teamYellowCount = State.Events.Count(e =>
                !ReferenceEquals(e, PendingEvent) && e.Team == team && e.Type == "YELLOW");
            if (benchAlreadyYellow || teamYellowCount >= 3)
            {
                PendingYellow = (team, letter, true);
                Notify(); return true;
            }
            PendingEvent.Text = "AMMONIZIONE";
            PendingEvent.Result = ScoreText();
            FinishPendingEvent(); return true;
        }
        if (PendingEvent.Type == "TWO")
        {
            var previousTwos = State.Events.Count(e => e.Team == team && e.Number == letter && e.Type == "TWO");
            if (previousTwos >= 1)
            {
                PendingEvent.Type = "RED";
                PendingEvent.Text = "ESPULSIONE DIRETTA";
                PendingEvent.SuspensionStartSeconds = State.TimerSeconds;
                PendingEvent.SuspensionEndSeconds = State.TimerSeconds + 120;
                StopClockForDisciplinary();
                FinishPendingEvent();
                return true;
            }
            CommitDisciplinaryPending(team, letter, "TWO", "ESCLUSIONE 2 MINUTI", roster.StaffNames[index], "2MIN"); return true;
        }
        if (PendingEvent.Type == "RED")
        {
            CommitDisciplinaryPending(team, letter, "RED", "ESPULSIONE DIRETTA", roster.StaffNames[index], "RED"); return true;
        }
        return false;
    }

    // Compatibilità con eventuali chiamate UI precedenti.
    public void CompletePenalty(string team, int index, bool realized)
    {
        if (PendingEvent is null) return;
        if (!TryGetPlayer(team, index, out _, out _, out _)) return;
        PendingEvent.Type = realized ? "PENALTY_GOAL" : "PENALTY_MISS";
        if (realized) IncrementScore(team);
        PendingEvent.Text = realized ? "7m GOAL" : "7m MISS";
        PendingEvent.Result = ScoreText();
        FinishPendingEvent();
    }

    public void ResolveYellow(string action)
    {
        if (PendingYellow is null) return;
        var pending = PendingYellow.Value;
        PendingYellow = null;
        if (action == "CANCEL") { CancelPendingEvent(); return; }
        if (PendingEvent is null) { Notify(); return; }

        PendingEvent.Team = pending.Team; PendingEvent.Number = pending.Number;
        if (action == "TWO")
            CommitDisciplinaryPending(pending.Team, pending.Number, "TWO", "ESCLUSIONE 2 MINUTI", GetSubjectName(pending.Team, pending.Number, pending.IsStaff), "2MIN");
        else if (action == "RED")
            CommitDisciplinaryPending(pending.Team, pending.Number, "RED", "ESPULSIONE DIRETTA", GetSubjectName(pending.Team, pending.Number, pending.IsStaff), "RED");
        else CancelPendingEvent();
    }

    public bool EditEvent(int index, string time, string team, string number, string type)
    {
        // Modifica consentita esclusivamente con il cronometro ufficiale fermo.
        if (!CanEditEvents) return false;
        if (index < 0 || index >= State.Events.Count) return false;

        var ev = State.Events[index];
        if (ev.Type == "SYSTEM" || ev.Type is "SHOOTOUT_GOAL" or "SHOOTOUT_MISS") return false;

        var parsedTime = ParseTime(time.Trim());
        if (parsedTime < 0 || parsedTime > GetCurrentPeriodEndSeconds()) return false;
        if (team is not ("A" or "B")) return false;
        if (type is not ("GOAL" or "PENALTY_GOAL" or "PENALTY_MISS" or "YELLOW" or "TWO" or "RED" or "TIMEOUT")) return false;

        if (type == "TIMEOUT")
        {
            number = "";
        }
        else if (!IsValidEditableIdentifier(team, number))
        {
            return false;
        }

        number = number.Trim();

        // Prima della modifica escludiamo l'evento corrente dai conteggi, così possiamo
        // verificare che la nuova sanzione continui a rispettare le regole della gara.
        if (!IsEditedDisciplineValid(index, parsedTime, team, number, type)) return false;

        // La terza esclusione (3x2') è una proprietà del nuovo evento, non del
        // vecchio evento che stiamo modificando. Questo è importante quando si cambia
        // anche giocatore: una RED/3x2 modificata su un giocatore diverso deve diventare
        // ESPULSIONE DIRETTA se quel giocatore non ha già due esclusioni precedenti.
        var isStaff = IsStaffIdentifier(number);
        var previousTwo = isStaff ? 0 : CountPreviousTwo(index, parsedTime, team, number, includeStaff: false);
        var becomesThreeByTwo = !isStaff && previousTwo >= 2 && (type is "TWO" or "RED");

        // Snapshot = la modifica è annullabile con UNDO, esattamente come un nuovo evento.
        Snapshot();

        ev.Time = FormatTime(parsedTime);
        ev.Team = team;
        ev.Number = number;
        ev.Type = becomesThreeByTwo ? "RED" : type;
        ev.Text = becomesThreeByTwo
            ? "ESCLUSIONE PER 3x2'"
            : EventDescription(type);
        ev.SuspensionStartSeconds = ev.Type is "TWO" or "RED" ? parsedTime : null;
        ev.SuspensionEndSeconds = ev.Type is "TWO" or "RED" ? parsedTime + 120 : null;


        // La modifica può cambiare completamente la natura dell'evento (es. GOAL ->
        // AMMONIZIONE oppure CASA -> OSPITI). Manteniamo il registro in ordine cronologico
        // anche se l'operatore ha corretto l'orario. L'ordine originale viene usato come
        // criterio secondario per gli eventi con lo stesso secondo.
        State.Events = State.Events
            .Select((item, originalIndex) => (item, originalIndex))
            .OrderBy(x => ParseTime(x.item.Time) < 0 ? int.MaxValue : ParseTime(x.item.Time))
            .ThenBy(x => x.originalIndex)
            .Select(x => x.item)
            .ToList();

        // Ricalcolo completo: risultato, parziali già chiusi, reti, 7m, ammonizioni,
        // esclusioni 2', 3x2'/espulsioni e countdown derivati dagli eventi.
        RecalculateRegistry();
        Notify();
        return true;
    }

    private bool IsEditedDisciplineValid(int index, int time, string team, string number, string type)
    {
        if (type is not ("YELLOW" or "TWO" or "RED")) return true;

        var prior = State.Events
            .Select((e, i) => (e, i, t: ParseTime(e.Time)))
            .Where(x => x.i != index && x.e.Team == team && x.t >= 0 && x.t <= time)
            .OrderBy(x => x.t)
            .ThenBy(x => x.i)
            .Select(x => x.e)
            .ToList();

        if (type == "YELLOW")
        {
            // Un giocatore non può ricevere due ammonizioni e la squadra non può
            // superare le tre ammonizioni complessive. Per la panchina resta valida
            // la regola separata di una sola ammonizione.
            var allOther = State.Events.Where((e, i) => i != index && e.Team == team).ToList();
            if (allOther.Count(e => e.Type == "YELLOW") >= 3) return false;
            if (allOther.Any(e => e.Type == "YELLOW" && e.Number == number)) return false;
            if (IsStaffIdentifier(number) && allOther.Any(e => e.Type == "YELLOW" && IsStaffIdentifier(e.Number))) return false;
        }

        if (type == "TWO" && !IsStaffIdentifier(number))
        {
            // La terza esclusione verrà trasformata automaticamente in RED/3x2.
            if (CountPreviousTwo(index, time, team, number, includeStaff: false) > 2) return false;
        }

        return true;
    }

    private int CountPreviousTwo(int index, int time, string team, string number, bool includeStaff)
    {
        return State.Events
            .Select((e, i) => (e, i, t: ParseTime(e.Time)))
            .Count(x => x.i != index && x.e.Team == team && x.e.Number == number &&
                        x.t >= 0 && x.t <= time &&
                        (x.e.Type == "TWO" || (x.e.Type == "RED" && x.e.Text.Contains("3x2", StringComparison.OrdinalIgnoreCase))) &&
                        (includeStaff || !IsStaffIdentifier(x.e.Number)));
    }

    public bool DeleteEvent(int index)
    {
        if (!CanEditEvents) return false;
        if (index < 0 || index >= State.Events.Count) return false;
        if (State.Events[index].Type == "SYSTEM") return false;
        Snapshot();
        State.Events.RemoveAt(index);
        RecalculateRegistry();
        Notify();
        return true;
    }

    private bool IsValidEditableIdentifier(string team, string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;
        var t = Team(team);
        EnsureRosterArrays(t);
        if (IsStaffIdentifier(number))
            return Array.IndexOf(t.StaffLetters, number.Trim()) >= 0;
        return Array.FindIndex(t.NumberLabels, x => string.Equals((x ?? "").Trim(), number.Trim(), StringComparison.Ordinal)) >= 0;
    }

    private void RecalculateRegistry()
    {
        var scoreA = 0;
        var scoreB = 0;

        // Gli eventi vengono ricalcolati per tempo ufficiale, non per l'ordine in cui
        // sono stati inseriti. Questo è fondamentale quando l'operatore modifica l'orario
        // di una rete o di un 7m: il risultato visualizzato su ogni riga deve tornare coerente.
        var ordered = State.Events
            .Select((ev, originalIndex) => (ev, originalIndex))
            .OrderBy(x => ParseTime(x.ev.Time) < 0 ? int.MaxValue : ParseTime(x.ev.Time))
            .ThenBy(x => x.originalIndex)
            .Select(x => x.ev)
            .ToList();

        foreach (var ev in ordered)
        {
            if (ev.Type is "GOAL" or "PENALTY_GOAL")
            {
                if (ev.Team == "A") scoreA++;
                else if (ev.Team == "B") scoreB++;
            }

            ev.Result = $"{scoreA}-{scoreB}";

            if (ev.Type is "TWO" or "RED")
            {
                var t = ParseTime(ev.Time);
                ev.SuspensionStartSeconds = t >= 0 ? t : null;
                ev.SuspensionEndSeconds = t >= 0 ? t + 120 : null;
            }
            else
            {
                ev.SuspensionStartSeconds = null;
                ev.SuspensionEndSeconds = null;
            }
        }

        State.ScoreA = scoreA;
        State.ScoreB = scoreB;

        // Ricostruisce anche i risultati progressivi dei periodi già chiusi. Se una rete
        // viene spostata da un tempo all'altro, il JSON e il referto non conservano il vecchio parziale.
        // Non creiamo però un parziale per un periodo ancora in corso.
        var closedPhases = State.PhaseScores.Keys.ToList();
        State.PhaseScores.Clear();
        foreach (var phase in closedPhases)
        {
            var end = phase <= 2
                ? phase * State.HalfDurationMinutes * 60
                : State.HalfDurationMinutes * 120 + (phase - 2) * 300;
            var phaseA = ordered.Count(e => (e.Type is "GOAL" or "PENALTY_GOAL") && e.Team == "A" && ParseTime(e.Time) >= 0 && ParseTime(e.Time) <= end);
            var phaseB = ordered.Count(e => (e.Type is "GOAL" or "PENALTY_GOAL") && e.Team == "B" && ParseTime(e.Time) >= 0 && ParseTime(e.Time) <= end);
            State.PhaseScores[phase] = $"{phaseA}-{phaseB}";
        }

        // I conteggi di reti, 7m, ammonizioni, 2' ed espulsioni sono derivati direttamente
        // da State.Events (PlayerGoals, PlayerPenaltyGoals, YellowCountFor, PlayerTwoCount,
        // PlayerIsInhibited, ecc.): non vengono duplicati in variabili separate. Dopo questo
        // passaggio risultano quindi automaticamente aggiornati in tutta l'interfaccia.
    }

    public void CancelPendingEvent()
    {
        if (PendingEvent is not null) State.Events.Remove(PendingEvent);
        ClearPending(); _undoState = null; Notify();
    }

    public void Undo()
    {
        if (_undoState is null) return;
        var running = State.Running; StopClock(); State = Clone(_undoState); State.Running = running; _undoState = null; ClearPending(false);
        if (State.Running) StartClock(); Notify();
    }

    public void AdjustTimer(int seconds)
    {
        if (State.ShootoutStarted || State.MatchFinished) return;
        State.TimerSeconds = Math.Clamp(State.TimerSeconds + seconds, GetPeriodStartSeconds(State.Phase), GetCurrentPeriodEndSeconds()); Notify();
    }

    public bool EditTimer(string value)
    {
        if (!CanEditEvents || State.ShootoutStarted || State.MatchFinished) return false;

        var parsed = ParseTime(value.Trim());
        if (parsed < 0) return false;

        var start = GetPeriodStartSeconds(State.Phase);
        var end = GetCurrentPeriodEndSeconds();
        if (parsed < start || parsed > end) return false;

        Snapshot();
        State.TimerSeconds = parsed;
        Notify();
        return true;
    }

    public void FinishCurrentPeriod()
    {
        if (State.Phase is < 1 or > 6 || State.MatchFinished || State.ShootoutStarted) return;
        StopClock();
        State.TimerSeconds = GetCurrentPeriodEndSeconds();
        State.PhaseScores[State.Phase] = ScoreText();
        AddSystemEvent($"FINE {PhaseName(State.Phase)}");
        PeriodStartConfirmed = false;
        ShowPeriodEnd = true;
        Notify();
    }

    public void ClosePeriodDialog()
    {
        // Nel flusso gara il pulsante CHIUDI del popup di fine periodo equivale
        // alla conferma del passaggio successivo. In particolare, al termine del
        // 1° tempo porta SEMPRE al 2° tempo; non può mai chiudere la gara.
        if (ShowPeriodEnd && !State.MatchFinished && !State.ShootoutStarted)
        {
            NextPeriod();
            return;
        }
        ShowPeriodEnd = false;
        Notify();
    }

    // Sequenza gara allineata alle regole del precedente Referto HTML:
    // 1) fine 1° tempo -> SEMPRE 2° tempo;
    // 2) fine 2° tempo STANDARD -> gara terminata (vittoria o pareggio);
    // 3) SUPPLEMENTARI -> solo in caso di parità si prosegue con 1°/2° TS,
    //    poi 3°/4° TS e infine rigori se la parità persiste;
    // 4) al termine del 2° o del 4° TS un vantaggio chiude immediatamente la gara;
    // 5) al termine del 4° TS una parità porta ai rigori.
    public bool NextPeriod()
    {
        if (PeriodStartConfirmed || State.MatchFinished || State.ShootoutStarted) return false;

        ShowPeriodEnd = false;

        switch (State.Phase)
        {
            case 1:
                // Il 1° tempo NON può mai chiudere la gara, qualunque sia il risultato
                // e qualunque modalità sia stata scelta in configurazione.
                StartPeriod(2);
                return true;

            case 2:
                if (State.MatchMode == "STANDARD")
                {
                    FinishMatch(State.ScoreA == State.ScoreB);
                    return true;
                }

                if (State.MatchMode == "RIGORI")
                {
                    if (State.ScoreA != State.ScoreB)
                    {
                        FinishMatch(false);
                    }
                    else
                    {
                        PrepareShootoutStart();
                    }
                    return true;
                }

                // Modalità CON SUPPLEMENTARI: se il 2° tempo è già deciso,
                // la gara termina; solo la parità porta ai supplementari.
                if (State.ScoreA != State.ScoreB)
                {
                    FinishMatch(false);
                    return true;
                }

                StartPeriod(3);
                return true;

            case 3:
                // 1° tempo supplementare: si conclude solo se si arriva alla fine
                // del periodo e poi si passa al 2° TS. Il vantaggio non chiude ancora.
                StartPeriod(4);
                return true;

            case 4:
                // 2° TS: vantaggio = vittoria; parità = 3° TS.
                if (State.ScoreA != State.ScoreB)
                {
                    FinishMatch(false);
                    return true;
                }
                StartPeriod(5);
                return true;

            case 5:
                // 3° TS: si passa sempre al 4° TS; la decisione finale avviene
                // alla fine del 4° TS.
                StartPeriod(6);
                return true;

            case 6:
                // 4° TS: vantaggio = vittoria; parità = tiri di rigore.
                if (State.ScoreA != State.ScoreB)
                {
                    FinishMatch(false);
                    return true;
                }
                PrepareShootoutStart();
                return true;

            default:
                return false;
        }
    }

    private void StartPeriod(int phase)
    {
        if (phase < 1 || phase > 6 || State.MatchFinished || State.ShootoutStarted) return;

        State.Phase = phase;
        State.TimerSeconds = GetPeriodStartSeconds(phase);
        State.MiniTimerSeconds = 0;
        State.MiniTimerRunning = false;
        State.MatchStarted = true;
        // Il passaggio al periodo successivo NON avvia automaticamente il cronometro.
        // Dopo la conferma del popup il nuovo periodo resta fermo a 00:00 (o al
        // relativo minuto iniziale); l'ufficiale deve premere START per farlo partire.
        PeriodStartConfirmed = true;
        StopClock();
        Notify();
    }

    private void PrepareShootoutStart()
    {
        StopClock();
        State.Running = false;
        State.MiniTimerRunning = false;
        PeriodStartConfirmed = true;
        ShowPeriodEnd = false;
        ShowShootoutStart = true;
        Notify();
    }

    private void FinishMatch(bool draw)
    {
        StopClock();
        State.MatchFinished = true;
        State.Running = false;
        State.MiniTimerRunning = false;
        PeriodStartConfirmed = true;
        ShowPeriodEnd = false;
        ShowShootoutStart = false;
        ShowMatchEnd = true;
        var result = ScoreText();
        if (draw)
            AddSystemEvent($"FINE GARA - PAREGGIO {result}");
        else
            AddSystemEvent($"FINE GARA - VINCENTE {(State.ScoreA > State.ScoreB ? State.Casa.Name : State.Ospiti.Name)} {result}");
        Notify();
    }

    public void ConfirmShootoutStart()
    {
        if (State.MatchFinished) return;
        ShowShootoutStart = false;
        ShowShootoutFirstTeam = true;
        PendingShootout = null;
        State.ShootoutStarted = true;
        State.ShootoutFinished = false;
        State.Phase = 7;
        State.TimerSeconds = 0;
        State.Running = false;
        State.ShootoutPhase = 1;
        State.ShootoutTurn = 0;
        State.ShootoutFirstTeam = "";
        State.ShootoutScoreA = 0;
        State.ShootoutScoreB = 0;
        State.ShootoutTakenA ??= [];
        State.ShootoutTakenB ??= [];
        State.ShootoutTakenA.Clear();
        State.ShootoutTakenB.Clear();
        State.Shootout ??= [];
        State.Shootout.Clear();
        StopClock();
        State.MiniTimerSeconds = 0;
        State.MiniTimerRunning = false;
        AddSystemEvent("INIZIO TIRI DI RIGORE");
        Notify();
    }

    public void CancelShootoutStart()
    {
        ShowShootoutStart = false;
        Notify();
    }

    public void ChooseShootoutFirstTeam(string team)
    {
        if (!State.ShootoutStarted || State.ShootoutFinished || !ShowShootoutFirstTeam) return;
        if (team is not ("A" or "B")) return;

        State.ShootoutFirstTeam = team;
        State.ShootoutTurn = 0;
        State.ShootoutPhase = 1;
        ShowShootoutFirstTeam = false;
        AddSystemEvent($"PRIMO TIRO RIGORI · {(team == "A" ? State.Casa.Name : State.Ospiti.Name)}");
        Notify();
    }

    public string ShootoutCurrentTeam =>
        State.ShootoutFirstTeam == "B"
            ? (State.ShootoutTurn % 2 == 0 ? "B" : "A")
            : (State.ShootoutTurn % 2 == 0 ? "A" : "B");

    public int ShootoutAttemptsFor(string team) => (State.Shootout ?? []).Count(x => x.Team == team);

    public int ShootoutGoalsFor(string team) => (State.Shootout ?? []).Count(x => x.Team == team && x.Goal);
    public int ShootoutPlayerAttempts(string team, string number) => (State.Shootout ?? []).Count(x => x.Team == team && x.Number == number);
    public int ShootoutPlayerGoals(string team, string number) => (State.Shootout ?? []).Count(x => x.Team == team && x.Number == number && x.Goal);

    public bool CanShootoutPlayer(string team, int index)
    {
        if (!State.ShootoutStarted || State.ShootoutFinished || ShowShootoutFirstTeam) return false;
        if (team != ShootoutCurrentTeam) return false;
        var roster = Team(team);
        EnsureRosterArrays(roster);
        if (index < 0 || index >= 16 || !roster.ActivePlayers[index]) return false;
        var number = (roster.NumberLabels[index] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(number) || PlayerIsInhibited(team, number)) return false;

        // Regola della serie: dopo i primi 5 tiri, in caso di parità si prosegue
        // ad oltranza. Un giocatore che ha già tirato non può essere scelto di nuovo
        // finché non hanno tirato tutti gli eleggibili della propria squadra.
        // Il conteggio dei già utilizzati NON viene quindi azzerato al passaggio
        // dalla serie iniziale alla morte improvvisa: si azzera solo quando l'intero
        // gruppo degli eleggibili (esclusi gli espulsi/inibiti) ha completato un giro.
        var taken = team == "A" ? State.ShootoutTakenA : State.ShootoutTakenB;
        taken ??= [];

        // La lista dei tiratori usati è separata dalla lista degli eventi: in questo
        // modo il passaggio a un nuovo giro non riabilita accidentalmente il primo
        // tiratore appena selezionato. Il reset avviene solo dopo che tutti gli
        // eleggibili della squadra hanno completato il giro.
        return !taken.Contains(number, StringComparer.Ordinal);
    }

    public bool OpenShootoutShot(string team, int index)
    {
        if (!CanShootoutPlayer(team, index)) return false;
        PendingShootout = (team, index);
        Notify();
        return true;
    }

    public void CancelShootoutShot()
    {
        PendingShootout = null;
        Notify();
    }

    public bool RecordShootoutShot(bool goal)
    {
        if (PendingShootout is null || !State.ShootoutStarted || State.ShootoutFinished) return false;
        var pending = PendingShootout.Value;
        if (!CanShootoutPlayer(pending.Team, pending.Index)) return false;

        var roster = Team(pending.Team);
        EnsureRosterArrays(roster);
        var number = (roster.NumberLabels[pending.Index] ?? string.Empty).Trim();
        var name = roster.PlayerNames[pending.Index] ?? string.Empty;
        var round = State.ShootoutTurn + 1;

        State.ShootoutTakenA ??= [];
        State.ShootoutTakenB ??= [];
        var taken = pending.Team == "A" ? State.ShootoutTakenA : State.ShootoutTakenB;
        if (taken.Contains(number, StringComparer.Ordinal)) return false;

        Snapshot();
        State.Shootout ??= [];
        State.Shootout.Add(new ShootoutAttempt
        {
            Id = State.Shootout.Count + 1,
            Phase = State.ShootoutPhase,
            Round = round,
            Team = pending.Team,
            Number = number,
            PlayerName = name,
            Goal = goal,
            ScoreA = State.ShootoutScoreA + (goal && pending.Team == "A" ? 1 : 0),
            ScoreB = State.ShootoutScoreB + (goal && pending.Team == "B" ? 1 : 0)
        });

        taken.Add(number);

        if (goal)
        {
            if (pending.Team == "A") { State.ShootoutScoreA++; State.ScoreA++; }
            else { State.ShootoutScoreB++; State.ScoreB++; }
        }

        // I rigori finali sono eventi separati dai 7m di gioco:
        // non entrano nel conteggio dei gol personali del giocatore.
        State.Events.Add(new EventRecord
        {
            Time = FormatTime(State.TimerSeconds),
            Team = pending.Team,
            Number = number,
            Type = goal ? "SHOOTOUT_GOAL" : "SHOOTOUT_MISS",
            Text = goal ? "RIGORE REALIZZATO" : "RIGORE ERRATO",
            Result = ScoreText()
        });

        PendingShootout = null;
        State.ShootoutTurn++;

        // Se tutti gli eleggibili hanno tirato, si prepara il giro successivo.
        // Questa regola vale anche in morte improvvisa e mantiene esclusi gli
        // espulsi/inibiti.
        var eligibleAfter = EligibleShooters(pending.Team).Select(x => x.Number).ToHashSet(StringComparer.Ordinal);
        if (eligibleAfter.Count > 0 && eligibleAfter.All(taken.Contains))
            taken.Clear();

        if (ShouldFinishShootout())
        {
            FinishShootout();
            return true;
        }

        if (State.ShootoutPhase == 1 && State.ShootoutTurn >= 10)
        {
            State.ShootoutPhase = 2;
            AddSystemEvent("INIZIO MORTE IMPROVVISA RIGORI");
        }

        Notify();
        return true;
    }

    private IEnumerable<(int Index, string Number)> EligibleShooters(string team)
    {
        var roster = Team(team);
        EnsureRosterArrays(roster);
        for (var i = 0; i < 16; i++)
        {
            if (!roster.ActivePlayers[i]) continue;
            var number = (roster.NumberLabels[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(number) || PlayerIsInhibited(team, number)) continue;
            yield return (i, number);
        }
    }

    private bool ShouldFinishShootout()
    {
        var a = (State.Shootout ?? []).Count(x => x.Team == "A");
        var b = (State.Shootout ?? []).Count(x => x.Team == "B");
        if (a != b) return false;

        var ga = State.ShootoutScoreA;
        var gb = State.ShootoutScoreB;

        // Nei primi 5 tiri per squadra si può chiudere anche prima se il risultato
        // è matematicamente irrecuperabile. Dopo 5-5, se il punteggio è pari,
        // si entra nella morte improvvisa e si confronta il risultato solo dopo
        // che entrambe le squadre hanno effettuato il medesimo numero di tiri.
        if (State.ShootoutPhase == 1)
        {
            if (a < 5)
            {
                var remaining = 5 - a;
                return ga > gb + remaining || gb > ga + remaining;
            }

            return ga != gb;
        }

        // Morte improvvisa: a parità di tiri, un vantaggio di una rete chiude
        // immediatamente la serie e quindi la gara.
        return ga != gb;
    }

    private void FinishShootout()
    {
        if (State.ShootoutFinished) return;
        State.ShootoutFinished = true;
        State.Shootout ??= [];
        State.MatchFinished = true;
        State.Running = false;
        ShowShootoutFirstTeam = false;
        PendingShootout = null;
        var winner = ShootoutWinnerName;
        AddSystemEvent($"FINE SERIE RIGORI {State.ShootoutScoreA:00}-{State.ShootoutScoreB:00} ({winner})");
        ShowShootoutWinner = true;
        Notify();
    }

    public void OpenShootoutWinner()
    {
        if (!State.ShootoutFinished) return;
        ShowShootoutWinner = true;
        Notify();
    }

    public void CloseShootoutWinner()
    {
        ShowShootoutWinner = false;
        Notify();
    }

    public void CloseMatchEnd()
    {
        ShowMatchEnd = false;
        Notify();
    }

    public void BackToMatchAfterShootout()
    {
        // La gara è definitivamente chiusa: si torna alla schermata principale
        // solo per consultare il referto, senza riattivare il cronometro.
        ShowShootoutWinner = false;
        State.ShootoutStarted = false;
        Notify();
    }

    public void StartStop()
    {
        // Durante un Team Time-Out il cronometro gara resta fermo fino alla fine del minuto.
        if (State.TimeoutRemainingSeconds > 0) return;
        if (State.MatchFinished || State.ShootoutStarted || !PeriodStartConfirmed) return;
        if (!State.MatchStarted && !ValidateRoster(out var error)) { ValidationError = error; ShowConfiguration = true; Notify(); return; }
        ValidationError = null; ShowConfiguration = false; State.MatchStarted = true; State.Running = !State.Running;
        // Il piccolo cronometro segue esattamente START/STOP del cronometro principale.
        State.MiniTimerRunning = State.Running;
        if (State.Running) StartClock(); else StopClock(); Notify();
    }

    public int GetCurrentPeriodDurationSeconds() => State.Phase <= 2 ? State.HalfDurationMinutes * 60 : 300;
    public int GetCurrentPeriodEndSeconds() => State.Phase <= 2 ? State.Phase * State.HalfDurationMinutes * 60 : State.HalfDurationMinutes * 120 + (State.Phase - 2) * 300;
    public int GetPeriodStartSeconds(int phase) => phase <= 1 ? 0 : phase == 2 ? State.HalfDurationMinutes * 60 : State.HalfDurationMinutes * 120 + (phase - 3) * 300;
    public int GetMiniTimerSeconds() => State.MiniTimerSeconds;

    public IEnumerable<(string Number, string Remaining)> GetSuspensions(string team, string? number = null)
    {
        // Il termine della sospensione è memorizzato sul singolo evento come tempo
        // assoluto della gara. In questo modo il countdown NON viene perso quando
        // il popup di fine periodo porta al periodo successivo.
        foreach (var ev in State.Events.Where(e => e.Team == team && (e.Type == "TWO" || e.Type == "RED"))
                     .Where(e => number is null || e.Number == number)
                     .Where(e => e.SuspensionEndSeconds.HasValue || e.SuspensionStartSeconds.HasValue))
        {
            var end = ev.SuspensionEndSeconds ?? (ev.SuspensionStartSeconds!.Value + 120);
            var left = end - State.TimerSeconds;
            if (left > 0 && left <= 120) yield return (ev.Number, FormatTime(left));
        }
    }

    // Dettaglio grafico dei countdown giocatore: distingue i 2' ordinari
    // (arancione) dalle espulsioni/3x2' (rosso). Il calcolo usa il termine
    // assoluto della sospensione, quindi attraversa correttamente i periodi.
    public IEnumerable<(string Remaining, bool IsRed)> GetPlayerSuspensionBadges(string team, string number)
    {
        foreach (var ev in State.Events.Where(e => e.Team == team && e.Number == number && (e.Type == "TWO" || e.Type == "RED"))
                     .Where(e => e.SuspensionEndSeconds.HasValue || e.SuspensionStartSeconds.HasValue))
        {
            var end = ev.SuspensionEndSeconds ?? (ev.SuspensionStartSeconds!.Value + 120);
            var left = end - State.TimerSeconds;
            if (left > 0 && left <= 120) yield return (FormatTime(left), ev.Type == "RED");
        }
    }

    public int PlayerYellowCount(string team) => State.Events.Count(e => !ReferenceEquals(e, PendingEvent) && e.Team == team && e.Type == "YELLOW" && IsPlayerIdentifier(e.Number));
    public int StaffYellowCount(string team) => State.Events.Count(e => !ReferenceEquals(e, PendingEvent) && e.Team == team && e.Type == "YELLOW" && IsStaffIdentifier(e.Number));
    // Il contatore 0/3 comprende giocatori e dirigenti.
    // La panchina ha inoltre la regola separata della sola prima ammonizione.
    public int YellowCountFor(string team) => PlayerYellowCount(team) + StaffYellowCount(team);

    public int TimeoutCount(string team) => State.Events.Count(e =>
        e.Team == team && (e.Type == "TIMEOUT" || e.Type == "T.O." || e.Type == "TO"));

    public bool RegisterTimeout(string team)
    {
        if (State.MatchFinished || State.ShootoutStarted || !PeriodStartConfirmed) return false;
        if (team != "A" && team != "B") return false;
        if (TimeoutCount(team) >= 3 || PendingEvent is not null) return false;
        if (State.TimeoutRemainingSeconds > 0) return false;
        Snapshot();
        State.Events.Add(new EventRecord
        {
            Time = FormatTime(State.TimerSeconds), Team = team, Number = "",
            Type = "TIMEOUT", Text = "TIME OUT", Result = ScoreText()
        });
        State.TimeoutTeam = team;
        State.TimeoutRemainingSeconds = 60;
        StopClock();
        // Il cronometro gara resta fermo, ma il task del countdown del time-out deve continuare a correre.
        StartClock();
        Notify();
        return true;
    }

    public void ExitTimeout()
    {
        if (State.TimeoutRemainingSeconds <= 0) return;

        // Termina anticipatamente solo il conteggio del time-out.
        // Il cronometro ufficiale della gara resta fermo: sarà l'operatore
        // a premere START quando vuole riprendere il gioco.
        State.TimeoutRemainingSeconds = 0;
        State.TimeoutTeam = "";
        StopClock();
        Notify();
    }

    public int PenaltyAttempts(string team) => State.Events.Count(e =>
        e.Team == team && (e.Type == "PENALTY_GOAL" || e.Type == "PENALTY_MISS"));

    public int PenaltyRealized(string team) => State.Events.Count(e =>
        e.Team == team && e.Type == "PENALTY_GOAL");

    public int PlayerTwoCount(string team, string number) => State.Events.Count(e =>
        e.Team == team && e.Number == number &&
        (e.Type == "TWO" || (e.Type == "RED" && e.Text.Contains("3x2", StringComparison.OrdinalIgnoreCase))));

    // Il giocatore viene inibito SOLO quando raggiunge la terza esclusione (3x2)
    // oppure riceve una espulsione diretta. Le prime due esclusioni 2'
    // non devono mai disabilitare la card.
    public bool PlayerIsInhibited(string team, string number) =>
        PlayerTwoCount(team, number) >= 3 ||
        State.Events.Any(e => e.Team == team && e.Number == number &&
            e.Type == "RED" && !e.Text.Contains("3x2", StringComparison.OrdinalIgnoreCase));

    public int PlayerGoals(string team, string number) => State.Events.Count(e => e.Team == team && e.Number == number && e.Type == "GOAL");
    public int PlayerPenaltyGoals(string team, string number) => State.Events.Count(e => e.Team == team && e.Number == number && e.Type == "PENALTY_GOAL");
    public bool PlayerHasYellow(string team, string number) => State.Events.Any(e => e.Team == team && e.Number == number && e.Type == "YELLOW");
    public bool PlayerHasRed(string team, string number) => State.Events.Any(e => e.Team == team && e.Number == number && e.Type == "RED");
    public bool StaffHasYellow(string team, string letter) => State.Events.Any(e => e.Team == team && e.Number == letter && e.Type == "YELLOW");
    public bool StaffHasRed(string team, string letter) => State.Events.Any(e => e.Team == team && e.Number == letter && e.Type == "RED");
    public IEnumerable<string> GetStaffSuspensions(string team, string letter) => GetSuspensions(team, letter).Select(x => x.Remaining);
    public string GetSubjectName(string team, string identifier, bool isStaff)
    {
        var t = Team(team);
        if (isStaff) { var i = Array.IndexOf(t.StaffLetters, identifier); return i >= 0 ? t.StaffNames[i] ?? "" : ""; }
        var byLabel = Array.FindIndex(t.NumberLabels ?? Array.Empty<string>(), x => string.Equals((x ?? "").Trim(), identifier, StringComparison.Ordinal));
        if (byLabel >= 0) return t.PlayerNames[byLabel] ?? "";
        if (int.TryParse(identifier, out var n)) { var i = Array.IndexOf(t.Numbers, n); return i >= 0 ? t.PlayerNames[i] ?? "" : ""; }
        return "";
    }

    public bool ValidateRoster(out string error)
    {
        foreach (var (teamCode, team) in new[] { ("CASA", State.Casa), ("OSPITI", State.Ospiti) })
        {
            EnsureRosterArrays(team);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 16; i++)
            {
                var label = (team.NumberLabels[i] ?? string.Empty).Trim();
                if (!team.ActivePlayers[i] && string.IsNullOrWhiteSpace(label)) continue;
                if (string.IsNullOrWhiteSpace(label) || label.Length > 2 || !label.All(char.IsDigit) || !int.TryParse(label, out var n) || n < 0 || n > 99)
                { error = $"Numero di maglia non valido per {teamCode}, posizione {i + 1}."; return false; }
                if (!seen.Add(label)) { error = $"Numeri di maglia duplicati per {teamCode}: {label}."; return false; }
            }
            for (var i = 0; i < 5; i++)
            {
                var letter = (team.StaffLetters[i] ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(letter)) continue;
                if (letter.Length != 1 || letter is not ("A" or "B" or "C" or "D" or "E"))
                { error = $"Identificativo dirigente non valido per {teamCode}, posizione {i + 1}."; return false; }
                if (!seen.Add("STAFF:" + letter)) { error = $"Identificativi dirigenti duplicati per {teamCode}: {letter}."; return false; }
            }
        }
        error = ""; return true;
    }

    public string ExportJson() => JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
    public void ImportJson(string json) { StopClock(); State = JsonSerializer.Deserialize<GameState>(json) ?? throw new InvalidOperationException("JSON partita non valido."); EnsureRosterArrays(State.Casa); EnsureRosterArrays(State.Ospiti); State.Shootout ??= []; State.ShootoutTakenA ??= []; State.ShootoutTakenB ??= []; State.MiniTimerRunning = false; ClearPending(); PendingShootout = null; ShowShootoutStart = false; ShowShootoutFirstTeam = false; ShowShootoutWinner = false; ShowMatchEnd = false; _undoState = null; PeriodStartConfirmed = !State.MatchFinished && !State.ShootoutStarted; Notify(); }
    public string ScoreText() => $"{State.ScoreA}-{State.ScoreB}";
    public static string FormatTime(int seconds) => $"{Math.Max(0, seconds) / 60:00}:{Math.Max(0, seconds) % 60:00}";
    public static string PhaseName(int phase) => phase switch { 1 => "1° TEMPO", 2 => "2° TEMPO", 3 => "1° TEMPO SUPPLEMENTARE", 4 => "2° TEMPO SUPPLEMENTARE", 5 => "3° TEMPO SUPPLEMENTARE", 6 => "4° TEMPO SUPPLEMENTARE", _ => "RIGORI" };
    public static string EventDescription(string type) => type switch
    {
        "GOAL" => "RETE",
        "PENALTY_GOAL" => "7m GOAL",
        "PENALTY_MISS" => "7m MISS",
        "PENALTY" => "TIRO DI 7 METRI",
        "YELLOW" => "AMMONIZIONE",
        "TWO" => "ESCLUSIONE 2 MINUTI",
        "DISQUALIFICATION_3X2" => "SQUALIFICA 3° X 2'",
        "RED" => "ESPULSIONE DIRETTA",
        _ => type
    };

    private void CommitDisciplinaryPending(string team, string identifier, string type, string text, string? name, string ticketType)
    {
        if (PendingEvent is null) return;
        PendingEvent.Team = team; PendingEvent.Number = identifier; PendingEvent.Type = type; PendingEvent.Text = text; PendingEvent.Result = ScoreText();
        if (type is "TWO" or "RED")
        {
            PendingEvent.SuspensionStartSeconds = State.TimerSeconds;
            PendingEvent.SuspensionEndSeconds = State.TimerSeconds + 120;
        }
        StartSuspension(team, identifier); StopClockForDisciplinary();
        // La stampa del cartellino verrà collegata al motore di stampa web nella fase UI.
        FinishPendingEvent();
    }

    private void StartSuspension(string team, string identifier) { /* Il countdown viene calcolato da SuspensionStartSeconds e dal cronometro gara. */ }
    private void StopClockForDisciplinary() { if (State.Running) StopClock(); }
    private bool StaffBenchAlreadyYellow(string team) => State.Events.Any(e => !ReferenceEquals(e, PendingEvent) && e.Team == team && IsStaffIdentifier(e.Number) && e.Type == "YELLOW");
    private bool PlayerAlreadyYellow(string team, string number) => State.Events.Any(e => !ReferenceEquals(e, PendingEvent) && e.Team == team && e.Number == number && e.Type == "YELLOW");
    private bool IsStaffIdentifier(string value) => value is "A" or "B" or "C" or "D" or "E";
    private bool IsPlayerIdentifier(string value) => int.TryParse(value, out _);
    private bool TryGetPlayer(string team, int index, out TeamState roster, out int number, out string numberLabel)
    {
        roster = Team(team); EnsureRosterArrays(roster); number = 0; numberLabel = "";
        if (index < 0 || index >= 16 || !roster.ActivePlayers[index]) return false;
        number = roster.Numbers[index];
        numberLabel = (roster.NumberLabels[index] ?? "").Trim();
        if (numberLabel is not ("0" or "00") && (!int.TryParse(numberLabel, out var parsed) || parsed < 1 || parsed > 99)) return false;
        return true;
    }
    private TeamState Team(string team) => team == "A" ? State.Casa : State.Ospiti;
    private int ParseTime(string value) { var p = value.Split(':'); return p.Length == 2 && int.TryParse(p[0], out var m) && int.TryParse(p[1], out var s) ? m * 60 + s : -1; }
    private int IncrementScore(string team) => team == "A" ? ++State.ScoreA : ++State.ScoreB;
    private void AddSystemEvent(string text) => State.Events.Add(new EventRecord { Time = FormatTime(State.TimerSeconds), Type = "SYSTEM", Text = text, Result = ScoreText() });
    private void Snapshot() => _undoState = Clone(State);
    private void FinishPendingEvent() { PendingEvent = null; PendingPenalty = null; PendingYellow = null; PendingStaff = null; Notify(); }
    private void ClearPending(bool notify = true) { PendingEvent = null; PendingPenalty = null; PendingYellow = null; PendingStaff = null; if (notify) Notify(); }
    private static GameState Clone(GameState s) => JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(s)) ?? new();
    private static GameState CreateNewGame() => new() { Phase = 1, HalfDurationMinutes = 30, MatchMode = "STANDARD", Casa = new TeamState("CASA"), Ospiti = new TeamState("OSPITI") };
    private static void EnsureRosterArrays(TeamState t)
    {
        t.Numbers ??= Enumerable.Range(1, 16).ToArray(); if (t.Numbers.Length != 16) t.Numbers = Enumerable.Range(1, 16).ToArray();
        t.NumberLabels ??= t.Numbers.Select(n => n.ToString()).ToArray(); if (t.NumberLabels.Length != 16) t.NumberLabels = Resize(t.NumberLabels, 16);
        t.PlayerNames ??= new string[16]; if (t.PlayerNames.Length != 16) t.PlayerNames = Resize(t.PlayerNames, 16);
        t.ActivePlayers ??= Enumerable.Repeat(true, 16).ToArray(); if (t.ActivePlayers.Length != 16) t.ActivePlayers = Resize(t.ActivePlayers, 16, true);
        // La presenza in distinta dipende esclusivamente dal numero: vuoto = assente.
        for (var i = 0; i < 16; i++)
            t.ActivePlayers[i] = !string.IsNullOrWhiteSpace((t.NumberLabels[i] ?? string.Empty).Trim());
        t.StaffLetters ??= ["A", "B", "C", "D", "E"]; if (t.StaffLetters.Length != 5) t.StaffLetters = Resize(t.StaffLetters, 5);
        t.StaffNames ??= new string[5]; if (t.StaffNames.Length != 5) t.StaffNames = Resize(t.StaffNames, 5);
        t.ActiveStaff ??= Enumerable.Repeat(true, 5).ToArray(); if (t.ActiveStaff.Length != 5) t.ActiveStaff = Resize(t.ActiveStaff, 5, true);
        // Anche per i dirigenti l'identificativo vuoto significa posizione non presente.
        for (var i = 0; i < 5; i++)
            t.ActiveStaff[i] = !string.IsNullOrWhiteSpace((t.StaffLetters[i] ?? string.Empty).Trim());
    }
    private static T[] Resize<T>(T[] source, int size, T? fill = default) { var r = new T[size]; Array.Copy(source, r, Math.Min(source.Length, size)); if (fill is not null && source.Length < size) for (var i = source.Length; i < size; i++) r[i] = fill; return r; }

    private void StartClock()
    {
        if (_clockTask is { IsCompleted: false }) return;
        _clockCts?.Cancel(); _clockCts = new CancellationTokenSource(); _clockTask = RunClockAsync(_clockCts.Token);
    }
    private async Task RunClockAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                var changed = false;

                if (State.TimeoutRemainingSeconds > 0)
                {
                    State.TimeoutRemainingSeconds--;
                    changed = true;
                    if (State.TimeoutRemainingSeconds == 0)
                    {
                        State.TimeoutTeam = "";
                    }
                }

                if (State.Running)
                {
                    State.TimerSeconds++;

                    // Il piccolo cronometro è sincronizzato allo START/STOP del principale,
                    // ma misura solo il tempo trascorso nel periodo corrente.
                    State.MiniTimerRunning = true;
                    State.MiniTimerSeconds++;

                    changed = true;
                    if (State.TimerSeconds >= GetCurrentPeriodEndSeconds())
                    {
                        State.TimerSeconds = GetCurrentPeriodEndSeconds();
                        State.Running = false;
                        State.MiniTimerRunning = false;
                        PeriodStartConfirmed = false;
                        State.PhaseScores[State.Phase] = ScoreText();
                        AddSystemEvent($"FINE {PhaseName(State.Phase)}");
                        ShowPeriodEnd = true;
                        Notify();
                        break;
                    }
                }

                if (changed) Notify();
                if (!State.Running && State.TimeoutRemainingSeconds <= 0) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void StopClock() { State.Running = false; State.MiniTimerRunning = false; _clockCts?.Cancel(); _clockCts = null; _clockTask = null; }
    public ValueTask DisposeAsync() { StopClock(); return ValueTask.CompletedTask; }

    private void Notify() => Changed?.Invoke();
}

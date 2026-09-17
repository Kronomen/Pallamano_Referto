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
    public bool ShowConfiguration { get; private set; } = true;
    public bool PeriodStartConfirmed { get; private set; } = true;
    public string? ValidationError { get; private set; }
    public bool CanUndo => _undoState is not null;
    public bool CanEditEvents => !State.Running && State.TimeoutRemainingSeconds <= 0 && PendingEvent is null && !State.MatchFinished && !State.ShootoutStarted;
    public event Action? Changed;

    public void OpenConfiguration()
    {
        if (!State.MatchStarted) { ValidationError = null; ShowConfiguration = true; Notify(); }
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
        StopClock(); State = CreateNewGame(); ClearPending(); _undoState = null;
        PeriodStartConfirmed = true; ShowPeriodEnd = false; ShowShootoutStart = false; ShowConfiguration = true; ValidationError = null; Notify();
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
                    // 3° 2' dello stesso giocatore: UNA SOLA RIGA nel Registro Gara.
                    // L'evento corrente viene trasformato nella sanzione definitiva,
                    // senza aggiungere un secondo evento separato.
                    PendingEvent.Type = "RED";
                    PendingEvent.Text = "ESCLUSIONE PER 3x2'";
                    PendingEvent.SuspensionStartSeconds = State.TimerSeconds;
                    PendingEvent.Result = ScoreText();
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
        if (!CanEditEvents) return false;
        if (index < 0 || index >= State.Events.Count) return false;
        var ev = State.Events[index];
        if (ev.Type == "SYSTEM") return false;

        var parsedTime = ParseTime(time.Trim());
        if (parsedTime < 0 || parsedTime > GetCurrentPeriodEndSeconds()) return false;
        if (team is not ("A" or "B")) return false;
        if (type is not ("GOAL" or "PENALTY_GOAL" or "PENALTY_MISS" or "YELLOW" or "TWO" or "RED" or "TIMEOUT")) return false;

        if (type == "TIMEOUT")
        {
            number = "";
        }
        else
        {
            if (!IsValidEditableIdentifier(team, number)) return false;
        }

        Snapshot();
        ev.Time = FormatTime(parsedTime);
        ev.Team = team;
        ev.Number = number.Trim();
        ev.Type = type;
        ev.Text = type == "RED" && ev.Text.Contains("3x2", StringComparison.OrdinalIgnoreCase)
            ? "ESCLUSIONE PER 3x2'"
            : EventDescription(type);
        ev.SuspensionStartSeconds = type is "TWO" or "RED" ? parsedTime : null;
        RecalculateRegistry();
        Notify();
        return true;
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
        foreach (var ev in State.Events)
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
            }
            else
            {
                ev.SuspensionStartSeconds = null;
            }
        }
        State.ScoreA = scoreA;
        State.ScoreB = scoreB;
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

    public void FinishCurrentPeriod()
    {
        if (State.Phase is < 1 or > 6 || State.MatchFinished) return;
        StopClock(); State.TimerSeconds = GetCurrentPeriodEndSeconds();
        State.PhaseScores[State.Phase] = ScoreText();
        AddSystemEvent($"FINE {PhaseName(State.Phase)}"); PeriodStartConfirmed = false; ShowPeriodEnd = true; Notify();
    }

    public void ClosePeriodDialog() { ShowPeriodEnd = false; Notify(); }

    public bool NextPeriod()
    {
        if (PeriodStartConfirmed || State.MatchFinished) return false;
        ShowPeriodEnd = false;
        if (State.Phase == 2 && State.ScoreA != State.ScoreB) { State.MatchFinished = true; Notify(); return true; }
        if (State.Phase == 6)
        {
            if (State.ScoreA == State.ScoreB) ShowShootoutStart = true;
            else State.MatchFinished = true;
            Notify(); return true;
        }
        State.Phase++; State.TimerSeconds = GetPeriodStartSeconds(State.Phase); State.Running = false; PeriodStartConfirmed = true; Notify(); return true;
    }

    public void ConfirmShootoutStart()
    {
        ShowShootoutStart = false; State.ShootoutStarted = true; State.ShootoutPhase = 1; State.ShootoutTurn = 0; State.ShootoutFirstTeam = "A"; StopClock(); AddSystemEvent("INIZIO TIRI DI RIGORE"); Notify();
    }
    public void CancelShootoutStart() { ShowShootoutStart = false; Notify(); }

    public void StartStop()
    {
        // Durante un Team Time-Out il cronometro gara resta fermo fino alla fine del minuto.
        if (State.TimeoutRemainingSeconds > 0) return;
        if (State.MatchFinished || State.ShootoutStarted || !PeriodStartConfirmed) return;
        if (!State.MatchStarted && !ValidateRoster(out var error)) { ValidationError = error; ShowConfiguration = true; Notify(); return; }
        ValidationError = null; ShowConfiguration = false; State.MatchStarted = true; State.Running = !State.Running;
        if (State.Running) StartClock(); else StopClock(); Notify();
    }

    public int GetCurrentPeriodDurationSeconds() => State.Phase <= 2 ? State.HalfDurationMinutes * 60 : 300;
    public int GetCurrentPeriodEndSeconds() => State.Phase <= 2 ? State.Phase * State.HalfDurationMinutes * 60 : State.HalfDurationMinutes * 120 + (State.Phase - 2) * 300;
    public int GetPeriodStartSeconds(int phase) => phase <= 1 ? 0 : phase == 2 ? State.HalfDurationMinutes * 60 : State.HalfDurationMinutes * 120 + (phase - 3) * 300;
    public int GetMiniTimerSeconds() => State.CronometroContinuativo ? State.TimerSeconds : Math.Max(0, State.TimerSeconds - GetPeriodStartSeconds(State.Phase));

    public IEnumerable<(string Number, string Remaining)> GetSuspensions(string team, string? number = null)
    {
        // Countdown personale: vale per ogni 2' ordinario, per l'espulsione diretta
        // e per la 3a esclusione (3x2'). Segue il cronometro ufficiale della gara.
        foreach (var ev in State.Events.Where(e => e.Team == team && (e.Type == "TWO" || e.Type == "RED"))
                     .Where(e => number is null || e.Number == number)
                     .Where(e => e.SuspensionStartSeconds.HasValue))
        {
            var start = ev.SuspensionStartSeconds!.Value;
            var elapsed = Math.Max(0, State.TimerSeconds - start);
            var left = 120 - elapsed;
            if (left > 0) yield return (ev.Number, FormatTime(left));
        }
    }

    // Dettaglio grafico dei countdown giocatore: distingue i 2' ordinari
    // (arancione) dalle espulsioni/3x2' (rosso).
    public IEnumerable<(string Remaining, bool IsRed)> GetPlayerSuspensionBadges(string team, string number)
    {
        foreach (var ev in State.Events.Where(e => e.Team == team && e.Number == number && (e.Type == "TWO" || e.Type == "RED"))
                     .Where(e => e.SuspensionStartSeconds.HasValue))
        {
            var start = ev.SuspensionStartSeconds!.Value;
            var elapsed = Math.Max(0, State.TimerSeconds - start);
            var left = 120 - elapsed;
            if (left > 0) yield return (FormatTime(left), ev.Type == "RED");
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
                if (string.IsNullOrWhiteSpace(team.StaffLetters[i])) { error = $"Identificativo dirigente mancante per {teamCode}, posizione {i + 1}."; return false; }
            }
        }
        error = ""; return true;
    }

    public string ExportJson() => JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
    public void ImportJson(string json) { StopClock(); State = JsonSerializer.Deserialize<GameState>(json) ?? throw new InvalidOperationException("JSON partita non valido."); EnsureRosterArrays(State.Casa); EnsureRosterArrays(State.Ospiti); ClearPending(); _undoState = null; PeriodStartConfirmed = !State.MatchFinished; ShowConfiguration = !State.MatchStarted; Notify(); }
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
        "RED" => "ESPULSIONE DIRETTA",
        _ => type
    };

    private void CommitDisciplinaryPending(string team, string identifier, string type, string text, string? name, string ticketType)
    {
        if (PendingEvent is null) return;
        PendingEvent.Team = team; PendingEvent.Number = identifier; PendingEvent.Type = type; PendingEvent.Text = text; PendingEvent.Result = ScoreText();
        if (type is "TWO" or "RED") PendingEvent.SuspensionStartSeconds = State.TimerSeconds;
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
        for (var i = 0; i < 16; i++) if (string.IsNullOrWhiteSpace(t.NumberLabels[i]) && t.Numbers[i] >= 0) t.NumberLabels[i] = t.Numbers[i].ToString();
        t.PlayerNames ??= new string[16]; if (t.PlayerNames.Length != 16) t.PlayerNames = Resize(t.PlayerNames, 16);
        t.ActivePlayers ??= Enumerable.Repeat(true, 16).ToArray(); if (t.ActivePlayers.Length != 16) t.ActivePlayers = Resize(t.ActivePlayers, 16, true);
        t.StaffLetters ??= ["A", "B", "C", "D", "E"]; if (t.StaffLetters.Length != 5) t.StaffLetters = ["A", "B", "C", "D", "E"];
        t.StaffNames ??= new string[5]; if (t.StaffNames.Length != 5) t.StaffNames = Resize(t.StaffNames, 5);
        t.ActiveStaff ??= Enumerable.Repeat(true, 5).ToArray(); if (t.ActiveStaff.Length != 5) t.ActiveStaff = Resize(t.ActiveStaff, 5, true);
        for (var i = 0; i < 5; i++) if (string.IsNullOrWhiteSpace(t.StaffLetters[i])) t.StaffLetters[i] = ((char)('A' + i)).ToString();
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
                    changed = true;
                    if (State.TimerSeconds >= GetCurrentPeriodEndSeconds()) { State.TimerSeconds = GetCurrentPeriodEndSeconds(); State.Running = false; PeriodStartConfirmed = false; State.PhaseScores[State.Phase] = ScoreText(); AddSystemEvent($"FINE {PhaseName(State.Phase)}"); ShowPeriodEnd = true; Notify(); break; }
                }

                if (changed) Notify();
                if (!State.Running && State.TimeoutRemainingSeconds <= 0) break;
            }
        }
        catch (OperationCanceledException) { }
    }
    private void StopClock() { State.Running = false; _clockCts?.Cancel(); _clockCts = null; _clockTask = null; }
    public ValueTask DisposeAsync() { StopClock(); return ValueTask.CompletedTask; }

    private void Notify() => Changed?.Invoke();
}

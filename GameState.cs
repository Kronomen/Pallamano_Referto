namespace RefertoPallamano_Blazor.Models;

public sealed class GameState
{
    public int Phase { get; set; } = 1;
    public int TimerSeconds { get; set; }
    public bool Running { get; set; }

    // Cronometro di supporto indipendente dal cronometro ufficiale.
    // Viene azzerato all'inizio di ogni periodo e gestito esclusivamente da START/STOP.
    public int MiniTimerSeconds { get; set; }
    public bool MiniTimerRunning { get; set; }
    public bool MatchStarted { get; set; }
    // True quando la gara è terminata e il JSON deve essere salvato all'uscita dal referto.
    public bool MatchFinished { get; set; }
    public int HalfDurationMinutes { get; set; } = 30;
    public string MatchMode { get; set; } = "STANDARD";
    public bool CronometroContinuativo { get; set; }
    public bool RichiediCartellinoEsclusione { get; set; }
    // Countdown separato del Team Time-Out (1 minuto). Il cronometro gara resta fermo.
    public int TimeoutRemainingSeconds { get; set; }
    public string TimeoutTeam { get; set; } = "";
    // True quando la distinta dirigenti è stata importata dal modello Excel.
    // Serve a distinguere le posizioni volutamente mancanti (lettera assente)
    // dalle nuove gare, che devono partire con A-B-C-D-E.
    public bool StaffRosterImported { get; set; }

    // Dati anagrafici della gara, importati dal modello Excel e salvati nel JSON.
    public string MatchNumber { get; set; } = "";
    public string MatchDate { get; set; } = "";
    public string MatchTime { get; set; } = "";
    public string Championship { get; set; } = "";
    public string City { get; set; } = "";
    public string Venue { get; set; } = "";
    public string Season { get; set; } = "";
    public string Referee1 { get; set; } = "";
    public string Referee2 { get; set; } = "";
    public string Delegate1 { get; set; } = "";
    public string Delegate2 { get; set; } = "";
    public string Timekeeper { get; set; } = "";
    public string Secretary { get; set; } = "";

    public int ScoreA { get; set; }
    public int ScoreB { get; set; }

    // Risultati progressivi registrati alla fine di ciascun periodo:
    // 1-2 = tempi regolamentari, 3-6 = tempi supplementari.
    // Il valore è memorizzato come "casa-ospiti" per essere facilmente serializzato nel JSON.
    public Dictionary<int, string> PhaseScores { get; set; } = new();

    public TeamState Casa { get; set; } = new("");
    public TeamState Ospiti { get; set; } = new("");

    public List<EventRecord> Events { get; set; } = [];
    public List<ShootoutAttempt> Shootout { get; set; } = [];
    public bool ShootoutStarted { get; set; }
    public bool ShootoutFinished { get; set; }
    public string ShootoutFirstTeam { get; set; } = "";
    public int ShootoutTurn { get; set; }
    public int ShootoutPhase { get; set; } = 0;
    public int ShootoutScoreA { get; set; }
    public int ShootoutScoreB { get; set; }

    // Giocatori che hanno già effettuato il tiro nel giro corrente della serie.
    // Vengono azzerati solo quando tutti gli eleggibili della rispettiva squadra
    // hanno completato il giro; gli espulsi/inibiti restano sempre esclusi.
    public List<string> ShootoutTakenA { get; set; } = [];
    public List<string> ShootoutTakenB { get; set; } = [];
}

namespace RefertoPallamano_Blazor.Models;

public sealed class ShootoutAttempt
{
    public int Id { get; set; }
    public int Phase { get; set; }
    public int Round { get; set; }
    public string Team { get; set; } = "";
    public string Number { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public bool Goal { get; set; }
    public int ScoreA { get; set; }
    public int ScoreB { get; set; }
}

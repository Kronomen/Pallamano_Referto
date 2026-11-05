namespace RefertoPallamano_Blazor.Models;

public sealed class TeamState
{
    public TeamState(string name) => Name = name;

    public string Name { get; set; }
    public string Color { get; set; } = "#2563EB";

    public int[] Numbers { get; set; } = Enumerable.Range(1, 16).ToArray();
    // Etichetta visualizzata della maglia: consente di distinguere 0 da 00.
    public string[] NumberLabels { get; set; } = Enumerable.Range(1, 16).Select(n => n.ToString()).ToArray();
    public string[] PlayerNames { get; set; } = new string[16];
    public bool[] ActivePlayers { get; set; } = Enumerable.Repeat(true, 16).ToArray();
    public string[] StaffLetters { get; set; } = ["A", "B", "C", "D", "E"];
    public string[] StaffNames { get; set; } = new string[5];
    public bool[] ActiveStaff { get; set; } = Enumerable.Repeat(true, 5).ToArray();
}

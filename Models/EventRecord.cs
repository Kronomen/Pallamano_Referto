namespace RefertoPallamano_Blazor.Models;

public sealed class EventRecord
{
    public string Time { get; set; } = "00:00";
    public string Team { get; set; } = "";
    public string Number { get; set; } = "";
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string Result { get; set; } = "";
    // Valore del cronometro gara al momento dell'inizio dell'esclusione di 2 minuti.
    // Il countdown segue quindi il cronometro della gara e non il semplice testo dell'evento.
    public int? SuspensionStartSeconds { get; set; }
    // Istante assoluto di fine della sospensione sul cronometro ufficiale.
    // Permette di mantenere i 2' attivi anche quando si passa al periodo successivo.
    public int? SuspensionEndSeconds { get; set; }
}

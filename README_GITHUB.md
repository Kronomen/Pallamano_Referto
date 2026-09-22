# Referto Pallamano — v123

Versione Blazor WebAssembly in C# ottimizzata per iPad 11" in orizzontale e GitHub Pages.

## Stato attuale

- Configurazione gara ottimizzata per iPad 11".
- Controlli superiori separati: durata, cronometro palazzetto, stampa cartellino.
- Impianto e Campionato nello stesso rigo.
- Arbitro 1 e Arbitro 2 nello stesso rigo e più larghi.
- Colori Casa/Ospiti sotto le rispettive squadre.
- Tre pannelli principali dimensionati sul contenuto, senza altezza fissa artificiale.
- Pulsanti inferiori compatti e visibili nella schermata iPad.
- Pulsante `REFERTO GARA`.
- Pulsante `ESCI`.
- Registro Gara modificabile a cronometro fermo tramite popup.
- Modifica/eliminazione evento con ricalcolo di punteggio, reti, 7m, ammonizioni, esclusioni 2' ed espulsioni.
- 3x2' gestito come esclusione definitiva.
- GitHub Actions configurata per GitHub Pages con base path `/Referto_Pallamano/`.

## Pubblicazione

Caricare il contenuto della cartella del progetto nel repository GitHub e fare push sul branch `main`.
L'azione `.github/workflows/deploy.yml` esegue la pubblicazione Blazor e il deploy su GitHub Pages.

## Nota

La compilazione deve essere verificata in Visual Studio/.NET sul computer locale, perché l'ambiente di lavoro usato per preparare il pacchetto non dispone del comando `dotnet`.

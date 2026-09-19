# Referto Pallamano — v108

Aggiornamento della schermata Tiri di Rigore finali con impostazione grafica ispirata al modello fornito: intestazione con risultato, pannelli CASA/OSPITI laterali, serie ufficiale centrale, legenda e comandi Undo/Salva.

La logica dei tiri di rigore resta integrata nel progetto Blazor: i gol della serie aumentano il risultato complessivo ma non il conteggio dei gol personali del giocatore.

Per il controllo locale in Visual Studio: aprire `RefertoPallamano_Blazor.sln`, attendere il ripristino NuGet e avviare il profilo `RefertoPallamano_Blazor`.


## Correzione regolamentare v109 — tiri di rigore ad oltranza
Dopo i primi 5 tiri per squadra, se il punteggio della serie è pari, la gara prosegue ad oltranza. A ogni coppia di tiri, cioè dopo che entrambe le squadre hanno effettuato lo stesso numero di tiri, se una squadra è in vantaggio la serie termina e quella squadra è vincente.

Durante l'oltranza, un giocatore che ha già effettuato un tiro non può essere selezionato nuovamente finché tutti i giocatori eleggibili della sua squadra non hanno effettuato un tiro. Gli espulsi/inibiti restano sempre esclusi. Quando tutti gli eleggibili hanno completato il giro, il gruppo dei potenziali tiratori viene azzerato e tutti gli eleggibili possono tornare a tirare.

Versione layout: v117 — configurazione iPad 11" compatta, pannelli accorciati e barra azioni ridotta.

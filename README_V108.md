# Referto Pallamano — v108

Aggiornamento della schermata Tiri di Rigore finali con impostazione grafica ispirata al modello fornito: intestazione con risultato, pannelli CASA/OSPITI laterali, serie ufficiale centrale, legenda e comandi Undo/Salva.

La logica dei tiri di rigore resta integrata nel progetto Blazor: i gol della serie aumentano il risultato complessivo ma non il conteggio dei gol personali del giocatore.

Per il controllo locale in Visual Studio: aprire `RefertoPallamano_Blazor.sln`, attendere il ripristino NuGet e avviare il profilo `RefertoPallamano_Blazor`.


## Correzione regolamentare v109 — tiri di rigore ad oltranza
Dopo i primi 5 tiri per squadra, se il punteggio della serie è pari, la gara prosegue ad oltranza. A ogni coppia di tiri, cioè dopo che entrambe le squadre hanno effettuato lo stesso numero di tiri, se una squadra è in vantaggio la serie termina e quella squadra è vincente.

Durante l'oltranza, un giocatore che ha già effettuato un tiro non può essere selezionato nuovamente finché tutti i giocatori eleggibili della sua squadra non hanno effettuato un tiro. Gli espulsi/inibiti restano sempre esclusi. Quando tutti gli eleggibili hanno completato il giro, il gruppo dei potenziali tiratori viene azzerato e tutti gli eleggibili possono tornare a tirare.

Versione layout: v117 — configurazione iPad 11" compatta, pannelli accorciati e barra azioni ridotta.

## Invio referto PDF via WhatsApp da iPad
A fine gara è disponibile il pulsante **INVIA PDF VIA WHATSAPP**. Inserire il numero del destinatario con prefisso internazionale. Il primo tocco genera il PDF; il secondo apre il pannello di condivisione di iPadOS. Il PDF viene creato dal modello ufficiale FIGH e inviato al pannello di condivisione nativo di iPadOS; da lì scegliere WhatsApp.

**Limite di iPadOS/WhatsApp:** una pagina web non può forzare WhatsApp ad allegare un file e inviarlo automaticamente a un numero specifico. Per sicurezza il destinatario va selezionato/confermato in WhatsApp. La generazione del PDF e l'apertura della condivisione sono automatizzate. La generazione PDF usa html2pdf.js da CDN e richiede una connessione internet.

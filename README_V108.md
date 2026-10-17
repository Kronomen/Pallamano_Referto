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

## Invio del referto a più destinatari (WhatsApp ed email)
La finestra di condivisione consente di inserire fino a 10 numeri WhatsApp e fino a 10 indirizzi email. Il PDF viene generato una sola volta e scaricato sul dispositivo. Per WhatsApp, il programma apre direttamente la chat del numero selezionato, una chat alla volta; l'operatore deve allegare il PDF scaricato e premere INVIA in WhatsApp per ciascun destinatario. Il sito non può inviare autonomamente allegati o certificare la consegna.

Per email, il programma apre il client di posta con tutti gli indirizzi inseriti nei destinatari e scarica il PDF. Per limiti del browser e dello schema `mailto:`, il file non può essere allegato automaticamente: va allegato manualmente dall'app di posta prima dell'invio. La procedura va provata su iPad/Safari e sul client di posta configurato.

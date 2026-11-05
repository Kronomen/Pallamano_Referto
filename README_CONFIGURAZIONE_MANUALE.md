# Configurazione Manuale — Referto Pallamano

La schermata manuale si apre dal pulsante **CONFIGURAZIONE MANUALE**, accanto a **CARICA CONFIGURAZIONE**.

Include: numero gara, data, ora, campionato, località, impianto, stagione, arbitri, delegati, cronometrista, segretario; nome di entrambe le squadre; 16 righe giocatori con numero maglia e nome; 5 righe dirigenti con lettera e nome.

Il pulsante **SALVA DATI MANUALI** conserva i valori nello stato della gara e chiude la finestra. Successivamente si può premere **REFERTO GARA** per proseguire con la normale configurazione e avviare la partita.

Il modulo riutilizza i dati già esistenti del progetto, senza creare una seconda struttura separata per i giocatori.

## Team Time-Out — Regolamento FIGH 2025

- 1 minuto per ogni Team Time-Out; il cronometro ufficiale resta fermo durante il countdown.
- Massimo 3 per squadra in tutta la gara regolamentare, massimo 2 per ciascun tempo; nessun Team Time-Out nei supplementari.
- Negli ultimi 5 minuti del secondo tempo, massimo 1 Team Time-Out per squadra in quella finestra.
- Con **CONTROLLO POSSESSO TIME OUT** attivo, la squadra richiedente deve essere indicata come in possesso della palla tramite il pulsante **PALLA**; inoltre, tra due time-out della stessa squadra il programma richiede la conferma di almeno un possesso avversario.
- Se il controllo del possesso è disattivato nella configurazione, il pulsante **PALLA** scompare e l’operatore verifica manualmente i requisiti di possesso previsti dal regolamento. Restano sempre attivi i limiti FIGH su numero di time-out, tempi regolamentari e ultimi 5 minuti del secondo tempo.
- Al termine del minuto, il popup resta visibile a **00:00** finché l’operatore preme **CHIUDI TIME OUT**. Il pulsante è disabilitato prima della fine del minuto; la chiusura non riavvia il cronometro ufficiale, che riparte solo quando l’operatore preme **START**.

Nella configurazione gara, i campi **Data** e **Ora inizio gara** sono affiancati e compatti; l’ora viene salvata e riportata nei dati del referto.

## Fine periodo e fine gara

Alla fine del tempo il popup iniziale serve soltanto a confermare la chiusura del periodo. Dopo l'OK, il cronometro resta fermo e l'operatore può aggiungere o correggere gli eventi. Premendo nuovamente **FINE PERIODO** si apre la conferma del periodo successivo; dopo l'OK il nuovo periodo è pronto ma il cronometro riparte solo con **START**. Se il periodo chiuso conclude la gara, viene mostrato il popup con la squadra vincente oppure con il risultato finale in parità.

## Invio del referto a più destinatari (WhatsApp ed email)
La finestra di condivisione consente di inserire fino a 10 numeri WhatsApp e fino a 10 indirizzi email. Il PDF viene generato una sola volta e scaricato sul dispositivo. Per WhatsApp, il programma apre direttamente la chat del numero selezionato, una chat alla volta; l'operatore deve allegare il PDF scaricato e premere INVIA in WhatsApp per ciascun destinatario. Il sito non può inviare autonomamente allegati o certificare la consegna.

Per email, il programma apre il client di posta con tutti gli indirizzi inseriti nei destinatari e scarica il PDF. Per limiti del browser e dello schema `mailto:`, il file non può essere allegato automaticamente: va allegato manualmente dall'app di posta prima dell'invio. La procedura va provata su iPad/Safari e sul client di posta configurato.

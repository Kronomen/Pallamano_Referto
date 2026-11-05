# Modulo Archivio – Referto Pallamano

Questa prima integrazione aggiunge il modulo `/archivio` al progetto Blazor WebAssembly.

## Struttura dati

- Campionati: entità dinamiche, non codificate come elenco chiuso.
- Stagioni: intervalli sportivi configurabili.
- Squadre: anagrafiche riutilizzabili.
- Persone: anagrafiche di atleti e dirigenti.
- Iscrizioni: collegano squadra, campionato, stagione e persona; il numero di maglia e la lettera del dirigente appartengono all'iscrizione.

## Persistenza

Il progetto attuale è una Blazor WebAssembly statica pubblicata su GitHub Pages e non dispone di un server applicativo. Per questa prima fase l'archivio viene conservato localmente nel browser tramite IndexedDB. I dati non sono sincronizzati automaticamente tra dispositivi o browser. È disponibile l'esportazione JSON come copia di sicurezza.

## Importazione Excel

La funzione importa i fogli `Squadre` e `Giocatori Dirigenti` del modello `.xlsm` e legge la stagione dal foglio `Gara`. I campionati vengono individuati dalle intestazioni del modello; l'elenco non è limitato ai tre campionati iniziali. Le anagrafiche già presenti vengono riutilizzate per ridurre i duplicati.

## Prima fase

Questa versione costituisce la base del modulo archivio: gestione di campionati, squadre, stagioni, anagrafiche e associazioni. Il collegamento diretto della selezione delle rose alla configurazione della partita è una fase successiva, da completare dopo la verifica del modulo e la conferma dell'importazione dei dati.


## Collegamento con la configurazione della gara (Fase 2)

Nella finestra «CONFIGURAZIONE SQUADRE» è disponibile il riquadro «CARICA LE ROSE DALL'ARCHIVIO».
L'operatore seleziona campionato, stagione e squadra CASA/OSPITI, quindi carica la rosa.
La distinta viene copiata nello stato della gara: le modifiche successive ai nominativi o ai numeri
nella distinta non modificano le anagrafiche permanenti dell'archivio.
I limiti della distinta restano quelli già previsti dal referto: 16 giocatori e 5 dirigenti per squadra.


## Correzione importazione Excel e rose (Fase 2.1)

L'importatore individua i gruppi del foglio «Squadre» leggendo le intestazioni della riga 5
e raccoglie i nomi delle squadre sottostanti. Nel foglio «Giocatori Dirigenti» individua
ogni blocco tramite la riga 3 (Squadra | Giocatori/Dirigenti | NU) e la denominazione
del campionato nella riga 2. Il messaggio finale distingue i dati letti dal file dai totali
già presenti nell'archivio. Le reimportazioni aggiornano i numeri e le lettere delle
associazioni già esistenti, senza creare doppioni.


## Archivio iniziale incluso nel progetto

Il progetto include `wwwroot/data/archivio-iniziale.json`, generato dal modello Excel fornito. Al primo avvio, se non esiste un archivio locale popolato, i dati iniziali vengono caricati automaticamente nel browser. L'archivio locale successivo prevale sul catalogo iniziale, così le modifiche dell'utente non vengono sovrascritte ad ogni avvio.


## Convocazioni per singola gara (Fase 4)

L'organico completo resta nell'Archivio. Nella configurazione squadre si selezionano i convocati:
massimo 16 atleti e 5 dirigenti. Per ogni atleta si può modificare il numero di maglia della
distinta; per ogni dirigente si assegna la lettera A-E. È possibile aggiungere rapidamente una
persona non presente: viene creata nell'Archivio e associata alla squadra/campionato/stagione.
Il campo Campionato è stato rimosso dalla configurazione generale gara; il valore viene
impostato dalla selezione nella configurazione squadre e resta disponibile per il referto.


## Pagina convocazioni dedicata (Fase 5)

La configurazione squadre non mostra più i 16 campi atleta e i 5 campi dirigente. Mostra riepiloghi e pulsanti per aprire una pagina dedicata CASA/OSPITI. Gli atleti sono elencati alfabeticamente: digitare un numero di maglia equivale a convocare l'atleta. La conferma valida massimo 16 convocati, numeri 0/00 o 1-99, evita duplicati e trasferisce la distinta ordinata per numero. I dirigenti sono selezionabili con checkbox e lettera A-E, massimo 5. La pagina include ricerca e inserimento rapido.

Nota: aprendo la pagina convocazioni, i numeri di maglia della rosa non vengono preselezionati automaticamente. L'operatore inserisce i numeri soltanto per gli atleti da convocare; se riapre la pagina durante la stessa gara, vengono mantenuti i numeri già presenti nella distinta.


## Schermo intero tablet (Fase 6)

È disponibile un piccolo pulsante flottante in basso a destra per entrare/uscire
dalla modalità schermo intero. Il pulsante usa le API Fullscreen del browser e
aggiorna icona e descrizione in base allo stato. Su iPad/Safari la disponibilità
dipende dal browser e dalla modalità di apertura della web app; se il browser
non consente la Fullscreen API, resta disponibile la modalità Aggiungi alla schermata Home.
La pagina mantiene il layout responsive e non forza una risoluzione specifica.


## Layout configurazione squadre (Fase 6.1)

I riquadri Squadra di casa e Squadra ospite sono disposti affiancati su tablet e desktop, con due colonne di uguale larghezza. Solo su schermi stretti (larghezza massima 600 px) il layout passa a una colonna.

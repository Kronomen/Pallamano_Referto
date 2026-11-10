# Appunti e miglioramenti – Referto Pallamano

Questo documento raccoglie le osservazioni emerse durante l'utilizzo reale del programma.
Le modifiche non vanno applicate direttamente alla versione stabile: prima si annotano,
poi si valutano e si sviluppano sul ramo di lavoro. Ogni modifica deve essere verificata
prima di essere integrata nella versione definitiva.

## Come registrare una nuova osservazione

Per ogni segnalazione compilare, quando possibile:

- **Data:**
- **Sezione del programma:**
- **Cosa succede / cosa manca:**
- **Comportamento desiderato:**
- **Gravità:** bloccante / importante / miglioramento
- **Stato:** da valutare / programmato / in lavorazione / verificato / archiviato

## Registro delle osservazioni

| N. | Data | Sezione | Osservazione / modifica richiesta | Priorità | Stato |
|---:|---|---|---|---|---|
| 1 |  |  |  |  | Da valutare |

## Regole per gli aggiornamenti

1. Non eliminare o modificare funzioni già collaudate senza una richiesta precisa.
2. Prima di ogni intervento individuare i file interessati e verificare gli effetti sulle altre sezioni.
3. Sviluppare e compilare le modifiche su un ramo separato.
4. Conservare una versione stabile precedente e non sovrascrivere gli archivi dati.
5. Prima di distribuire un aggiornamento, verificare compilazione, sincronizzazione e stampa del referto.
6. Le osservazioni risolte restano nel registro con lo stato aggiornato: costituiscono la cronologia delle decisioni.

## Stato iniziale

La base di partenza è il ramo `operator-access`, commit
`f477a31d1d7614ef5bd7089fcf941d01bfc68934`.

Sono già state verificate la sincronizzazione cloud bidirezionale, la persistenza dopo
riapertura, la gestione degli operatori, la rigenerazione dei codici, la revoca dei
dispositivi e la visualizzazione del nome dell'operatore.

**Nota di rilascio:** questa copia di lavoro mantiene attualmente la configurazione del
Worker di test. Prima di un rilascio operativo definitivo occorre verificare e configurare
esplicitamente il Worker e l'archivio dati di produzione. Non modificare il Worker,
il repository dati o il branch `main` durante questa preparazione.

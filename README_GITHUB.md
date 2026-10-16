# Referto Pallamano — pacchetto per GitHub Pages e iPad

Questa cartella contiene il progetto Blazor WebAssembly e il workflow che compila e pubblica il sito su GitHub Pages.

## Caricamento su GitHub

1. Crea su GitHub un repository, per esempio `Referto_Pallamano`.
2. Estrai questo ZIP sul computer.
3. Apri la cartella estratta: devi vedere subito `RefertoPallamano_Blazor.csproj`, `Program.cs`, `Components`, `Models`, `Services`, `wwwroot` e `.github`. **Non caricare la cartella contenitore esterna**: carica il contenuto di questa cartella nella radice del repository.
4. In GitHub scegli **Add file → Upload files**, trascina tutti i file e le cartelle, quindi fai **Commit changes** sul branch `main`.
5. Apri la scheda **Actions** e attendi che `Deploy Referto Pallamano` termini con esito verde.
6. In **Settings → Pages**, se richiesto, seleziona la pubblicazione tramite **GitHub Actions**.
7. Apri l'indirizzo indicato dal deployment. Per un repository chiamato `Referto_Pallamano`, sarà in genere `https://NOMEUTENTE.github.io/Referto_Pallamano/`.

Il workflow imposta automaticamente il percorso corretto in base al nome del repository: non è necessario modificare manualmente `wwwroot/index.html` se scegli un nome diverso.

## Prova su iPad

1. Apri l'URL pubblicato con Safari su iPad e attendi il caricamento completo.
2. Prova ad aprire la gestione gara e verifica i comandi principali.
3. Per il referto, usa una gara di prova: controlla che il referto FIGH si apra e che il comando di uscita non mostri la schermata iniziale.
4. Prova **INVIA PDF VIA WHATSAPP**: inserisci il numero, genera il PDF con il primo tocco, poi tocca di nuovo per aprire il pannello Condividi di iPad e scegliere WhatsApp. L'invio finale richiede la conferma dell'utente; un sito web non può inviare in autonomia un allegato a un contatto WhatsApp specifico.
5. Verifica che il PDF contenga tutte le pagine e che i dati della gara siano corretti prima di usarlo in una partita ufficiale.

## Importante

Il pacchetto è predisposto per la pubblicazione su GitHub Pages. La pubblicazione deve essere completata sul tuo account GitHub e la prova effettiva su iPad deve essere eseguita sul dispositivo: da questo ambiente non posso accedere al tuo account né controllare fisicamente il comportamento di Safari/WhatsApp sul tuo iPad.

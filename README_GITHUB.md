# Referto Pallamano — GitHub Pages

Progetto Blazor WebAssembly in C#, predisposto per la pubblicazione su GitHub Pages.

## Caricamento nel repository

1. Apri il repository GitHub `Referto_Pallamano` dell'account `kronomen` (oppure crea un repository con questo nome).
2. Estrai questo ZIP sul computer.
3. Carica **tutti i file e le cartelle contenuti nello ZIP nella radice del repository**. Non caricare lo ZIP come singolo file e non caricare la cartella contenitore. La cartella `.github/workflows` deve essere presente nella radice.
4. Salva/committa i file sul branch `main`.
5. In GitHub apri **Settings → Pages** e seleziona **GitHub Actions** come origine della pubblicazione.
6. Apri **Actions** e attendi il completamento di `Deploy Referto Pallamano`.

Se il repository è `kronomen/Referto_Pallamano`, l'indirizzo previsto è:

`https://kronomen.github.io/Referto_Pallamano/`

## Esecuzione locale

Richiede il .NET 10 SDK. Dalla cartella che contiene `RefertoPallamano_Blazor.csproj`, esegui:

```bash
dotnet restore
dotnet build
dotnet run
```

## Nota

Il workflow usa .NET 10 e pubblica automaticamente a ogni push sul branch `main`. Prima dell'uso ufficiale, testare l'applicazione e la stampa con una gara di prova.

# Referto Pallamano — versione controllo Visual Studio

Questa copia è predisposta per il controllo locale su PC con Visual Studio.

## Avvio
1. Aprire `RefertoPallamano_Blazor.sln` in Visual Studio.
2. Attendere il ripristino dei pacchetti NuGet.
3. Selezionare il profilo `RefertoPallamano_Blazor` (HTTP).
4. Premere F5 oppure il pulsante ▶.
5. L'app si apre su `http://localhost:5200/`.

## Nota GitHub Pages
`wwwroot/index.html` usa localmente `<base href="/" />`.
Il workflow `.github/workflows/deploy.yml` sostituisce automaticamente la base con `/Pallamano_Referto/` durante la pubblicazione su GitHub Pages.

Non sono inclusi `.vs`, `bin` e `obj`, perché Visual Studio li ricrea automaticamente.

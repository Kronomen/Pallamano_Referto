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

## Invio del referto a più destinatari (WhatsApp ed email)
La finestra di condivisione consente di inserire fino a 10 numeri WhatsApp e fino a 10 indirizzi email. Il PDF viene generato una sola volta e scaricato sul dispositivo. Per WhatsApp, il programma apre direttamente la chat del numero selezionato, una chat alla volta; l'operatore deve allegare il PDF scaricato e premere INVIA in WhatsApp per ciascun destinatario. Il sito non può inviare autonomamente allegati o certificare la consegna.

Per email, il programma apre il client di posta con tutti gli indirizzi inseriti nei destinatari e scarica il PDF. Per limiti del browser e dello schema `mailto:`, il file non può essere allegato automaticamente: va allegato manualmente dall'app di posta prima dell'invio. La procedura va provata su iPad/Safari e sul client di posta configurato.

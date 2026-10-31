# Cum explorezi o simulare

Folosește date fictive pentru a înțelege un flux de pasageri. Pentru modificări
și lansări ai nevoie de rolul Operator sau Admin. O simulare arată rezultatul
unui model demonstrativ, nu o predicție validată pentru un aeroport real.

1. **Creează un scenariu** (`/scenario-studio`): alege exemplul recomandat sau
   construiește unul cu propriile zboruri, numărul de pasageri și personalul.
   Parcurge pașii, verifică și salvează. [Ghidul editorului](simulation-journey.md)
   explică și setările detaliate.
2. **Scenariile mele** (`/scenario`): alege scenariul și apasă **Pornește
   simularea**. La final poți citi indicatorii, urmări terminalul și trece la
   comparație sau la repetarea testului. Dacă vii din editor, scenariul salvat
   este deja selectat.
3. **Compară personalul** (`/compare`): alege scenariul, controlul și numărul
   de operatori pentru noua variantă. Se rulează configurația inițială și o
   copie cu alocarea modificată. Originalul este păstrat. Dacă nu sunt operatori
   liberi, alocările trebuie schimbate în editor. Comparabilitatea și datele
   indisponibile sunt explicate în rezultat.
4. **Teste repetate** (`/batches`): alege scenariul și numărul de repetări,
   opțional selectează până la patru variante. Interfața arată câte simulări
   vor fi lansate și limita de timp. Rezumatul prezintă media și valorile
   minimă/maximă disponibile; o simulare eșuată nu este transformată în zero.
   Intervalul observat nu garantează rezultatele viitoare. Lista de seed-uri,
   diagnosticele și statisticile complete sunt în secțiunile detaliate.
5. **Flux de decizie** (`/operator-workflow`): alege un zbor și un val de
   pasageri, simulează, caută o împărțire mai bună a operatorilor, compară și
   aprobă sau respinge, cu audit. Pentru scenarii de cel puțin 2 ore (butonul
   **Zi de lucru · 8 ore**) poți cere un **plan pe ture la 15 minute**, cu ture,
   pauze, mutări și încărcarea țintă a operatorilor; comparația arată cine e
   la fiecare control la fiecare sfert de oră. Detaliile modelului sunt în
   [algoritmi și ipoteze](product/algorithms-and-assumptions.md#personal-pe-ture-la-15-minute).

Toate comenzile de pornire sunt explicite. O ciornă nu este salvată automat,
procesele în curs își afișează progresul, iar rezultatele calculate și istoricul
rămân disponibile ulterior. Revezi rulările persistate în Time Travel după autentificare.


### Închideri, puncte de control și redistribuirea cozilor

În fila **Trasee**, închiderea unei zone recalculează imediat traseele din editor, fără să salveze scenariul. Rezultatul arată pasagerii cu acces la poartă, pasagerii fără traseu și durata medie de mers de bază. Salvarea aplică această configurație următoarelor simulări; cozile și riscul de întârziere se evaluează prin simulare.

Agentul cu mutări de personal activate poate propune deschiderea unui punct de control existent, fără operatori, folosind rezerva sau un operator disponibil de la alt control. Poate redistribui și o parte din pasagerii încă aflați în coadă către un control cu capacitate liberă. Acțiunile respectă terminalul, disponibilitatea, personalul și timpul de transfer; motorul verifică din nou accesul spre poartă. Procesarea deja începută continuă, iar istoricul KPI rămâne capturat. Un pasager poate schimba coada o singură dată într-o rulare.

În **AI Copilot**, acțiunea **Puncte de control și cozi** afișează variante calculate din snapshot-ul selectat și datele folosite. Analiza nu aplică modificări. Numărul propus de pasageri este o limită de transfer, iar beneficiul trebuie verificat în simulator. „Punct suplimentar” înseamnă un punct existent în layout, nu crearea unei noi infrastructuri.

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

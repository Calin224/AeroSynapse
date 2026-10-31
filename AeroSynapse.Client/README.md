# AeroSynapse Client

Aplicație Angular standalone, organizată pe feature-uri, cu PrimeNG/Aura și Three.js.
Control Tower afișează un snapshot aeroportuar; Scenario permite selectarea unor
configurații demonstrative, aplicate aceluiași model 3D.

## Pornire locală

Folosește o versiune Node.js compatibilă cu Angular 21 (Node 22.12+ sau Node 24 LTS)
și npm. Versiunile exacte instalate sunt păstrate în package-lock.json.

```powershell
npm.cmd ci
npm.cmd start
```

Rute: http://localhost:4200/control-tower și http://localhost:4200/scenario.
Ruta / redirecționează la Control Tower; adresele necunoscute afișează o pagină 404.

În checkout-uri cu caracterul literal % în cale, inclusiv AeroSynapse%20AI,
scripts/start.mjs dezactivează prebundling, HMR și live reload pentru a evita
importurile /@fs/ decodificate greșit de browser. Compilarea continuă să urmărească
modificările; reîncarcă manual pagina după editare. Pentru căi fără %, rămân
opțiunile standard Angular. Scriptul transmite argumentele npm start -- ... către CLI.

## Stack și temă

Angular 21 și PrimeNG 21 au peer dependencies compatibile. PrimeNG este fixat
la 21.1.10, iar @primeuix/themes la 2.0.3. Aceste versiuni evită configurarea
unei chei de licență introdusă în generația PrimeNG 22.
Documentația furnizorului: https://primeui.dev/licenses/community.

Tema Aura este configurată prin providePrimeNG în app.config.ts; clasa app-dark
din index.html activează paleta întunecată. CSS responsiv adaptează navigarea,
indicatorii și panourile pentru desktop și telefon, fără servicii externe de fonturi.

## Organizare

- core/http: ApiClient, configurare runtime, DemoDataClient și interceptor funcțional pentru normalizarea erorilor.
- shared: resource HTTP reutilizabil, loading/empty/error cu retry și pagina 404.
- features/control-tower: modele, store și dashboard.
- features/scenario: store, selecție și previzualizare de scenarii.
- features/digital-twin: componenta Angular, contractul TwinRenderer, renderer Three.js,
  încărcarea modelelor GLB și gestionarea resurselor GPU.
- public/models/aerosynapse: terminalul, exteriorul cu două niveluri de detaliu și manifestul entităților.
- public/data: fixture-uri locale servite prin HTTP; nu reprezintă telemetrie live.

Dashboardurile transmit un TwinScene rendererului. Ele nu dețin camere, materiale,
geometrii sau WebGLRenderer. TWIN_RENDERER_FACTORY permite înlocuirea rendererului
în teste, astfel încât testele rutelor și stărilor să ruleze fără GPU.

Rendererul este inițializat după render-ul Angular și rulează în afara fluxului de
actualizare al dashboardului. Are OrbitControls, resetarea camerei și ResizeObserver.
La distrugere oprește bucla de animație, deconectează observerul, elimină listenerii,
eliberează controalele, geometriile, materialele și texturile partajate, apoi
eliberează rendererul/contextul WebGL și elimină canvasul. Aeronavele reutilizează
geometria modelelor la schimbarea snapshot-ului. Randarea continuă este suspendată
când documentul este ascuns. Erorile de inițializare și pierderea contextului au retry în UI.

Terminalul folosește modelul GLB cu acoperiș transparent. Modurile Interior / Exterior,
filtrele Aeronave / Bagaje / Vehicule / Pistă și selecția prin click sau listă sunt locale
vizualizării. Exteriorul și nivelul detaliat se încarcă la cerere; un eșec permite retry
și păstrează modelul deja încărcat. Entitățile au identificatori stabili în manifest,
fără a crea date în backend. Vezi [documentația modelelor 3D](../docs/airport-3d-assets.md).

## HTTP și configurare runtime

`public/runtime-config.js` este încărcat înaintea aplicației și configurează adresele
publice ale serviciilor, implicit `apiBaseUrl: '/api'` și
`intelligenceBaseUrl: '/intelligence'`. La găzduire, fișierul livrat poate fi înlocuit
fără recompilarea aplicației; servește-l fără cache pentru a evita configurații vechi.
Fișierul este public și nu trebuie să conțină parole sau chei API. Configurația implicită
se aplică și dacă variabila globală nu este definită.

ApiClient citește `API_BASE_URL` din această configurație. DemoDataClient citește
separat fixture-urile din `/data`, astfel încât schimbarea adresei backendului nu mută
datele demonstrative. Interceptorul păstrează statusul HTTP și produce mesaje utile
fără a afișa detalii interne de la server.

`npm start` activează `proxy.conf.json` pentru dezvoltare:

| Cerere din browser | Destinație |
| --- | --- |
| `/api/health` | `http://127.0.0.1:5080/health` |
| `/api/**` | `http://127.0.0.1:5080/api/**`, cu ruta păstrată pentru hărțile aeroporturilor |
| `/intelligence/**` | `http://127.0.0.1:8000/**`, fără prefixul `/intelligence` |

Proxy-ul Angular este doar pentru dezvoltare. Găzduirea de producție trebuie să
configureze propriul proxy sau adrese compatibile cu politica CORS a serviciilor.

Footer-ul verifică o dată, la încărcarea aplicației, endpoint-urile de sănătate API și
FastAPI, cu timeout de 4 secunde. Afișează verificare, conectat sau indisponibil,
independent pentru fiecare serviciu. Serviciul FastAPI nu este inclus încă în acest
repository; absența lui sau a API-ului nu împiedică explorarea datelor demo.

Resource-ul reutilizabil expune loading, ready, empty și error. Refresh/retry anulează
cererea precedentă prin switchMap; distrugerea feature-ului anulează abonamentul.
Nu sunt efectuate retry-uri automate pentru cereri eșuate.

Pentru verificare manuală:

- Loading: activează throttling în DevTools și actualizează pagina.
- Empty Control Tower: înlocuiește temporar fixture-ul control-tower.json cu null.
- Empty Scenario: înlocuiește temporar scenarios.json cu [].
- Error: blochează requestul /data/control-tower.json în DevTools și actualizează.
- Retry: deblochează requestul și apasă „Încearcă din nou”.
- 3D: rotește modelul, mărește-l, resetează camera și navighează între feature-uri.
  Restabilește fixture-urile după verificare. Scenariile schimbă poziții demonstrative;
  un motor de simulare și telemetria reală vor fi integrate separat.

## Build și teste

```powershell
npm.cmd test -- --watch=false
npm.cmd run build
npm.cmd run verify:assets
```

TypeScript și template-urile Angular folosesc verificare strictă.
Build-ul de producție are rute lazy și bugete pentru bundle-ul inițial și stiluri.
Output: dist/AeroSynapse.Client/browser. Hostingul SPA trebuie să redirecționeze
cererile de navigare către index.html pentru accesul direct la rute.

Testele Vitest verifică HTTP/interceptorul, rutele și fallbackul, loading/empty/error,
retry-ul, selecția de scenarii, anularea cererilor și cleanup-ul rendererului.
Testele folosesc porturi browser simulate pentru GPU, controale și observer;
vizualizarea reală trebuie verificată și într-un browser cu WebGL disponibil.
Verificatorul de asset-uri importă GLB-urile prin Three.js/Meshopt fără GPU și verifică
dimensiunile, axele, transparența, identificatorii și bugetele modelelor.

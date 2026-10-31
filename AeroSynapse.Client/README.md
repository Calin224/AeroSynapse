# AeroSynapse Client

Aplicație Angular standalone, organizată pe feature-uri, cu PrimeNG/Aura și Three.js.
Pagina `/operations` folosește API-ul pentru scenarii persistente, lansare,
consultarea metricilor/stărilor și retry după eșec. Consultă [modelul de date și contractele](../docs/product/data-and-contracts.md).
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
opțiunile standard Angular. Scriptul transmite argumentele npm.cmd start -- ... către CLI.

## Stack și temă

Angular 21 și PrimeNG 21 au peer dependencies compatibile. PrimeNG este fixat
la 21.1.10, iar @primeuix/themes la 2.0.3. Aceste versiuni evită configurarea
unei chei de licență introdusă în generația PrimeNG 22.
Documentația furnizorului: https://primeui.dev/licenses/community.

Tema Aura este configurată prin providePrimeNG în app.config.ts, cu presetul din
core/theme/app-theme.ts. Spațiul de lucru folosește suprafețe deschise, navigare verde
închis și o suprafață distinctă pentru terminalul 3D. Tokenii de design sunt în src/styles.css.
Fonturile Geist și Geist Mono vin din pachetele @fontsource-variable și sunt servite
local, fără servicii externe. CSS responsiv adaptează navigarea, indicatorii și
panourile pentru desktop și telefon.

Navigarea păstrează etichete lizibile și ținte de minimum 44 px în meniul mobil.
Pe telefon, bara de navigare rămâne vizibilă la derulare; dialogurile blochează
derularea paginii din fundal și restabilesc focusul la închidere. Schimbarea paginii
mută focusul pe titlu. Tabelele ample au derulare proprie și regiuni accesibile
cu tastatura. Verifică layout-ul la 320, 768, 1024 și 1440 px, inclusiv stările de
încărcare, eroare și lipsă de date. Păstrează paleta forest și tokenii existenți.

## Organizare

- core/http: ApiClient, API_BASE_URL și interceptor funcțional pentru normalizarea erorilor.
- shared: resource HTTP reutilizabil, loading/empty/error cu retry și pagina 404.
- features/control-tower: dashboard-ul ultimei rulări finalizate și `run-overview.ts`, care derivă indicatorii, zborurile și scena 3D dintr-o rulare stocată.
- features/scenario: scenariile salvate din API, previzualizarea ultimei lor rulări și lansarea simulărilor.
- features/digital-twin: componenta Angular, contractul TwinRenderer, renderer Three.js,
  încărcarea asset-urilor GLB și gestionarea resurselor GPU.

Dashboardurile transmit un TwinScene rendererului. Ele nu dețin camere, materiale,
geometrii sau WebGLRenderer. TWIN_RENDERER_FACTORY permite înlocuirea rendererului
în teste, astfel încât testele rutelor și stărilor să ruleze fără GPU.

Vizualizarea pornește cu harta 2D. Butonul **3D** încarcă dinamic motorul grafic
și modelele; schimbarea paginii anulează inițializarea rămasă în așteptare.
Rendererul 3D are OrbitControls, resetarea camerei și ResizeObserver.
La distrugere oprește bucla de animație, deconectează observerul, elimină listenerii,
eliberează controalele, geometriile, materialele și texturile partajate, apoi
eliberează rendererul/contextul WebGL și elimină canvasul. Aeronavele din GLB
reutilizează geometria la schimbarea snapshot-ului. Randarea continuă este suspendată
când documentul este ascuns. Erorile de inițializare și pierderea contextului au retry în UI.

Modelul folosește aeroportul furnizat, cu arhitectura originală, acoperiș
transparent și lounge eliminat. Trei controale și cinci porți existente au ID-uri.
Exteriorul și LOD
detaliat sunt încărcate la cerere; modurile și filtrele nu modifică simularea.
Selecția prin click sau listă returnează `zoneId` și `entityId` din glTF extras.
Vezi [manifestul 3D](public/models/aerosynapse/manifest.json) și
[creditele materialelor](../assets/blender/materials/asset-credits.json). `npm run verify:assets` verifică exporturile
reale prin Three.js, independent de WebGL.

## HTTP și backend viitor

ApiClient folosește HttpClient și API_BASE_URL (implicit /api), citit din
runtime-config.js. În Docker, fișierul este generat la pornire din variabilele
publice FRONTEND_API_BASE_URL și FRONTEND_INTELLIGENCE_BASE_URL. Nginx proxy
trimite cererile către API .NET și FastAPI. În dezvoltarea locală, proxy.conf.json
folosește porturile 5080 și 8000. Alertele paginii indică lipsa conexiunii browserului
și cererile API lente, fără a deduce starea serviciilor.
Interceptorul păstrează statusul
HTTP și produce mesaje utile fără a afișa detalii interne de la server.

Resource-ul reutilizabil expune loading, ready, empty și error. Refresh/retry anulează
cererea precedentă prin switchMap; distrugerea feature-ului anulează abonamentul.
Nu sunt efectuate retry-uri automate pentru cereri eșuate.

Pentru verificare manuală:

- Loading: activează throttling în DevTools și actualizează pagina.
- Empty Control Tower: pornește cu o bază de date fără rulări finalizate.
- Empty Scenario: pornește cu o bază de date fără scenarii salvate.
- Error: blochează requestul /api/scenes în DevTools și actualizează.
- Retry: deblochează requestul și apasă „Încearcă din nou”.
- 3D: rotește modelul, mărește-l, resetează camera și navighează între feature-uri.
  Restabilește fixture-urile după verificare. Scenariile schimbă poziții demonstrative;
  un motor de simulare și telemetria reală nu fac parte din acest prototip.

## Timeline de aglomerație

Controalele heatmap și playback-ul pasagerilor colorează cursorul și întreaga bară de timp după cea mai gravă stare observată într-o zonă. Stările critice și avertismentele au prioritate; datele lipsă sunt marcate distinct, fără să pară trafic normal.

Playback-ul folosește ultimul snapshot disponibil înaintea cursorului, fără interpolarea severității. Culorile păstrează durata intervalelor observate, iar eticheta accesibilă a cursorului include minutul și starea de aglomerație. Schimbarea rulării recalculează timeline-ul fără să modifice datele sursă.

## Build și teste

```powershell
npm.cmd test -- --watch=false
npm.cmd run build
```

TypeScript și template-urile Angular folosesc verificare strictă.
Build-ul de producție are rute lazy și bugete pentru bundle-ul inițial și stiluri.
Output: dist/AeroSynapse.Client/browser. Hostingul SPA trebuie să redirecționeze
cererile de navigare către index.html pentru accesul direct la rute.

Testele Vitest verifică HTTP/interceptorul, rutele și fallbackul, loading/empty/error,
retry-ul, selecția de scenarii, anularea cererilor și cleanup-ul rendererului.
Testele folosesc porturi browser simulate pentru GPU, controale și observer;
vizualizarea reală trebuie verificată și într-un browser cu WebGL disponibil.

## Navigare și explorare interactivă

Meniul grupează instrumentele pe operațiuni, simulare, analiză și explorare.
Pe telefon se deschide din butonul de meniu. Căutarea globală se deschide cu Ctrl+K / ⌘K
sau din meniul lateral; acceptă cuvinte fără diacritice, săgeți, Enter și Escape. Destinațiile
recente sunt păstrate doar în memorie. Meniul și căutarea respectă rolul și feature flags;
guard-urile și autorizarea serverului rămân obligatorii.

În Control Tower, „De la scenariu la decizie” deschide ghidul de lucru. Graficele permit
selectarea unui moment prin cursor, atingere sau slider cu tastatura. „Ultimul moment”
revine la observația recentă. Inspecția graficului nu modifică KPI-urile rulării sau harta;
Time Travel oferă replay-ul complet. Selectarea unei alte rulări resetează momentul inspectat.

Verificarea experienței (fixture-uri de test, fără mutații în backend):

```sh
npm start
# În alt terminal:
npx playwright test e2e/workspace-experience.spec.ts
```

Suita verifică autentificarea și recuperarea după eroare, căutarea, rolul Viewer, stările
loading/empty/error/partial, accesibilitatea cu axe, focusul în dialoguri și reflow la
320, 390, 1366 și 1440 px. Capturile locale sunt în `artifacts/experience/` la rădăcina repo-ului.

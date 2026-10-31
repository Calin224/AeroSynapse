# AeroSynapse

AeroSynapse include un backend .NET 10 cu ASP.NET Core Minimal API, MediatR și persistență SQLite pentru hărțile aeroporturilor, plus un frontend Angular cu Control Tower și previzualizări 3D demonstrative.

## Arhitectură

| Proiect | Responsabilitate |
| --- | --- |
| `backend/src/AeroSynapse.Domain` | Modelul aeroportului și validarea hărții: terminale, etaje, zone, rute, checkpoint-uri, porți și resurse. Fără dependențe externe. |
| `backend/src/AeroSynapse.Application` | Query-ul de sănătate, contractul `IClock`, comenzile și query-urile MediatR pentru hărți, contractul repository-ului și snapshot-ul versionat. Depinde de Domain. |
| `backend/src/AeroSynapse.Infrastructure` | Ceasul UTC, repository-ul de hărți și contextul Entity Framework Core cu SQLite. Depinde de Application și Domain. |
| `backend/src/AeroSynapse.API` | Pornirea aplicației, compunerea dependențelor, health și endpoint-urile hărților pentru Development. Depinde de Application și Infrastructure. |
| `backend/tests/AeroSynapse.Tests` | Teste xUnit pentru domeniu, API și persistența hărților, inclusiv restart, concurență, constrângeri SQL și restricționarea rutelor după mediu. |

## Frontend

Clientul Angular standalone folosește PrimeNG/Aura și Three.js. Include navigare responsivă, dashboard-ul Control Tower, selecția scenariilor demonstrative, vizualizare 3D cu rotire și zoom, stări de încărcare/eroare și retry. Datele JSON sunt locale; frontend-ul poate fi explorat fără pornirea backendului.

Din rădăcina repository-ului, cu Node.js 22.12+ sau Node.js 24 LTS și npm:

```sh
cd AeroSynapse.Client
npm ci
npm start
```

Deschide `http://localhost:4200/control-tower` sau `http://localhost:4200/scenario`. Pentru verificare, din directorul clientului:

```sh
npm test -- --watch=false
npm run build
```

Detalii despre organizare, fixture-uri și gestionarea resurselor 3D: [AeroSynapse.Client/README.md](AeroSynapse.Client/README.md).

## Pornire backend

Instalează SDK-ul .NET indicat în `global.json` (10.0.301 sau un feature band compatibil din .NET 10). Rulează din rădăcina repository-ului:

```sh
dotnet restore AeroSynapse.sln
dotnet run --project backend/src/AeroSynapse.API --launch-profile Development
```

API-ul pornește la `http://localhost:5080`. Verificare:

```sh
curl http://localhost:5080/health
```

Răspuns HTTP 200, cu ora UTC curentă:

```json
{"status":"Healthy","timestampUtc":"2026-10-30T12:00:00+00:00"}
```

## API pentru hărți

Rutele de mai jos sunt disponibile doar în mediul `Development`, pentru lucrul local, fără autentificare. La pornirea acestui mediu, aplicația creează automat schema SQLite. În celelalte medii, inclusiv `Production` și `Staging`, aceste rute răspund cu 404, iar aplicația nu deschide baza de date. `GET /health` este disponibil în toate mediile.

| Metodă și rută | Răspuns |
| --- | --- |
| `POST /api/airports` | 201 cu snapshot-ul hărții și antetul `Location`; 400 pentru JSON invalid; 422 cu `issues` pentru erori de domeniu; 409 pentru ID-uri deja folosite. |
| `GET /api/airports` | 200 cu cel mult 100 de aeroporturi, ordonate după nume și ID. |
| `GET /api/airports/{airportId}/snapshot` | 200 cu snapshot-ul versionat sau 404 dacă aeroportul nu există. |
| `GET /api/airports/{airportId}/navigation` | 200 cu graful de navigație, 404 dacă aeroportul nu există sau 422 cu `issues` dacă harta depășește limitele de navigație. |

Exemplu de creare și listare, rulat din rădăcina repository-ului:

```sh
curl -i -H "Content-Type: application/json" --data-binary "@backend/examples/airport-map.json" http://localhost:5080/api/airports
curl http://localhost:5080/api/airports
```

Pentru citirea hărții, folosește URL-ul din antetul `Location`. Enumerările din JSON sunt șiruri, de exemplu `"Entrance"`; valorile numerice pentru enum și câmpurile necunoscute sunt respinse cu 400. Coordonatele nefinite sunt respinse cu 422 și detalii despre câmp. Repetarea unei cereri de creare cu aceleași ID-uri produce 409.

## Verificare

```sh
dotnet build AeroSynapse.sln --configuration Release
dotnet test AeroSynapse.sln --configuration Release --no-build
```

Validarea hărții acoperă unicitatea ID-urilor, referințele dintre obiecte, capacități și rate pozitive, coordonate finite, conectivitatea rutelor și disponibilitatea checkpoint-urilor. Exemplul sintetic folosit de teste este în `backend/examples/airport-map.json`.

## Persistența hărților

Componenta de persistență este disponibilă prin MediatR, în namespace-ul `AeroSynapse.Application.Airports`:

| Cerere | Comportament |
| --- | --- |
| `CreateAirportMapCommand` | Validează harta și salvează toate obiectele într-o tranzacție. Returnează snapshot-ul cu eventualele avertismente. |
| `GetAirportSnapshotQuery` | Încarcă harta după ID-ul aeroportului și recalculează validările. Returnează `null` dacă aeroportul nu există. |
| `ListAirportsQuery` | Returnează cel mult 100 de aeroporturi, ordonate după nume și ID. |

`AddInfrastructure(connectionString)` configurează SQLite și repository-ul scoped. Valoarea implicită a extensiei este `Data Source=aerosynapse.db`. API-ul folosește `ConnectionStrings:AirportMaps` din configurație, cu fallback `Data Source=airport-maps.db`; poți suprascrie această valoare prin variabila de mediu `ConnectionStrings__AirportMaps`. În `Development`, gazda creează schema prin `AirportMapDbContext.Database.EnsureCreatedAsync()` într-un scope. Testele folosesc baze temporare separate.

Hărțile cu erori sunt respinse prin `AirportMapValidationException`, iar reutilizarea unui ID existent, inclusiv între tipuri diferite de obiecte, produce `AirportMapConflictException`. Tranzacțiile împiedică salvarea parțială, iar constrângerile SQL protejează referințele, capacitățile, ratele și coordonatele și în cazul scrierilor directe.

## Graf de navigație

`NavigationGraphImporter.FromMap(map)` convertește o hartă într-un `NavigationConfig` versiunea `1.0`, fără acces la baza de date sau servicii externe. Păstrează ID-urile, coordonatele în metri, direcția și durata rutelor, precum și legăturile dintre checkpoint-uri, porți și zone. Distanțele muchiilor sunt calculate din coordonatele 3D.

Zonele și muchiile indisponibile rămân în graf cu `IsActive = false`. Disponibilitatea ține cont de aeroport, terminal, etaj, checkpoint-uri, porți și resurse, inclusiv zona în care se află resursele. Scările și muchiile care le ating au `IsStepFree = false`. Importul generează profilurile `solo`, `family`, `business` și `tour`.

Importul verifică structura hărții și limitele contractului de navigație: 1–2000 de zone, maximum 10000 de rute, 1000 de checkpoint-uri și 1000 de porți, nume de zone de maximum 160 de caractere, coordonate între −1000000 și 1000000 metri, distanțe de maximum 1000000 metri și durate între 0,001 și 604800 de secunde. Valorile invalide produc `AirportMapValidationException` cu detalii despre câmp.

O hartă poate fi proiectată în graf chiar dacă are zone inactive sau componente deconectate. Importul folosește `AirportMapValidator.Validate(map, validateNavigation: false)` pentru verificările structurale; salvarea hărților folosește în continuare validarea completă implicită. Graful unei hărți salvate este disponibil prin `GET /api/airports/{airportId}/navigation`, doar în `Development`. Exportul nu modifică harta; o hartă validă pentru stocare poate primi 422 la export dacă depășește limitele mai stricte ale navigației. Calculul traseelor va fi integrat separat.

## Scop

Backendul include modelele, validarea, stocarea hărților, conversia lor în grafuri de navigație și endpoint-urile HTTP pentru dezvoltare locală. Frontend-ul include Control Tower și scenarii cu date demonstrative locale; conectarea lui la API, autentificarea, simularea și serviciul Python vor fi integrate separat. Rutele hărților vor putea fi activate în producție după integrarea autentificării și autorizării.

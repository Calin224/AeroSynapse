# AeroSynapse

Backend AeroSynapse: .NET 10, ASP.NET Core Minimal API, MediatR și persistență SQLite pentru hărțile aeroporturilor.

## Arhitectură

| Proiect | Responsabilitate |
| --- | --- |
| `backend/src/AeroSynapse.Domain` | Modelul aeroportului și validarea hărții: terminale, etaje, zone, rute, checkpoint-uri, porți și resurse. Fără dependențe externe. |
| `backend/src/AeroSynapse.Application` | Query-ul de sănătate, contractul `IClock`, comenzile și query-urile MediatR pentru hărți, contractul repository-ului și snapshot-ul versionat. Depinde de Domain. |
| `backend/src/AeroSynapse.Infrastructure` | Ceasul UTC, repository-ul de hărți și contextul Entity Framework Core cu SQLite. Depinde de Application și Domain. |
| `backend/src/AeroSynapse.API` | Pornirea aplicației, compunerea dependențelor și endpoint-ul HTTP. Depinde de Application și Infrastructure. |
| `backend/tests/AeroSynapse.Tests` | Teste xUnit pentru domeniu, health API și persistența hărților, inclusiv restart, concurență și constrângeri SQL. |

## Pornire

Instalează SDK-ul .NET indicat în `global.json` (10.0.301 sau un feature band compatibil din .NET 10). Rulează din rădăcina repository-ului:

```sh
dotnet restore AeroSynapse.sln
dotnet run --project backend/src/AeroSynapse.API
```

API-ul pornește la `http://localhost:5080`. Verificare:

```sh
curl http://localhost:5080/health
```

Răspuns HTTP 200, cu ora UTC curentă:

```json
{"status":"Healthy","timestampUtc":"2026-10-30T12:00:00+00:00"}
```

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

`AddInfrastructure(connectionString)` configurează SQLite și repository-ul scoped. Valoarea implicită este `Data Source=aerosynapse.db`. Înainte de prima utilizare a repository-ului, gazda trebuie să creeze schema prin `AirportMapDbContext.Database.EnsureCreatedAsync()` într-un scope; testele fac acest lucru pe baze temporare separate. Pornirea API-ului și verificarea `/health` nu creează baza de date.

Hărțile cu erori sunt respinse prin `AirportMapValidationException`, iar reutilizarea unui ID existent, inclusiv între tipuri diferite de obiecte, produce `AirportMapConflictException`. Tranzacțiile împiedică salvarea parțială, iar constrângerile SQL protejează referințele, capacitățile, ratele și coordonatele și în cazul scrierilor directe.

## Scop

API-ul HTTP expune în continuare `GET /health`; operațiile de persistență nu au încă endpoint-uri HTTP. Acest pas include modelele, validarea și stocarea hărților. Autentificarea, simularea, serviciul Python și frontend-ul vor fi integrate separat.

# AeroSynapse

Primul pas al backendului, extras din arhitectura proiectului AeroSynapse AI: .NET 10, ASP.NET Core Minimal API și MediatR.

## Arhitectură

| Proiect | Responsabilitate |
| --- | --- |
| `backend/src/AeroSynapse.Domain` | Modelul aeroportului și validarea hărții: terminale, etaje, zone, rute, checkpoint-uri, porți și resurse. Fără dependențe externe. |
| `backend/src/AeroSynapse.Application` | Query-ul de sănătate, handler-ul MediatR, contractul `IClock` și snapshot-ul versionat al hărții. Depinde de Domain. |
| `backend/src/AeroSynapse.Infrastructure` | Implementarea ceasului UTC și înregistrarea sa în dependency injection. Depinde de Application și Domain. |
| `backend/src/AeroSynapse.API` | Pornirea aplicației, compunerea dependențelor și endpoint-ul HTTP. Depinde de Application și Infrastructure. |
| `backend/tests/AeroSynapse.Tests` | Teste xUnit pentru validările domeniului și integrarea API → MediatR → ceas. |

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

## Scopul acestui commit

API-ul expune doar `GET /health`. Modelele, validatorul și snapshot-ul hărții sunt reutilizate din proiectul original; nu sunt încă expuse prin endpoint-uri. Acest pas nu include persistență, autentificare, simulare, serviciul Python sau frontend. Pornirea nu necesită bază de date ori alte servicii externe. Nu include configurația privată sau istoricul Git al proiectului original.

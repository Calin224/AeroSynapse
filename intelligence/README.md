# AeroSynapse Intelligence

Serviciu Python 3.13 / FastAPI, gestionat cu [uv](https://docs.astral.sh/uv/).
Instalează uv folosind [instrucțiunile oficiale](https://docs.astral.sh/uv/getting-started/installation/).

## Instalare, pornire și teste

Din rădăcina repository-ului:

```sh
cd intelligence
uv sync --locked
uv run --locked uvicorn app.main:app --host 127.0.0.1 --port 8000
```

uv gestionează Python-ul din `.python-version` și mediul izolat `.venv`.
`uv.lock` se include în Git; `--locked` verifică sincronizarea cu `pyproject.toml`.

Teste, din același director:

```sh
uv run --locked pytest
```

## Endpoint-uri și exemple JSON

- `GET /health` → 200, `{"status": "Healthy"}`.
- `GET /health/ready` → 200 standalone; în Docker verifică PostgreSQL și răspunde
  503 dacă baza nu este disponibilă. Răspunsurile nu includ credențiale.
- `GET /docs` → documentație Swagger UI.
- `GET /openapi.json` → contractele OpenAPI.
- `POST /simulation/validate` → 200 pentru un `SimulationRequest` valid, 422 pentru input invalid.

Contractul inițial cere `duration_minutes` și `passenger_count`, întregi strict
pozitivi. `seed` este un întreg nenegativ, implicit 42. Câmpurile necunoscute,
valorile de tip greșit și JSON-ul invalid sunt respinse.

```sh
curl -i http://127.0.0.1:8000/health
curl -i http://127.0.0.1:8000/simulation/validate -H "Content-Type: application/json" --data-binary @examples/simulation-request.json
curl -i http://127.0.0.1:8000/simulation/validate -H "Content-Type: application/json" --data-binary @examples/simulation-request-invalid.json
```

În Windows PowerShell folosește `curl.exe`. Exemplul răspunsului 422 este în
`examples/validation-error.json`; același contract este descris în OpenAPI.
Endpoint-ul validează și returnează cererea. Algoritmii vor fi implementați
în stories dedicate.

## Generare deterministă pe zbor

`POST /passengers/v1/generate?offset=0&limit=100` generează populația logică
folosind NumPy 2.5.4 / PCG64 și returnează o pagină explicită, totaluri pe zbor,
subseturi, estimări de supraîncărcare și fingerprint SHA-256 al întregii liste.
`POST /simulation/passenger-run` folosește sosirile și serviciile generate la
checkpoint-ul fiecărui pasager. Profilurile sunt acceptate și de execuția
durabilă `/simulation/runs`; contractul înghețat `/simulation/v1/run` rămâne
strict. Configurația și exemplele sunt în
[ghidul de algoritmi](../docs/product/algorithms-and-assumptions.md).

## Rutare operațională

`POST /navigation/v1/preview` planifică traseele către porțile asignate;
`POST /navigation/v1/route` verifică o pereche de zone/profil. Dijkstra minimizează
timpul cu filtre pentru închideri, etichete și acces fără scări. Endpoint-ul
`POST /simulation/navigation-run` și execuția durabilă `/simulation/runs` adaugă
coada și serviciul la mers pentru evaluarea boarding. Raportul returnează erori
explicite per pasager și waypoint-uri independente de interpolarea renderer-ului.
Vezi [ghidul de algoritmi](../docs/product/algorithms-and-assumptions.md) și exemplele comune din
`contracts/navigation/v1/examples`.

## Fluxuri alternative

Câmpul opțional `flow` adaugă tipuri de pasageri, etape și blocări.
`app/simulation/flows.py` rulează traseele cu SimPy; rulările cu `flow` păstrează
rezultatele v1 pe checkpoint, iar `POST /passengers/v1/flows` returnează
statisticile agregate pe etapă și tip. Detalii în
[ghidul de algoritmi](../docs/product/algorithms-and-assumptions.md).

## Predictor ML de cercetare

Creează directorul de rapoarte înainte de evaluare: `python -c "from pathlib import Path; Path('../artifacts').mkdir(exist_ok=True)"`.

`python -m app.forecast.model --output ../artifacts/us038-evaluation.json` regenerează
datasetul sintetic versionat, antrenează și evaluează modelele scikit-learn față de
baseline-uri. `POST /simulation/queue-prediction` primește `QueueForecastRequest` și
returnează `QueuePredictionResult` (`contracts/forecast/v1`). Cu `QUEUE_ML_PREDICTOR=enabled`,
modelul se antrenează în fundal la pornire; până atunci, ca și când predictorul
este dezactivat, răspunsul este forecast-ul determinist. Detalii în
[ghidul de algoritmi](../docs/product/algorithms-and-assumptions.md).

## Anomalii față de variația normală

`POST /anomaly/detect` primește seriile agregate ale unei rulări și ale replicărilor
Monte Carlo (`contracts/anomaly/v1`). Returnează banda empirică, p-valorile, alertele pe
ferestre cu explicația Shapley a factorilor și calibrarea leave-one-out.
`POST /anomaly/evaluation` și `/anomaly/evaluation/case` expun FP/FN pe setul de test
sintetic, construit o singură dată în fundal. Regenerarea raportului se face cu
`python -m app.anomaly.evaluation --output ../artifacts/us039-evaluation.json`. Detalii în
[ghidul de algoritmi](../docs/product/algorithms-and-assumptions.md).

## Structură și logging

Motorul KPI adaugă `app/simulation/metrics.py` și `POST /simulation/kpi-run`.
Motorul `simpy-kpi-1.0` capturează intervalul 1–5 minute, KPI-urile etichetate
și reconcilierea populației în `kpiResult`. Wait-urile folosesc numai servicii
terminate, P95 folosește nearest rank, iar utilizarea integrează ocuparea reală.
Vezi [calculul KPI](app/simulation/metrics.py).

Motorul SimPy de procese este în `app/simulation/processes.py`. Cererile cu
`engine: "simpy-1.0"` folosesc procese SimPy și resurse cu capacitate simultană
limitată; `/simulation/simpy-run` acceptă și zero pasageri. Raportul `processResult`
păstrează stările și timestamp-urile observate până la orizont. Dispatcher-ul
păstrează motorul legacy pentru cererile istorice fără markerul de versiune.
Vezi [ghidul de algoritmi](../docs/product/algorithms-and-assumptions.md).

Modulele sunt în `app/simulation`, `app/optimization`, `app/forecast` și `app/agents`.
SimPy și OR-Tools sunt instalate și fixate în lockfile.

Logurile aplicației folosesc consola, timestamp UTC, nivel și numele logger-ului.
Nivelul implicit este INFO; variabila `LOG_LEVEL` permite DEBUG, INFO, WARNING,
ERROR sau CRITICAL. Se loghează validările și erorile de validare, fără corpul cererii.

Contractele de simulare primesc date prin JSON și funcționează independent de DB-ul
aplicației. Doar verificarea de readiness folosește PostgreSQL când `POSTGRES_HOST`
este configurat (în Compose); rularea standalone nu cere o bază de date.

## Integrare Docker

Din rădăcina repository-ului: `docker compose up --build --detach --wait`.
Nginx expune serviciul pe http://localhost:8080/intelligence/docs și
http://localhost:8080/intelligence/simulation/validate. `ROOT_PATH=/intelligence`
păstrează corecte URL-urile OpenAPI când serviciul rulează prin proxy.

`pyproject.toml` și `uv.lock` definesc dependențele. Docker instalează exportul
fixat `requirements.lock`, inclusiv SimPy, OR-Tools și psycopg. După modificarea
dependențelor, din acest director:

```sh
uv lock
uv export --locked --no-dev --format requirements-txt --no-hashes --no-emit-project --output-file requirements.lock
uv export --locked --only-group dev --format requirements-txt --no-hashes --no-emit-project --output-file requirements-dev.lock
```

Testele în Docker sunt descrise în [ghidul comun](../docs/docker.md).

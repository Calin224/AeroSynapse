# Modelul călătoriei și întârzieri pe durata zilei

Acest ghid documentează motorul `journey` și API-urile sale. Configurația
`ScenarioInputs.Journey` stabilește capacitatea controlului de pașapoarte,
serviciul de îmbarcare, sosirile aeronavelor, rotațiile și legăturile.
Evenimentele `flight_change` introduc întârzieri în simulare. Raportul se citește
prin `GET /runs/{runId}/journey`; secțiunea dedicată „Călătoria completă” a fost
eliminată din Operations.

## Etape și conservarea pasagerilor

Pasagerii de origine trec prin check-in dacă au bagaj de cală și dacă etapa este
configurată, securitate, pașapoarte dacă zborul o cere, mers și coada de
îmbarcare. Check-in-ul, securitatea, valurile suplimentare și programul de
personal reutilizează motorul existent. Pașapoartele au o coadă FCFS comună;
fiecare poartă are propria resursă FCFS de îmbarcare. Duratele celor două
servicii noi sunt constante și configurabile. Zero personal blochează etapa.

O legătură rezervă pasageri din cohorta modelată a zborului de ieșire; nu îi
adaugă din nou. Totalul rezervat nu poate depăși cohorta de ieșire sau
capacitatea aeronavei de intrare. Pasagerii în tranzit apar la sosirea efectivă
a zborului de intrare, continuă airside, trec prin pașapoarte dacă legătura sau
zborul de ieșire o cere și parcurg timpul de transfer declarat. Modelul nu
simulează un aeroport extern ori zborul anterior al fiecărui pasager.

Îmbarcat înseamnă serviciu **terminat** până la închidere inclusiv. Sosirea la
poartă nu este suficientă. La orizont, serviciile neterminate păstrează timpi
`null`; planul poate conține plecări ulterioare orizontului. O schimbare de
poartă implică mers fizic pentru pasagerii aflați în așteptare. Navigația
configurată folosește graful, profilul de acces și închiderile de coridoare;
fără traseu, pasagerul așteaptă. O închidere nu întrerupe mersul deja început.
Primul transfer airside folosește timpul explicit al legăturii.

`at_risk` este un indicator determinist bazat pe timpul de transfer/serviciu
rămas estimat și marja configurată până la închiderea îmbarcării. Nu este o
probabilitate calibrată și nu anticipează întreaga așteptare în cozi.
`missed` apare la închiderea îmbarcării pentru pasagerii încă neîmbarcați;
rezultatul efectiv este calculat din execuția cozilor.

## Replanificarea porților cu CP-SAT

La început și după fiecare întârziere, OR-Tools planifică întreaga zi capturată.
Ocuparea porții este intervalul semi-deschis `[sosire, plecare)`, incluzând
turnaround-ul înaintea îmbarcării. Porțile eligibile sunt în același terminal,
au capacitatea necesară și rute de la controalele care servesc zborul. Cu
navigație, trebuie să aibă și legătură la graf; accesul efectiv se verifică
pentru fiecare pasager.

Constrângerile interzic suprapunerea aeronavelor la aceeași poartă și impun
`sosire următoare ≥ plecare precedentă + interval rotație`. Obiectivul
minimizează întâi suma întârzierilor, apoi schimbările de poartă, apoi costul
ordinii stabile a porților. O întârziere nouă se adaugă plecării curente;
întârzierile propagate nu sunt recuperate automat ulterior. Dacă nu există o
poartă liberă, se întârzie operațiunile încă neîncepute. Rotațiile pot transmite
efectul până la zborurile de după-amiază sau seară.

Momentele strict anterioare evenimentului sunt imuabile. O aeronavă deja sosită
își păstrează poarta. O întârziere după închiderea îmbarcării poate amâna
plecarea, fără redeschiderea îmbarcării. Evenimentele de la minutul T se aplică
înaintea operațiunilor din T; după plecarea efectivă, întârzierea este respinsă
cu `event_conflict`. Conflictele imposibil de rezolvat produc o rulare eșuată,
fără raport parțial prezentat ca valid.

Solverul folosește secunde întregi, un worker, seed 0 și un buget determinist
de 5 unități OR-Tools. `optimal` și `feasible` sunt returnate distinct;
`feasible` nu garantează optimul. Eșecurile disting `gate_plan_infeasible`,
`gate_plan_timeout` și `gate_plan_invalid`. Planificarea este limitată la
minutul 30240; bugetele protejează execuția, nu promit rezolvarea oricărui orar.

## Asistentul de alocare a porților

Cererea API precizează întârzierea raportată: zborul, câte minute și minutul zilei.
`POST /scenarios/{id}/gate-advice?revision=R` reconstruiește planul cunoscut atunci
(planul inițial plus întârzierile deja programate până la acel minut, în ordinea
rulării) și cere același solver CP-SAT planul care absoarbe noua întârziere. Pentru
comparație, solverul caută și cel mai bun plan în care fiecare zbor își păstrează
poarta; lipsa lui este semnalată în răspuns, nu înlocuită cu un număr.

Răspunsul vine în câteva secunde și nu salvează nimic. .NET verifică independent
că fiecare zbor apare o dată, că porțile sunt eligibile, că două zboruri nu ocupă
aceeași poartă simultan, că întârzierea raportată e aplicată și că un avion deja
sosit își păstrează poarta. Pentru aplicare, se salvează întârzierea ca eveniment
`flight_change`; rularea următoare reface exact aceeași replanificare și calculează
efectul asupra cozilor și îmbarcării. „Timp real” înseamnă aici timpul operării în
digital twin: aplicația nu primește date live de la un aeroport.

## Contract, persistență și compatibilitate

Configurația opțională `ScenarioInputs.Journey` este salvată în documentul
scenariului prin PUT-ul existent și revizia sa. Cererea capturată conține
`journey` și contractul `contracts/journeys/v1`; execuția folosește în continuare
endpoint-ul KPI, cu proveniența `journey-1.0` și build-ul care include OR-Tools.
Rezultatul verificat se persistă atomic ca snapshot `journeys/v1`, împreună
cu rularea. Nu există migrare SQL sau dependență nouă.

`GET /runs/{runId}/journey?offset=0&limit=100` returnează planurile, snapshot-urile
și pagina de pasageri. Limita este 1–100000; ruta cere autorizarea existentă
pentru consultare. Prefixul public Compose este `/api`. Raportul folosește
etichetele din captura rulării; editarea scenariului nu rescrie istoricul.
Trace-ul upstream este păstrat pentru validarea corelației, fără a fi expus ca
rezultat al îmbarcării complete. Comparațiile și optimizările care cer vechile
rezultate KPI nu substituie acest nou raport.

Fără `journey`, regulile și rezultatele existente rămân neschimbate. Nu se
combină cu fluxurile alternative. Limite suplimentare: 200 zboruri, 100
întârzieri programate, 1000 legături și cel mult 10 milioane de observații
`populație × momente snapshot`. Valurile suplimentare trebuie să refere un
zbor. Ciclurile rotațiilor, reutilizarea aceleiași aeronave pentru două rotații
succesoare și cohortele suprarezervate sunt respinse la salvare
și la frontiera Python/.NET.

## Dovezi automate

- `intelligence/tests/test_gate_advice.py`: mutarea în locul întârzierii propagate,
  reluarea întârzierilor programate, planul fără mutări, 422 și conflictul 409.
- `GateAdvisorClientTests`: respingerea planurilor care încalcă regulile și
  verificarea răspunsului serviciului de planificare.

- `intelligence/tests/test_journeys.py`: cozi, personal zero, transferuri,
  risc și pierdere de legătură, mers după reasignare, rotații până după-amiază,
  evenimente la limite, orizont, navigație și reproductibilitate.
- `JourneyTests`: aceleași exemple executate în Python și validate în .NET;
  respingerea serviciilor fabricate, a istoricului rescris și a agregatelor
  incompatibile cu pasagerii.
- `JourneyApiTests`: API .NET, motor Python și PostgreSQL reale, persistență,
  paginare, autorizare și păstrarea capturii după editarea scenariului.

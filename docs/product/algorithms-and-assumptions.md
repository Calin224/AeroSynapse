# Algoritmi, ipoteze și limitele concluziilor

[Înapoi la documentația produsului](../README.md). Owner: **Simulation lead**; **QA lead** verifică reproducerea și criteriile, iar **Product owner** stabilește scopul concluziilor.

Toate modelele curente sunt demonstrative. Geometria 3D, orarul, capacitățile și comportamentele pasagerilor nu sunt măsurători autorizate ale unui aeroport. Un output numeric calculat corect poate fi nepotrivit pentru realitate dacă ipotezele nu corespund. Nici solverul, nici un model LLM nu certifică o recomandare.

## Catalogul calculelor

Programarea zborurilor, evenimentelor și fluxurilor folosește date și ore absolute: editorul afișează `HH:MM` în fusul orar declarat al scenariului, iar API-ul primește timestamp-uri UTC (`departureAtUtc`, `occurredAtUtc`, `startsAtUtc`, `endsAtUtc`). Data distinge programările de după miezul nopții. Ora plecării este reperul pentru îmbarcare și sosirile pasagerilor; câmpurile de programare nu cer minute scurse de la începutul simulării. Orele locale inexistente sau ambigue la schimbarea orei sunt respinse; un timestamp existent își păstrează instantul. Duratele, întârzierile și separarea dintre plecări rămân durate în minute. Contractele interne istorice pentru distribuțiile pasagerilor convertesc timestamp-urile în offseturi numai la limita cu motorul.

Scenariile noi din Scenario Studio păstrează `minimumDepartureSpacingMinutes = 60`: între două plecări active, inclusiv întârzierile configurate, de la aceeași poartă trebuie să existe cel puțin o oră; porți diferite pot avea plecări simultane. Editorul distanțează zborurile adăugate, iar validarea din interfață și API respinge orarele prea apropiate. Toate presetările păstrează regula de o oră; presetările de cinci zboruri durează șase ore, presetul de opt ore are opt zboruri, iar „Vârf de trafic” trimite cinci zboruri în 40 de minute, fiecare de la altă poartă. Scenariile istorice fără această proprietate își păstrează regulile originale.

Intervalul de o oră este o regulă solicitată pentru această aplicație, nu un standard aeroportuar universal. De exemplu, [declarația oficială de capacitate Schiphol S26](https://slotcoordination.nl/wp-content/uploads/2025/10/AMS-Capacity-Declaration-Summer-2026.pdf), pagina 3, permite până la 74 de plecări pe oră în configurația de vârf cu două piste de decolare. Modelul aplicației nu simulează separarea ATC pe piste sau restricțiile wake turbulence.

| Subsistem / sursă | Metodă implementată | Output și limită principală |
| --- | --- | --- |
| [Generare pasageri](../../intelligence/app/passengers) | NumPy, seed local, populații pe zbor, distribuții declarate ale sosirii/serviciului/mersului, grupuri | Populație sintetică deterministă; distribuțiile sunt inputuri/ipoteze, nu profiluri reale estimate |
| [Navigație](../../intelligence/app/navigation) | Dijkstra pe graful capturat, filtre și rute închise | Traseu minim pentru costurile declarate; pasagerul fără rută este `unreachable`, fără teleportare |
| [Fluxuri](../../intelligence/app/simulation/flows.py) | Etape de procesare și resurse pentru profiluri de flux | Nu toate versiunile motorului emit aceeași serie KPI; motorul pe etape are propriul output |
| [Procese SimPy](../../intelligence/app/simulation/processes.py) | Simulare cu evenimente discrete, cozi FCFS, resurse și timp virtual | Serviciul terminat se observă numai după timeout-ul virtual; la orizont pot rămâne pasageri neterminați |
| [KPI](../../intelligence/app/simulation/metrics.py) | Observație la evenimente și snapshot-uri la cadența capturată | Cohorte/numitori expliciți; lipsa observațiilor produce `null`, nu zero inventat |
| [Perturbații](../../intelligence/app/simulation/events.py) | Aplicare deterministă a evenimentelor capturate la minutele lor | Surge, închideri și reduceri conforme tipului; efecte limitate la simulator |
| [Călătorii și porți](../../intelligence/app/simulation/journeys.py) | Cozi SimPy pentru pașapoarte/îmbarcare, transferuri și CP-SAT repetat pentru porți și rotații ([ghid](../passenger-journey-delays.md)) | Îmbarcare efectivă și întârzieri pe durata zilei; riscul legăturilor este o marjă estimată, nu probabilitate calibrată |
| [Personal](../../intelligence/app/optimization/staffing.py) | OR-Tools CP-SAT, variabile întregi, bugete și limite, obiectiv deficit + schimbare | Propunere statică; scorul proxy nu este direct timpul real de așteptare |
| [Personal pe ture](../../intelligence/app/optimization/shifts.py) | CP-SAT pe om × control × interval de 15 min: ture contigue, pauze, mutări cu timp pierdut, coadă reportată între intervale ([detalii](#personal-pe-ture-la-15-minute)) | Program de planificare; toți oamenii presupuși disponibili, o tură pe om; efectul se verifică în SimPy |
| Evaluare candidat | Aceleași intrări/populație/seed, configurație alternativă, reevaluare SimPy | Îmbunătățirea se acceptă numai din KPI calculați și reguli declarate; poate fi neconcludentă |
| [Resurse integrate / Pareto](../../intelligence/app/optimization) | CP-SAT și evaluări multiobiectiv, configurații distincte și filtrare Pareto | Frontieră aproximativă a candidaților evaluați, nu enumerarea tuturor soluțiilor posibile |
| Batch / Monte Carlo | Replicări pe aceeași listă explicită de seed-uri pentru fiecare variantă | Variație empirică și diferențe pereche; eșecurile sunt vizibile și excluderea este declarată |
| [Forecast fluid](../../intelligence/app/simulation/forecast.py) | `Q[t+1] = max(0, Q[t] + arrivals[t] - capacity[t])` | Proiecție deterministă la 15–30 minute simulate; poate fi fracționară și nu este numărătoare observată |
| [ML forecast](../../intelligence/app/forecast) | Modele scikit-learn și selecție comparată cu baseline-uri pe dataset sintetic, separare pe scenariu | Scoruri pe simulator/datasetul declarat; nu acuratețe transferată unui aeroport real |
| [Anomalii](../../intelligence/app/anomaly) | Benzi empirice din replicări, depășiri repetate și praguri | Cu `n` replicări, rezoluția p-valorii este `1/(n+1)`; minutele nu sunt independente |
| [Copilot](../../backend/src/AeroSynapse.Infrastructure/Execution/CopilotService.cs) | LLM opțional selectează o temă; serverul construiește răspuns din citări validate | LLM-ul nu produce cifre finale, nu aprobă și nu invocă unelte arbitrare; date insuficiente → limitare |
| Module opționale | Bagaje, airside și energie au modele/contracte separate | Nu sunt un model fizic integrat și validat; energia folosește coeficienți ipotetici |

## Personal: ce optimizează CP-SAT

Pentru fiecare checkpoint `c`, `x[c]` este personalul întreg propus. Un control închis are zero; unul deschis respectă minimul, maximul și limita fizică declarată. Sumele respectă bugetul total și bugetul terminalului. Nu se presupune că personalul se poate muta între terminale.

```text
min Σc [max(0, demandMilli[c] - capacityMilli[c] × x[c])
        + changeCost[c] × abs(x[c] - currentStaff[c])]
```

`demandMilli` și `capacityMilli` sunt cuantizate în miimi de pasager/minut. Limita fizică este un input explicit al operatorului, nu o valoare dedusă din aspectul modelului 3D. Un worker și un seed fix limitează variația de căutare; bugetul de timp și starea solverului rămân parte a rezultatului. `optimal`, `feasible`, `infeasible`, `timeout` și `invalid_model` au semnificații distincte. `feasible` nu înseamnă optim demonstrat, iar lipsa unei soluții nu este completată cu o alocare implicită.

Cererea de planificare și evenimentele ulterioare pot avea ipoteze diferite; de aceea propunerea se evaluează în simulator. Un candidat care reduce proxy-ul CP-SAT poate să nu îmbunătățească KPI-ul ales în SimPy. Aprobarea umană este un pas separat, legat de scenariu/revizie și de dovezile păstrate.

## Personal pe ture, la 15 minute

Pentru o zi de cel puțin 120 de minute, același set de restricții poate produce un plan care spune cine lucrează, unde și când, pe intervale de 15 minute. Cererea pe interval vine din aceeași populație eșantionată ca vârful pe minut (`arrivalsPerQuarterHour` din generator); capacitatea pe operator este cea a modelului fix. Sunt planificați primii `availableStaff` oameni din layout, fiecare doar la controalele terminalului său.

Reguli (`ShiftRules`): o singură tură contiguă pe om, de `minShiftSlots`–`maxShiftSlots`, care începe și se termină lucrând; în orice `breakAfterSlots + 1` intervale consecutive un om lucrează cel mult `breakAfterSlots`; o mutare (două intervale lucrate la rând la controale diferite) costă `transferMinutes` de serviciu la destinație, cu cel mult `maxMovesPerHour` mutări pe oră. Minimul, maximul, limita fizică și `closed` se aplică la fiecare interval; `requiredOpen` înseamnă „ocupat în fiecare interval în care vin pasageri”, altfel o zi lungă ar ține oameni la controale unde nu vine nimeni.

```text
cap[c]     = floor(capacityMilliPerMinute[c] · targetUtilizationPercent / 100)
queue[c,t] = max(0, queue[c,t-1] + 1000·arrivals[c,t] - cap[c]·(15·staff[c,t] - transferMinutes·movers[c,t]))
min queueWeight·Σ queue + staffSlotCost·intervale de tură + moveCost·mutări
```

Coada se reportează între intervale, deci întărirea înainte de val contează. Modelul e un flux și nu vede cozile produse de variabilitate: la 90% ocupare un singur operator are deja cozi de ordinul a 9 minute. De aceea planul folosește doar `targetUtilizationPercent` din capacitate (implicit 75%, editabil). Oamenii aceluiași terminal sunt ordonați după începutul turei, ceea ce elimină planurile simetrice; solverul are un worker, seed fix și 12 secunde pe plan. Python și .NET (`ShiftPlanValidation`) recalculează independent regulile, personalul pe interval, cozile și scorul din ture.

Evaluarea pe ture simulează pe aceeași populație și seed planul de azi, cel mai bun plan fix, planul pe ture și același plan fără mutări. Turele intră în scenariu ca `staffShifts` și în simulator ca pași de personal (`staffSchedules`); aprobarea le scrie în revizia nouă. Pe demo-ul „Zi de lucru · 8 ore” (1440 de pasageri la un control, 6 oameni), planul pe ture cu 4 oameni și pauze a redus așteptarea medie simulată de la 22 s la 6 s; planul fix ajunge la 1 s, dar ține 6 oameni 8 ore fără pauze, iar interfața îl numește așa.

Limite: toți oamenii sunt presupuși disponibili toată ziua, cu cel mult o tură; check-in-ul nu e planificat pe ture; pe un control cu program `staff_reduction` e respins, iar `resume` doar încheie o indisponibilitate.

## Bani economisiți

Recomandarea AI din simulare și planul recomandat din fluxul de decizie arată un singur rând cu economia (sau costul) estimată în euro față de alocarea de azi ([calcul](../../AeroSynapse.Client/src/app/shared/ui/cost-estimate.ts)):

```text
economie = pasageri × (așteptare medie azi − cu planul) / 60 × valoarea orei de pasager
         − (ore de operator cu planul − ore azi) × costul orei de operator
```

| Valoare fixă | Sursă |
| --- | --- |
| 22,89 € / oră de pasager | [EUROCONTROL Standard Inputs v10.0.2](https://ansperformance.eu/economics/cba/standard-inputs/chapters/passenger_value_of_time.html), valoarea de referință UE25 pentru călătorii personale, prețuri 2025 (afaceri: 55,90 €) |
| 12,5 € / oră de operator | [Eurostat](https://ec.europa.eu/eurostat/web/products-eurostat-news/w/ddn-20250328-1), costul total al orei de muncă în România, 2024 (salariu și contribuții; media UE 33,5 €) |

Orele de operator sunt cele plătite: o tură se numără de la primul la ultimul minut, cu pauze; un plan fix ține oamenii pe toată durata simulării. Valorile nu se editează în interfață; sursele apar la trecerea mouse-ului peste rând. Un plan respins nu primește estimare. Pasagerii care nu mai riscă să piardă zborul nu intră în sumă: Dublin și Schiphol au despăgubit în 2022 pasagerii rămași la cozile de securitate (zbor nou, transport, cazare), fără un cost mediu public pe pasager.

## KPI și comparații corecte

Pentru snapshot-ul la minutul `t`, mean/median/P95 wait includ numai serviciile terminate până la `t`, cu așteptarea `startService-arrival`. P95 folosește nearest rank, fără interpolare. Throughput-ul ferestrei este numărul de servicii terminate în `(a,t]` împărțit la `t-a`. Utilizarea integrează minutele ocupate, raportate la capacitate × durată. Boarding risk folosește cohorta cu termen cunoscut; fără numitor valid este `null`. În modelul de procese, sosirea exact la închiderea boarding-ului este la timp.

La fiecare run, cererea modelată se reconciliază între nesosiți, în coadă, în serviciu, în mers, ajunși și fără traseu, conform motorului respectiv. Un surge poate adăuga cerere: nu se cere conservare față de populația inițială ignorând evenimentul. Persoanele neobservate la orizont nu se declară procesate. Vezi [calculul KPI](../../intelligence/app/simulation/metrics.py).

Pentru un KPI de minimizat, îmbunătățirea procentuală este `100 × (baseline-candidate)/baseline` numai dacă baseline-ul este valid și strict pozitiv. Pentru throughput direcția este inversă. Unitățile, cohorta, minutul, build-ul, layout-ul și populația trebuie să fie compatibile; două runId-uri distincte nu sunt singure suficiente. Un zero la baseline poate avea diferență absolută, fără procent. Datele lipsă și run-urile eșuate nu sunt zero.

Batch-ul folosește 1–25 seed-uri distincte și aceleași seed-uri pentru variante. Raportarea pereche folosește numai perechile valide, păstrează numărul și motivele excluderilor și arată variația. Nu se prezintă o cuantila empirică a câtorva seed-uri drept interval de încredere sau probabilitate calibrată pentru un aeroport real.

## Registru de ipoteze pentru validare

| ID | Ipoteză curentă | Ce ar trebui verificat cu stakeholderii |
| --- | --- | --- |
| A-01 | Sosirile și serviciile urmează distribuțiile declarate | Date autorizate agregate, variație pe interval/zbor, calibrare și test separat |
| A-02 | FCFS și capacitate nominală în modelul ales | Reguli reale de prioritate, pauze, schimburi, accesibilitate și variabilitatea servirii |
| A-02b | Un operator deservește o linie de control: 3 pasageri/min la CP-1 și CP-2, 2 la CP-3 (120–180/oră); scenariile create înainte de această calibrare păstrează layout-ul capturat cu 7–9/min | Debitul măsurat pe linie, câți agenți are o linie și cât variază cu bagajele, ora și profilul pasagerilor |
| A-03 | Graful/costurile și limitele fizice sunt fictive sau declarate | Plan aprobat, sensuri, accesibilitate și costuri autorizate; geometria vizuală nu ține loc de plan |
| A-04 | Boarding și alocarea controlului sunt cunoscute în snapshot | Semnificația timestamp-urilor, întârzieri și excepții operaționale |
| A-05 | Un seed și un build reproduc aceleași intrări/calcul | Verificarea `inputHash`, `resultHash`, `engineBuild`, lockfiles și regulilor de compatibilitate |
| A-06 | Coeficienții energiei și resursele airside/bagaje sunt demonstrative | Validări și scopuri separate; nu se adună metrici incompatibile într-un rezultat „real” |
| A-07 | Economia în euro folosește valori publice europene (EUROCONTROL, Eurostat), nu costurile aeroportului | Costul real al orei de agent de securitate, valoarea timpului pentru profilul de pasageri al aeroportului și costul unui zbor pierdut la control |
| A-08 | AI vede numai context restrâns și surse suficiente | Evaluări de grounding, injection, context străin, indisponibilitate și revizuire umană |

## Reproducere și model change

Păstrează configurația capturată, versiunea contractului, seed/listă de seed-uri, engine version/build, input/result hash, commit, lockfiles și imaginea release. Hash-ul nu certifică adevărul datelor. UUID-urile, timpii reali și durata CPU pot diferi chiar când rezultatul logic este stabil. Schimbarea distribuțiilor, formulelor, ordinii evenimentelor sau solverului trebuie să declare impactul și să ruleze testele potrivite; nu este o simplă optimizare vizuală.

Rendererul 2D/3D și sampling-ul animației consumă snapshot-uri persistate; nu rescriu matematica. KPI, hartă și tabel trebuie să folosească același run și aceeași selecție de timp. Testele automate acoperă invariants și regresii sintetice, iar [evaluarea real-world](algorithms-and-assumptions.md) tratează separat validitatea modelului.

# Agent AI în simulare

Agentul ia decizii singur în timpul unei rulări de simulare. Scenariul nu se modifică: fiecare decizie devine un
input al rulării respective și acționează abia după minutul în care a fost luată.

## Ce poate face

- **Mută operatori între controale.** Algoritmul propune mutări către controlul cel mai încărcat: coada plus
  sosirile din următoarele 30 de minute, raportate la capacitatea de servire. Operatorii vin de la cele mai libere
  controale, iar fiecare control păstrează cel puțin o persoană. Un operator mutat servește din nou după
  `transferMinutes`.
- **Cheamă operatori din rezervă** la controlul cel mai încărcat, când nu are pe cine să mute: 2, 5 sau tot ce a
  rămas, din cel mult `reserveOperators` pe rulare. Un agent care mută operatori și nu are rezerva setată primește
  implicit 10, inclusiv scenariile salvate înainte de această opțiune; `reserveOperators: 0` o dezactivează. Și ei
  servesc după `transferMinutes`. Rezerva nu e disponibilă pe un plan de ture.
- **Deschide ghișee de check-in**, câte cel mult două la un pas, până la numărul de ghișee din zonă.
- **Aplică planul optim (`plan`).** La fiecare punct periodic motorul Python rezolvă un model CP-SAT al cozii (fluid, pe
  blocuri de 5 minute, următoarea oră) din sosirile estimate ale rulării. Rezultă câte ghișee de check-in să fie
  deschise pe minut (se pot și închide; un ghișeu închis își termină pasagerul servit) și câți operatori să aibă fiecare
  control. Obiectivul minimizează coada maximă și coada rămasă, la cost minim de personal (ghișee/operatori deschiși și
  schimbări). Operatorii se repartizează dintr-un total fix (cei de acum plus rezerva rămasă), fiecare control păstrează
  cel puțin unul, iar cei adăugați servesc după `transferMinutes`. Planul se oferă întreg sau pe părți
  (`plan`, `plan-staff`, `plan-desks`) și se recalculează la fiecare punct: primul punct acționează ca plan inițial.
  Ghișeele se planifică doar cu `checkInDesks`, operatorii doar cu `staffMoves`.
- **Alege porțile la o întârziere programată.** Variantele sunt replanificarea optimă sau păstrarea fiecărui zbor
  la poarta lui, când un astfel de plan există.
- **Deschide o poartă inactivă cu un membru al personalului.** Când coada de îmbarcare a unui zbor nu se golește
  în timp util (minute necesare = coadă × `boardingServiceMinutes` ÷ benzi), algoritmul oferă poarta eligibilă la
  care niciun zbor nu e planificat până la plecarea lui și un `StaffMember` liber din același terminal (nealocat la
  securitate, ture sau echipe). Zborul își păstrează poarta: poarta deschisă adaugă o bandă de îmbarcare, iar
  pasagerii merg acolo doar dacă așteptarea câștigată depășește mersul (`gateChangeWalkingMinutes`). Agentul nu
  mută și nu schimbă niciodată porțile între zboruri. Se decide periodic și imediat după fiecare întârziere
  programată, cu `gateChoices` activ; deschiderea acționează după minutul deciziei și se salvează în
  `journey.gateOpenings`.

Controalele care au evenimente programate (închidere, reducere de personal) rămân în afara mutărilor și a rezervei.
Testele repetate și evaluările de personal rulează fără agent, ca variantele să rămână comparabile.

## Cum decide

1. La fiecare `decisionIntervalMinutes` minute simulate (5 când agentul e pornit din Control Tower, unde se poate
   schimba din panou; 15 în API dacă lipsește) și la fiecare întârziere programată,
   .NET cere motorului Python să ruleze ziua până la acel minut (`POST /simulation/v1/observe`).
2. Progresul se înregistrează, deci Control Tower avansează live.
3. Algoritmul oferă câteva variante plus „nicio schimbare”, între ele planul optim când există.
4. Gemini alege una după id, sub o schemă JSON cu enum. Motivul pe care îl scrie este afișat operatorului. Modelul
   nu poate adăuga acțiuni sau cifre.
5. Decizia se salvează în `twin.agent_decisions`, iar UI o citește din `GET /runs/{id}/agent-decisions`.
6. La final, rularea completă se execută cu toate deciziile ca inputuri: programe de personal (mutări și rezervă), ghișee deschise,
   alegeri de porți și porți deschise de personal (`journey.gateOpenings`). Validarea, corelarea, hash-urile și stocarea rămân cele existente.
7. O rulare reconciliată sau reluată refolosește deciziile salvate, fără să întrebe din nou modelul.

Dacă nu există cheie, dacă modelul nu răspunde la timp, dacă răspunsul e invalid sau dacă s-a terminat creditul,
decide o regulă de rezervă deterministă: aplică planul optim când există, altfel mută operatori sau deschide ghișee doar unde coada plus sosirile din următoarele
30 de minute depășesc capacitatea, sau deschide o poartă doar dacă coada de îmbarcare nu s-ar goli până la închidere, altfel „nicio schimbare” (la întârzieri, replanificarea optimă). Motivul începe cu
„Regulă de rezervă”, iar starea modelului rămâne înregistrată. Fiecare apel este
înregistrat în `ai_agent_events` (`agent_model_call`), cu limită zilnică și limită per rulare.

## Rezultatul agentului

După o rulare în care agentul a schimbat ceva, panoul compară rularea cu aceeași zi și același seed simulate fără
deciziile lui (`GET /runs/{id}/agent-impact`): ce a schimbat („4 operatori din rezervă la CP-1”), așteptarea medie și
P95 maxim (calculate ca în cardurile Control Tower), coada maximă și ora la care se golește coada. Simularea fără agent
are un id derivat din cel al rulării, deci motorul o calculează o singură dată și o servește apoi din execuțiile
salvate. Pașii fără schimbare sunt strânși într-un singur rând.

## Configurare

În `.env`, local, niciodată în Git:

```dotenv
GEMINI_API_KEY=
AGENT_MODEL=gemini-2.5-flash
```

`GET /agent/status` spune dacă modelul e configurat, fără să expună cheia. În Control Tower, comutatorul
„Agent AI” salvează agentul pe scenariu, cu toate acțiunile pe care scenariul le permite.

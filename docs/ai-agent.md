# AI în simulare

AI-ul nu mai ia decizii în timpul unei rulări: simularea rulează exact inputurile scenariului. Agentul AI live (mutări
de operatori, rezervă, ghișee și porți decise în timpul rulării) a fost scos din aplicație. Un scenariu salvat cu
câmpul `inputs.agent` se încarcă, se validează și se salvează în continuare, dar câmpul este ignorat la rulare: nu se
mai înregistrează decizii și personalul nu se schimbă în timpul simulării. Deciziile salvate de rulările vechi rămân în
`twin.agent_decisions`, dar nu mai sunt afișate.

AI-ul apare doar după o rulare terminată, în optimizarea de mai jos.

## Optimizare propusă după simulare

Sub rezumatul fiecărei rulări terminate, panoul „Optimizare propusă de AI” (`GET /runs/{id}/ai-optimization`)
arată variante de personal în jurul controlului cu cea mai lungă coadă: mută un operator, adaugă unul,
adaugă doi, retrage unul unde e cel mai puțin nevoie. Când scenariul are check-in cu coadă, apar și „încă un ghișeu” și
„încă două ghișee”, primele când coada de la check-in e mai mare decât la securitate. Așteptarea medie include atunci
și check-in-ul. Se propun doar variante pe care oamenii scenariului le pot acoperi (aceeași regulă ca what-if): un
operator în plus vine doar dintre operatorii de securitate nealocați ai scenariului (fără echipă proprie, dintr-un post
liber din layout), un ghișeu în plus doar de la un operator de check-in care nu e deja la ghișeu și în limita ghișeelor
zonei. Dacă
blocajul nu poate primi oameni, panoul spune de ce. Fiecare variantă este simulată din nou pe aceeași zi și cu
același seed (id derivat, calculat o singură dată de motor). Controalele cu plan de ture sunt lăsate neschimbate.

Tot ce se poate calcula se calculează în cod, nu de model (`RunOptimizationPlanner.Brief`):

- **blocajul**: cea mai lungă coadă din orice minut, check-in inclus — același „moment de vârf” ca titlul rezumatului
  rulării („Cea mai lungă coadă: 14 pasageri la CHECK-IN, în minutul 7.”);
- pentru fiecare variantă: verdictul (`better` / `worse` / `same`, cu pragurile de 5% de mai jos), așteptarea
  („9 s → 8 s (-10%)”), coada maximă, P95, personalul și o propoziție deterministă („Pasagerii așteaptă în medie cu
  1 s mai puțin, cu un operator în plus.”);
- **recomandarea** (`Recommend`) și de ce („scurtează cel mai mult așteptarea medie…” sau „economisește personal fără să
  lungească vizibil așteptarea”). Modelul nu alege niciodată.

Modelul primește aceste fapte deja digerate (text, nu numere brute) și scrie doar cuvinte: o propoziție scurtă pe
variantă și o frază de recomandare. Titlul cardului este acțiunea însăși. Fiecare propoziție a modelului e verificată
(`OptimizationText.Agrees`): fără cifre, fără locuri pe care varianta nu le atinge, așteptarea descrisă în sensul
verdictului (o variantă `worse` nu poate „scădea”/„scurta”/„reduce” așteptarea, una `better` nu o poate „crește”, una
`same` trebuie să spună că rămâne la fel), personalul în sensul variantei și „recomandat” doar la cea recomandată. Fraza
de recomandare trebuie să conțină acțiunea recomandată exact. Ce nu trece verificarea, sau lipsește, e înlocuit cu
propoziția deterministă, deci panoul nu arată niciodată text care contrazice cifrele. Rezumatul este fraza despre blocaj
(mereu din cod) plus fraza de recomandare.

O variantă merită recomandată doar dacă scurtează așteptarea medie cu cel puțin 5% sau economisește un operator cu cel
mult 5% așteptare în plus. Pasagerii au prioritate: dintre variantele care scurtează așteptarea se alege cea cu cel mai
puțin personal în plus care e la cel mult 5% de cea mai bună așteptare (o mutare gratuită bate o angajare aproape la fel
de bună); o economie de personal e recomandată doar când nicio variantă nu scurtează așteptarea. Pragurile nu se
suprapun, deci o economie aplicată nu este propusă înapoi la rularea următoare.
Când nicio variantă nu merită, panoul spune „Scenariul este deja optim”, nu recomandă nimic și păstrează variantele
testate într-o secțiune pliată.

„Aplică în scenariu” (`POST /runs/{id}/ai-optimization/apply`, doar cu drept de scriere) salvează varianta ca revizie
nouă a scenariului: retragerea scoate ultima persoană alocată la acel control, mutarea o realocă la controlul țintă,
adăugarea ia un operator liber după regula de mai sus, iar un ghișeu în plus crește ghișeele deschise la check-in.
Dacă scenariul s-a modificat după rulare, serverul răspunde 409 și nu scrie nimic.

## Configurare

În `.env`, local, niciodată în Git:

```dotenv
AGENT_PROVIDER=gemini
GEMINI_API_KEY=
AGENT_MODEL=gemini-3.8-flash
```

Alternativă gratuită, locală, fără cheie și fără credite: [Ollama](https://ollama.com). Instalează-l, descarcă un model
(`ollama pull qwen2.5:7b`, ~4,7 GB) și setează:

```dotenv
AGENT_PROVIDER=ollama
AGENT_MODEL=qwen2.5:7b
AGENT_ENDPOINT=http://host.docker.internal:11434
```

API-ul din Docker ajunge la Ollama de pe calculator prin `host.docker.internal`. Același contract JSON (schema cu
variantele permise) se trimite în câmpul `format` al Ollama, deci modelul local nu poate inventa variante sau cifre.
Primul apel după pornire încarcă modelul în memorie și durează mai mult.
Pe rulări reale, `qwen2.5:7b` a scris ~2–6 s per panou, fără contradicții rămase după verificare; `qwen2.5:14b` a fost de
~2,5 ori mai lent și a avut mai multe propoziții respinse, deci 7b rămâne recomandat.

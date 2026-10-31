# Explicații verificabile și transcripturi

Copilot păstrează selecția unui singur scenariu/run/zoneId/minut. Atât răspunsul
despre snapshot, cât și rezultatul unei unelte includ `explanation` cu versiunea
`1.0`. Textul este construit de server din șabloane. Modelul opțional poate
selecta numai un topic; proza, numerele și afirmațiile sale despre conectarea la
sisteme reale nu sunt acceptate. Fiecare explicație precizează că sunt date DEMO,
fără conectare sau comenzi asupra unui aeroport real.

## Surse și comparații

- `facts`: valori și unități copiate din răspunsul efectiv, runId, zoneId dacă
  observația este zonală, și `sourcePath` JSON Pointer în outputul original.
- `outputHash`: SHA-256 al outputului original serializat cu convențiile JSON
  Web .NET, înainte de adăugarea explicației și a receipt-ului. Pentru răspunsul
  simplu cele două proprietăți opționale sunt `null`; la unelte sunt omise.
  Acesta identifică sursa; nu este semnătură pentru date furnizate de client.
- `runs`: rularea investigată, baseline-ul nou al evaluării și fiecare candidat.
  UI oferă linkuri către replay și raportul KPI pentru aceste runId-uri.
- `comparisons`: valorile baseline/candidat, ambele runId-uri și căile către
  operanzi. Diferența = candidat − baseline. Procentul = diferență / baseline ×
  100, numai pentru baseline pozitiv și operanzi finiți. Zero/lipsă înseamnă
  `percent: null` cu motiv, niciodată un procent inventat. Cohortele incompatibile
  nu primesc comparații calculate. Indicatorii comparați sunt așteptarea medie,
  P95, riscul de îmbarcare ca fracție și pasagerii neprocesați.
- `outcomes`: explică separat candidații `unchanged`, `rejected`, `unassessable`,
  `failed`, `pending`, `tradeoff`, `improved`. Motivele folosesc un vocabular
  controlat; erorile brute și payload-urile nu intră în transcript. Variantele
  eșuate nu produc fapte sau comparații valide. Limitele explică datele lipsă,
  căutarea incompletă și lipsa unei concluzii de semnificație statistică.

Prognozele rămân etichetate estimări. Diagnosticul nu stabilește cauzalitate.
Scorul solverului nu devine procent de îmbunătățire. Generarea explicației și
citirea evaluării nu salvează automat transcripturi sau recomandări.

## Salvare explicită și detalii sensibile

Prefix: `/scenarios/{scenarioId}/runs/{runId}/copilot`.

| Metodă / cale                                 | Comportament                                                                              |
| --------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `POST /transcripts`                           | `{receipt, hideSensitive: true, retentionHours: 24}` salvează explicația emisă de server. |
| `GET /transcripts`                            | Ultimele 100 de intrări neexpirate ale rulării; numai metadate.                           |
| `GET /transcripts/{id}`                       | Explicație și surse; întrebarea este ascunsă implicit.                                    |
| `GET /transcripts/{id}?includeSensitive=true` | Arată întrebarea numai dacă operatorul a ales să o păstreze.                              |
| `POST /transcripts/{id}/redact`               | Elimină definitiv întrebarea liberă.                                                      |
| `DELETE /transcripts/{id}`                    | Șterge transcriptul.                                                                      |

`receipt` este o captură criptată și autentificată cu ASP.NET Data Protection,
valabilă 15 minute și legată de scenariu/run. Clientul nu poate modifica
explicația, inventa sursele sau salva dovada într-un alt context. Retry-ul
aceleiași capturi reutilizează ID-ul și expirarea inițială; nu poate restaura
întrebarea eliminată. O dovadă invalidă/expirată sau o retenție invalidă produce
`400 validation_failed` cu câmpul problematic. O citire expirată/străină produce
`404 resource_not_found`.

Implicit se păstrează numai explicația structurată, fără întrebarea liberă.
Nu se copiază outputul brut, payload-uri de evenimente, nume/ID-uri de personal
sau jurnalul de audit. La opt-in, întrebarea se stochează separat, dar răspunsul
de salvare și citirea obișnuită o ascund. Pagina AI Copilot nu expune
transcripturile; salvarea, eliminarea definitivă a întrebării și ștergerea sunt
disponibile numai prin API. Nu se salvează întrebări în localStorage sau cache-ul replay.
Răspunsurile Copilot au `Cache-Control: no-store`. API-ul folosește modelul de
acces al platformei demo; ascunderea nu adaugă autorizare pe roluri.

Ștergerea elimină imediat conținutul. Un marcaj fără întrebare, explicație sau
zonă rămâne cel mult 15 minute pentru a refuza reutilizarea unui receipt vechi,
apoi este eliminat de curățarea periodică. Acest marcaj nu apare în citiri.

## Retenție și configurare

Retenția implicită este 24 ore; maximul configurabil este 168 ore. Operatorul
poate alege o oră, o zi sau o săptămână în limita configurată. În Compose:

```dotenv
COPILOT_TRANSCRIPT_RETENTION_HOURS=24
COPILOT_TRANSCRIPT_MAX_HOURS=168
```

Cheile Data Protection sunt păstrate pe server în `/data/copilot-keys`, în
volumul API existent. Pentru alte deployment-uri, configurați
`CopilotTranscripts__KeyDirectory` într-un director persistent accesibil numai
serviciului. Cheile nu sunt distribuite în browser sau Git. Pierderea cheilor
invalidează receipt-urile nesalvate, fără să afecteze transcripturile deja salvate.

Citirile exclud imediat înregistrările expirate; un worker șterge fizic datele
expirate la fiecare minut când API-ul rulează. Dacă API-ul este oprit, acestea
rămân în DB până la următoarea curățare și sunt inaccesibile prin endpoint-uri.
Backup-urile DB urmează politica de retenție a deployment-ului, separat de worker.
Arhivarea scenariului păstrează transcripturile până la expirare. Purjarea permisă
a scenariului șterge transcripturile prin relația `ON DELETE CASCADE` cu run-ul;
restricțiile existente pentru run-uri reținute de batch/recomandare rămân active.

## Demonstrare și verificare

În Studio creează presetul Bottleneck, rulează-l, apoi deschide Copilot și cere
o observație. Verifică sursele și linkurile. În unelte, lansează o evaluare de
personal și verifică diferențele și motivele fiecărui candidat. Salvează explicit
un răspuns cu întrebarea eliminată, apoi reîncarcă transcripturile rulării fără
o întrebare nouă. Pentru demonstrarea opțiunilor sensibile, salvează o altă
întrebare cu opt-in, arată/ascunde textul și elimină-l definitiv.

Testele `CopilotExplanationTests` verifică numeric căile surselor, hash-ul,
comparații CP-SAT/SimPy reale, numitor zero/lipsă, eșecuri, retenție cu ceas
controlat, receipt falsificat/străin/expirat, retry, ascundere, redactare,
arhivare și purjare. Testele Angular verifică linkurile, procentele și acțiunile
explicit declanșate, resetarea contextului și refuzul datelor străine.

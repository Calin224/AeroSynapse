# Modelele 3D ale aeroportului

Clientul folosește exporturi autonome glTF 2.0 în `AeroSynapse.Client/public/models/aerosynapse/`.
Geometria terminalului provine din modelul aeroportuar furnizat pentru proiect, cu
acoperiș transparent și lounge eliminat. Datele și pozițiile sunt demonstrative;
modelul nu reproduce exact un aeroport real și nu folosește telemetrie live.

| Fișier | Octeți | Triunghiuri | Încărcare |
| --- | ---: | ---: | --- |
| `terminal.glb` | 1.965.480 | 106.945 | La inițializarea vizualizării |
| `exterior-low.glb` | 668.988 | 32.293 | La selectarea modului Exterior |
| `exterior-detail.glb` | 1.226.064 | 81.284 | La apropierea camerei în modul Exterior |

Exporturile includ paleta PNG și folosesc `EXT_meshopt_compression`, decodată prin
Three.js/Meshopt. Unitatea este metrul; glTF folosește axa verticală Y. Manifestul
documentează transformările din Blender, dimensiunile și limitele de performanță.

Cele 46 de entități au identificatori stabili, inclusiv între nivelurile de detaliu.
Selecția prin click sau lista accesibilă expune `entityId` și `zoneId`. Identificatorii
sunt metadate sintetice ale vizualizării; această integrare nu creează rânduri în baza
de date și nu conectează modelul la hărțile încărcate prin API.

Filtrele și modurile schimbă numai afișarea. Rendererul reutilizează modelele,
anulează cererile la distrugere și eliberează geometriile, materialele și texturile.
Un eșec la încărcarea exteriorului păstrează terminalul; un eșec al nivelului detaliat
păstrează exteriorul simplificat și permite reîncercarea.

## Proveniență

Sursa și SHA-256 sunt în `manifest.json`. Autorul, URL-ul și licența originală a
modelului furnizat nu au fost identificate și sunt păstrate ca `unverified`.
Exporturilor nu li se atribuie o licență CC0. Marcajele demonstrative adăugate pentru
proiect nu schimbă drepturile asupra modelului original.

## Verificare

Din `AeroSynapse.Client`:

```sh
npm run verify:assets
npm test -- --watch=false
npm run build
```

Verificatorul importă modelele reale prin Three.js/Meshopt și verifică structura GLB,
paleta încorporată, axele, dimensiunile, acoperișul transparent, identificatorii și
bugetele. În Node, texturile sunt înlocuite cu obiecte `Texture`; aspectul și
interacțiunea WebGL se verifică separat în browser.

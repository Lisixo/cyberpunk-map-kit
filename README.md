# Cyberpunk 2077 Map Data Extractor

Automatyczny ekstraktor danych przestrzennych z gry **Cyberpunk 2077 + Phantom Liberty (EP1)** wykorzystujący biblioteki lokalnego **WolvenKit 10** (`WolvenKit.RED4.dll`, `WolvenKit.Common.dll`, `WolvenKit.Modkit.dll`) na platformie **.NET 10**, czytający archiwa `.archive` oraz `tweakdb_ep1.bin` bezpośrednio w pamięci.

## Wymagania i instalacja

- **.NET 10 SDK** (Windows x64)
- **Cyberpunk 2077** (zalecane z dodatkiem **Phantom Liberty / EP1**)
- **[WolvenKit](https://github.com/WolvenKit/WolvenKit)** (licencja GPL-3.0) w wersji min. **`10.0.0-beta.1`** (`10-beta.1`) — zainstaluj lub rozpakuj zawartość WolvenKit bezpośrednio do folderu `WolvenKit/` w katalogu głównym repozytorium (tak, aby wymagane biblioteki, m.in. `WolvenKit.RED4.dll`, `WolvenKit.Common.dll`, `WolvenKit.Core.dll`, `WolvenKit.Modkit.dll`, `CommunityToolkit.Mvvm.dll` oraz `kraken.dll`, znajdowały się bezpośrednio w `WolvenKit/`).

## Uruchomienie (regeneracja danych)

```powershell
dotnet run -c Release --project extractor -- --extract --game "C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077"
```

Parametry:
- `--game "<ścieżka do Cyberpunk 2077>"` — ścieżka do katalogu instalacji gry
- `--out "<katalog wyjściowy>"` — katalog wyjściowy (domyślnie `./output`)
- `--verbose` — pełne logi RTTI

## Układ współrzędnych

Wszystkie pliki GeoJSON używają **surowych współrzędnych silnika REDengine 4 w metrach** (`[x, y]` lub `[x, y, z]`, gdzie oś `Z` to wysokość nad poziomem odniesienia świata gry). Pliki są w pełni poprawnym formatem `.geojson` (i standardowym `.json`).

## Wygenerowane pliki (`output/`)

### 1. `regions.geojson` — Dzielnice, poddzielnice i obszary lokacji
Geometria: `Polygon` (`[x, y]`).
Pola `properties`:
- `id`, `district_id` (np. `Districts.Kabuki`), `enum_name`
- `name` — polska nazwa dzielnicy / lokacji (z `onscreens_final.json` / `TweakDB`)
- `parent_district_id`, `parent_district`, `top_district` — hierarchia dzielnic
- `is_subdistrict`, `is_quest_district`
- `area_m2`, `z_min`, `z_max`, `center`, `bbox`, `source_sector`

### 2. `roads.geojson` — Sieć drogowa i graf pasów ruchu
Geometria: `LineString` (`[[x, y, z], ...]`, kolejność wierzchołków określa kierunek ruchu).
Pola `properties`:
- `id` — identyfikator pasa (`lane_id`)
- `direction` (`"oneway"` — każdy pas ruchu w silniku ma jawny kierunek; `road_bidirectional` wskazuje, czy dana grupa sąsiednich pasów `neighbor_group_index` ma pasy w przeciwnych kierunkach)
- `group_lane_count`, `neighbor_group_index`, `lane_number`, `seq_number`, `is_reversed`
- `length_m`, `width_m`, `max_speed`, `flags`, `sub_graph_id`, `dead_end_start`, `road_materials`
- `in_lanes` — tablica `id` pasów wchodzących
- `out_lanes` — tablica `id` pasów wychodzących (połączenia na skrzyżowaniach)
- `district`, `subdistrict`

### 3. `buildings.geojson` — Budynki i ich obrysy
Geometria: `Polygon` (zorientowany obrys 2D OBB w metrach).
Pola `properties`:
- `id`, `name`
- `type` *(opcjonalne; `null`, jeśli brak danych klasyfikujących)* — m.in. `megabuilding`, `skyscraper`, `highrise`, `corporate`, `commercial`, `industrial`, `residential`, `infrastructure`
- `location` (`[x, y, base_z]`), `width_m`, `length_m`, `height_m`, `area_m2`, `base_z`, `top_z`, `rotation_deg`
- `district`, `subdistrict`
- `source_type`, `mesh_path`

### 4. `poi.geojson` — Punkty POI i terminale Fast Travel / Metro
Geometria: `Point` (`[x, y, z]`).
Pola `properties`:
- `id`, `name` (po polsku)
- `category` (`fast_travel`, `metro_station`, `ripperdoc`, `shop_weapons`, `shop_clothes`, `shop_food`, `bar`, `apartment`, `fixer`, `tarot`, `cyberpsycho`, `ncpd_scanner`, `gig`, `dogtown_courier`, `autofixer`, `street_race`, itp.)
- `variant`, `district`, `subdistrict`, `source`, `ref_id`

### 5. `index_stats.json` — Podsumowanie i metadane indeksu

## Podgląd wizualny

Projekt zawiera dwa sposoby wizualnego podglądu wygenerowanych danych z `output/`:

### Night City Holomap (`viewer/`) — zalecane (3D Three.js + 2D Leaflet)

Interaktywna aplikacja webowa (React + TypeScript + Vite) z widokiem **3D (Three.js)** oraz **2D (Leaflet)**, filtrowaniem warstw/dzielnic/typów budynków i POI, wyszukiwarką (`Ctrl+K`), szybkim przełączaniem trybu (`3` / `2`) oraz inspektorem obiektów. Automatycznie serwuje pliki z `output/` pod ścieżką `/data/` (obsługuje też ręczne wczytywanie plików w interfejsie).

Wymaga środowiska **Bun** (dla `bun run start`; skrypty `dev`, `build` i `preview` działają również z Node.js):

```powershell
cd viewer
bun install
```

- **Tryb deweloperski (Vite):**
  ```powershell
  bun run dev
  ```
  Otwórz `http://localhost:5173`.

- **Build produkcyjny + serwer Bun:**
  ```powershell
  bun run build
  bun run start
  ```
  Otwórz `http://localhost:3000` (port można zmienić zmienną środowiskową `PORT`). Alternatywnie `bun run preview` uruchamia podgląd zbudowanej aplikacji przez Vite na `http://localhost:4173`.

## Licencja i zastrzeżenia prawne

- **Licencja:** Projekt jest udostępniany na licencji **GNU General Public License v3.0** ([LICENSE](LICENSE)) ze względu na wykorzystanie bibliotek [WolvenKit](https://github.com/WolvenKit/WolvenKit) (GPL-3.0).
- **Projekt nieoficjalny i niekomercyjny:** Narzędzie ma charakter nieoficjalny i wyłącznie niekomercyjny, zgodnie z [Wytycznymi dotyczącymi twórczości fanowskiej CD PROJEKT RED](https://www.cdprojektred.com/en/fan-content). Projekt nie jest w żaden sposób powiązany, wspierany ani zatwierdzony przez CD PROJEKT RED.
- **Znaki towarowe i zasoby gry:** *CD PROJEKT®*, *CD PROJEKT RED®*, *Cyberpunk®*, *Cyberpunk 2077®* oraz powiązane logotypy i nazwy (w tym *Phantom Liberty* i *Night City*) są znakami towarowymi należącymi do CD PROJEKT S.A. Repozytorium **nie zawiera** plików gry, wygenerowanych danych przestrzennych (`output/`) ani binariów WolvenKit (`WolvenKit/`) — użytkownik musi posiadać własną, legalną kopię gry i samodzielnie wygenerować dane lokalnie.

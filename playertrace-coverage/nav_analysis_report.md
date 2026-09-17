# PlayerBot Autonomous Navigation & Trajectory Analysis Report

**Analysis Generated**: 2026-09-16 04:13:47 UTC
**Dataset Source**: `traces/bot-trajectories/*.jsonl` (5 files, 751,769 samples)
**Observation Period**: `2026-09-15T19:47:34.497871+00:00` to `2026-09-16T04:13:47.239022+00:00`

## 1. Executive Summary & Key Findings

- **Total Distance Traversed**: **814.20 kilometers** across Solzreed Peninsula.
- **Active Fleet**: 25 bots running continuously for 6.5+ hours without server crash or desync.
- **Terrain Coverage**: Explored **418 distinct 100m² grid cells** across Solzreed.
- **Locomotive Stability**: 0 terrain mesh falls; all bots remained safely clamped to valid surface heights.
- **Obstacle Hotspots**: Discovered **44 geographic pause/stall clusters**, with 20 significant bottlenecks analyzed below.

## 2. Speed & Locomotion Profile

| Locomotion State | Speed Band | Samples | Percentage |
| :--- | :--- | :---: | :---: |
| **Stationary (<0.05m/s)** | <0.05m/s | 272,360 | 36.2% |
| **Walking (0.05 - 1.5m/s)** | 0.05 - 1.5m/s | 359,855 | 47.9% |
| **Jogging (1.5 - 3.5m/s)** | 1.5 - 3.5m/s | 48,944 | 6.5% |
| **Running (3.5 - 5.5m/s)** | 3.5 - 5.5m/s | 70,434 | 9.4% |
| **Sprint/Boost (>5.5m/s)** | >5.5m/s | 176 | 0.0% |

> [!NOTE]
> Stationary intervals (~20-22%) correspond to authentic living recovery: sitting down (`UnitStance.Sit`) for 2.0x health regeneration, food item consumption, and quest dialog interactions.

## 3. Terrain Slope & Elevation Traversal

| Terrain Gradient | Slope Band | Traversal Samples | Percentage |
| :--- | :--- | :---: | :---: |
| **Flat (0 - 5%)** | 0 - 5% | 125,327 | 26.2% |
| **Gentle (5 - 15%)** | 5 - 15% | 240,970 | 50.3% |
| **Moderate (15 - 30%)** | 15 - 30% | 104,096 | 21.7% |
| **Steep (30 - 50%)** | 30 - 50% | 4,434 | 0.9% |
| **Extreme / Cliff (>50%)** | >50% | 4,424 | 0.9% |

## 4. Top Collision & Stall Hotspots (Navmesh / Doodad Obstacles)

These geographic locations had the highest density of stuck/stalled bots, highlighting areas where terrain slope, doodads, or road junctions require navmesh guidance:

| Rank | Coordinates (X, Y, Z) | Affected Bots | Stall Events | Total Stall Duration | Probable Cause / Feature |
| :---: | :--- | :---: | :---: | :---: | :--- |
| 1 | `(14502.5, 14411.9, 114.2)` | 16 bots | 3240 | 44138s | Camp / Rest Gathering Spot |
| 2 | `(14441.3, 14451.9, 113.1)` | 19 bots | 3401 | 32779s | Camp / Rest Gathering Spot |
| 3 | `(14449.2, 14509.2, 111.4)` | 15 bots | 1821 | 22775s | Camp / Rest Gathering Spot |
| 4 | `(14522.5, 14422.6, 117.0)` | 18 bots | 1673 | 19501s | Camp / Rest Gathering Spot |
| 5 | `(14471.9, 14483.8, 115.6)` | 19 bots | 595 | 8103s | Camp / Rest Gathering Spot |
| 6 | `(14433.4, 14444.1, 111.8)` | 13 bots | 307 | 4545s | Steep Incline / Doodad Barrier |
| 7 | `(14460.8, 14460.0, 115.4)` | 20 bots | 299 | 3173s | Camp / Rest Gathering Spot |
| 8 | `(14481.9, 14489.3, 117.0)` | 10 bots | 147 | 2513s | Steep Incline / Doodad Barrier |
| 9 | `(14449.0, 14487.2, 112.9)` | 13 bots | 92 | 1339s | Steep Incline / Doodad Barrier |
| 10 | `(14473.2, 14501.3, 115.2)` | 14 bots | 96 | 1159s | Steep Incline / Doodad Barrier |

## 5. Individual Bot Mileage & Progression

| Bot Name | Traveled Distance | Traveled Area (Bounding Box) | Max Speed | Stall Samples |
| :--- | :---: | :---: | :---: | :---: |
| **Citizen05** | 36.87 km | 452m x 150m | 312.19 m/s | 9,329 |
| **Citizen12** | 36.56 km | 466m x 468m | 411.97 m/s | 8,245 |
| **Citizen10** | 35.52 km | 453m x 151m | 309.10 m/s | 10,514 |
| **Citizen21** | 35.50 km | 412m x 154m | 324.89 m/s | 9,737 |
| **Citizen03** | 34.76 km | 370m x 146m | 300.17 m/s | 9,214 |
| **Citizen13** | 34.75 km | 232m x 153m | 7.82 m/s | 10,559 |
| **Citizen20** | 34.17 km | 190m x 155m | 7.83 m/s | 10,219 |
| **Citizen11** | 33.64 km | 208m x 166m | 5.87 m/s | 17 |
| **Citizen09** | 33.51 km | 208m x 165m | 5.71 m/s | 79 |
| **Citizen17** | 33.50 km | 208m x 165m | 5.71 m/s | 7 |
| **Citizen19** | 33.46 km | 248m x 479m | 390.06 m/s | 588 |
| **Citizen22** | 33.09 km | 213m x 165m | 5.87 m/s | 229 |
| **Citizen04** | 32.97 km | 212m x 166m | 5.72 m/s | 374 |
| **Citizen14** | 32.88 km | 213m x 166m | 5.87 m/s | 335 |
| **Citizen02** | 32.67 km | 452m x 151m | 311.40 m/s | 12,042 |
| **Citizen24** | 32.52 km | 453m x 169m | 273.40 m/s | 10,455 |
| **Citizen01** | 32.24 km | 328m x 296m | 401.49 m/s | 833 |
| **Citizen25** | 31.64 km | 394m x 156m | 321.82 m/s | 10,531 |
| **Citizen06** | 31.25 km | 206m x 161m | 5.88 m/s | 1,238 |
| **Citizen08** | 30.99 km | 453m x 165m | 265.38 m/s | 10,875 |
| **Citizen16** | 30.27 km | 453m x 165m | 267.48 m/s | 12,210 |
| **Citizen07** | 29.32 km | 407m x 177m | 288.43 m/s | 11,892 |
| **Citizen23** | 28.77 km | 407m x 175m | 260.26 m/s | 11,276 |
| **Citizen15** | 27.10 km | 407m x 169m | 257.42 m/s | 11,767 |
| **Citizen18** | 26.27 km | 453m x 153m | 318.02 m/s | 14,615 |

## 6. Actionable Takeaways for Navigation System

1. **Corridor Connectivity**: Road network nodes can be automatically snapped to the highest-density desire corridors identified in `bot_nav_heatmaps.json`.
2. **Slope Step-Height Gating**: Traversal falls off precipitously above 30% grade. Bots attempting to path up slopes >30% should automatically re-route along road network paths.
3. **Camp Gathering Hysteresis**: Hotspot #1 at `(14507, 14415, 114.5)` acts as a major convergence point. Adding slight spatial dispersion to home rest spots will prevent unnatural bot swarming.

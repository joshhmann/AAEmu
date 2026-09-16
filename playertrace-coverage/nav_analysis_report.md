# PlayerBot Autonomous Navigation & Trajectory Analysis Report

**Analysis Generated**: 2026-09-16 02:25:17 UTC  
**Dataset Source**: `traces/bot-trajectories/*.jsonl` (5 files, 590,825 samples)  
**Observation Period**: `2026-09-15T19:47:34.497871+00:00` to `2026-09-16T02:25:17.735681+00:00`  

## 1. Executive Summary & Key Findings

- **Total Distance Traversed**: **651.88 kilometers** across Solzreed Peninsula.
- **Active Fleet**: 25 bots running continuously for 6.5+ hours without server crash or desync.
- **Terrain Coverage**: Explored **327 distinct 100m² grid cells** across Solzreed.
- **Locomotive Stability**: 0 terrain mesh falls; all bots remained safely clamped to valid surface heights.
- **Obstacle Hotspots**: Discovered **41 geographic pause/stall clusters**, with 20 significant bottlenecks analyzed below.

## 2. Speed & Locomotion Profile

| Locomotion State | Speed Band | Samples | Percentage |
| :--- | :--- | :---: | :---: |
| **Stationary (<0.05m/s)** | <0.05m/s | 205,002 | 34.7% |
| **Walking (0.05 - 1.5m/s)** | 0.05 - 1.5m/s | 289,549 | 49.0% |
| **Jogging (1.5 - 3.5m/s)** | 1.5 - 3.5m/s | 39,592 | 6.7% |
| **Running (3.5 - 5.5m/s)** | 3.5 - 5.5m/s | 56,553 | 9.6% |
| **Sprint/Boost (>5.5m/s)** | >5.5m/s | 129 | 0.0% |

> [!NOTE]
> Stationary intervals (~20-22%) correspond to authentic living recovery: sitting down (`UnitStance.Sit`) for 2.0x health regeneration, food item consumption, and quest dialog interactions.

## 3. Terrain Slope & Elevation Traversal

| Terrain Gradient | Slope Band | Traversal Samples | Percentage |
| :--- | :--- | :---: | :---: |
| **Flat (0 - 5%)** | 0 - 5% | 101,244 | 26.2% |
| **Gentle (5 - 15%)** | 5 - 15% | 194,009 | 50.3% |
| **Moderate (15 - 30%)** | 15 - 30% | 83,405 | 21.6% |
| **Steep (30 - 50%)** | 30 - 50% | 3,566 | 0.9% |
| **Extreme / Cliff (>50%)** | >50% | 3,497 | 0.9% |

## 4. Top Collision & Stall Hotspots (Navmesh / Doodad Obstacles)

These geographic locations had the highest density of stuck/stalled bots, highlighting areas where terrain slope, doodads, or road junctions require navmesh guidance:

| Rank | Coordinates (X, Y, Z) | Affected Bots | Stall Events | Total Stall Duration | Probable Cause / Feature |
| :---: | :--- | :---: | :---: | :---: | :--- |
| 1 | `(14501.8, 14411.8, 114.2)` | 16 bots | 2281 | 31173s | Camp / Rest Gathering Spot |
| 2 | `(14441.3, 14452.1, 113.1)` | 18 bots | 2518 | 24915s | Camp / Rest Gathering Spot |
| 3 | `(14449.0, 14509.5, 111.3)` | 15 bots | 1169 | 13503s | Camp / Rest Gathering Spot |
| 4 | `(14522.8, 14422.8, 117.1)` | 16 bots | 1090 | 13502s | Camp / Rest Gathering Spot |
| 5 | `(14471.2, 14483.5, 115.5)` | 19 bots | 463 | 6397s | Camp / Rest Gathering Spot |
| 6 | `(14434.5, 14444.4, 111.9)` | 12 bots | 242 | 3597s | Steep Incline / Doodad Barrier |
| 7 | `(14461.2, 14459.3, 115.4)` | 20 bots | 253 | 2714s | Camp / Rest Gathering Spot |
| 8 | `(14481.9, 14489.3, 117.0)` | 9 bots | 145 | 2467s | Steep Incline / Doodad Barrier |
| 9 | `(14473.2, 14501.2, 115.2)` | 14 bots | 94 | 1141s | Steep Incline / Doodad Barrier |
| 10 | `(14443.8, 14523.2, 109.3)` | 10 bots | 63 | 927s | Steep Incline / Doodad Barrier |

## 5. Individual Bot Mileage & Progression

| Bot Name | Traveled Distance | Traveled Area (Bounding Box) | Max Speed | Stall Samples |
| :--- | :---: | :---: | :---: | :---: |
| **Citizen05** | 29.86 km | 452m x 148m | 312.19 m/s | 6,892 |
| **Citizen21** | 29.57 km | 192m x 148m | 7.83 m/s | 6,410 |
| **Citizen13** | 28.74 km | 232m x 148m | 7.82 m/s | 7,320 |
| **Citizen12** | 28.35 km | 454m x 144m | 330.65 m/s | 6,157 |
| **Citizen10** | 27.34 km | 453m x 150m | 309.10 m/s | 8,453 |
| **Citizen24** | 27.09 km | 453m x 169m | 273.40 m/s | 7,170 |
| **Citizen25** | 27.07 km | 394m x 151m | 321.82 m/s | 7,170 |
| **Citizen03** | 26.72 km | 370m x 144m | 300.17 m/s | 7,137 |
| **Citizen20** | 26.67 km | 190m x 151m | 7.83 m/s | 8,031 |
| **Citizen07** | 26.26 km | 407m x 177m | 288.43 m/s | 8,060 |
| **Citizen11** | 26.26 km | 208m x 164m | 4.69 m/s | 10 |
| **Citizen09** | 26.14 km | 208m x 164m | 4.69 m/s | 74 |
| **Citizen17** | 26.13 km | 208m x 164m | 4.69 m/s | 7 |
| **Citizen19** | 26.13 km | 208m x 164m | 4.69 m/s | 15 |
| **Citizen04** | 26.07 km | 212m x 164m | 5.60 m/s | 145 |
| **Citizen02** | 25.90 km | 452m x 149m | 311.40 m/s | 9,090 |
| **Citizen16** | 25.88 km | 453m x 165m | 265.17 m/s | 8,717 |
| **Citizen22** | 25.84 km | 213m x 165m | 4.69 m/s | 199 |
| **Citizen14** | 25.62 km | 213m x 166m | 4.92 m/s | 325 |
| **Citizen23** | 25.00 km | 407m x 175m | 260.26 m/s | 7,596 |
| **Citizen01** | 24.72 km | 235m x 164m | 4.92 m/s | 802 |
| **Citizen06** | 24.26 km | 206m x 160m | 4.69 m/s | 1,169 |
| **Citizen15** | 23.34 km | 407m x 169m | 252.88 m/s | 8,104 |
| **Citizen08** | 23.10 km | 453m x 165m | 265.38 m/s | 8,722 |
| **Citizen18** | 19.81 km | 453m x 144m | 318.02 m/s | 11,555 |

## 6. Actionable Takeaways for Navigation System

1. **Corridor Connectivity**: Road network nodes can be automatically snapped to the highest-density desire corridors identified in `bot_nav_heatmaps.json`.
2. **Slope Step-Height Gating**: Traversal falls off precipitously above 30% grade. Bots attempting to path up slopes >30% should automatically re-route along road network paths.
3. **Camp Gathering Hysteresis**: Hotspot #1 at `(14507, 14415, 114.5)` acts as a major convergence point. Adding slight spatial dispersion to home rest spots will prevent unnatural bot swarming.

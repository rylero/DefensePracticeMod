# Defense Practice Mod

Splitscreen player-2 bump-defense AI for MoSim Rebuilt 2026.

The chassis is a **1678 Citrus Circuits** prefab variant so the bot can go under the trench. Difficulty and the newer bump/pin behavior are serialized on the robot, with Easy / Medium / Hard / Brutal variants.

## Robot parameters

On `HubBumpDefenseAutopilot`:

| Field | What it does |
| --- | --- |
| `Level` | Easy / Medium / Hard / Brutal reads, ram, and retreat |
| `Shot` | Off / Normal / Aggressive shot contest |
| `DriveSpeed` | 0.50x–1.25x drive scale |
| `StayOffBump` | Stay in the alliance zone instead of climbing the bump |
| `RequireRealPin` | Only pin after real contact while the opponent is shooting |
| `UsePracticeMenuSettings` | If enabled, the practice-menu sliders override the fields above |

Pick a variant in the robot menu, or edit the fields on the prefab.

## Drop into MoSim public source

Copy the `Assets/` folder from this repo into a [MoSim public](https://github.com/MoSimulator/MoSimulator-Public) Unity project (Unity 2023.2.22f1). Then:

1. `Tools > Modding > Generate Bump Defense Prefabs` if the 1678 variants need regenerating
2. `Tools > Modding > Export Bump Defense For Steam`

Steam loads one Workshop mod at a time, so select this robot as splitscreen player 2.

Official LICENSE requires Workshop distribution for in-game mods. This repo is source only.

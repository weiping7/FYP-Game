# FYP Game — 2D Arena Survival

A ten-stage, single-player arena survival game developed in Unity for the BMCS3413 Project II submission. The project combines constrained stage progression and enemy decision logic with combat, weapons, rewards, shops, persistent run data, and player-guidance interfaces.

## Project team and module ownership

| Member | Module | Primary responsibilities |
| --- | --- | --- |
| Ew Chiu Linn | Module 1 — Game intelligence and progression | Enemy finite-state-machine decisions, Global Aggro, constrained Portal generation, stage routing, run persistence, difficulty selection, tutorial/guidance, and progression UI |
| Chua Wei Ping | Module 2 — Combat execution and optimization | Player/enemy movement, collisions and damage, weapons and projectiles, enemy statistics and rewards, health feedback, and object pooling/performance testing |

The modules communicate through `GameEvents`, `IEnemyMotor`, `IPlayerInteractable`, shared run data, public configuration methods, and the shared `GameplayCore` prefab. Module 1 decides enemy state and progression flow; Module 2 executes movement and combat behaviour.

## Implemented outcome

- New Game setup with nickname and Easy, Normal, or Hard difficulty descriptions.
- Continue support through one versioned local JSON save.
- First-run tutorial and reusable User Guide.
- Contextual HUD showing health, nickname, level, experience, coins, weapon, stage, room type, objective, and distance to the final Boss.
- Enemy Idle, Chase, and Attack states with proximity-triggered Global Aggro.
- Combat, Elite, Shop, and final Boss rooms in dedicated authored scenes.
- Weighted Portal selection with unique choices, no consecutive Shops, and three non-Elite stages between Elite rooms.
- Fixed ten-stage demonstration flow: Stage 1 is Combat, Stage 9 offers only the Boss Portal, and Stage 10 ends through the End Portal after the final Boss is defeated.
- Four weapon behaviours, enemy health and damage feedback, experience/coin rewards, permanent upgrades, and interactive Shop displays.
- Pause, death, Retry, Main Menu, Quit Game, and victory flows.
- Object pooling and a separate performance-test scene.

## Requirements

- Unity Editor `6000.4.1f1`
- Windows 10 or Windows 11 for the submitted desktop target
- Git or a downloaded ZIP of this repository

Unity Package Manager restores the dependencies declared in `Packages/manifest.json` when the project is opened.

## Open and run the project

1. Clone the repository or download and extract its ZIP archive.
2. Add the repository folder to Unity Hub and open it with Unity `6000.4.1f1`.
3. Allow Unity to restore packages and finish importing assets.
4. Open `Assets/Scenes/Menu.unity`.
5. Enter Play Mode and select **New Game**. Use **Continue** only after a valid run has been saved.

The enabled build-scene order is:

1. `Assets/Scenes/Menu.unity`
2. `Assets/Scenes/Combat.unity`
3. `Assets/Scenes/Elite.unity`
4. `Assets/Scenes/Boss.unity`
5. `Assets/Scenes/Shop.unity`

`Assets/Scenes/Game.unity` is retained as the source scene used by the editor-only gameplay scene builder. `Assets/Scenes/PerformanceTest.unity` is a test harness and is intentionally excluded from the player build.

## Controls

| Input | Action |
| --- | --- |
| W, A, S, D | Move the player |
| E | Interact with a nearby Portal or Shop display |
| Esc | Pause, resume, open the User Guide, return to Main Menu, or quit |
| Mouse / UI buttons | Choose menu, difficulty, upgrade, and purchase actions |

Weapons attack valid targets automatically. The player controls positioning, progression choices, and nearby interactions.

## Validation and testing

Before creating a build, run **Tools > FYP > Validate Gameplay Setup** in the Unity Editor. The validator checks the shared gameplay prefab, enemy composition, required scenes, Build Settings, UI resources, and Shop anchors.

The optional `Assets/Scenes/PerformanceTest.unity` scene compares the real gameplay path with object pooling enabled and disabled. Configure `PerformanceTestController`, run each mode under the same conditions, and record Unity Profiler/FPS results separately from the production build.

Manual functional and integration test cases cover the tutorial, interface, FSM transitions, Global Aggro, Portal constraints, scene routing, Shop flow, persistence, difficulty multipliers, damage, pause/death protection, and the final Boss flow.

## Build a Windows executable

1. Open **File > Build Profiles**.
2. Select the Windows profile.
3. Confirm that only the five production scenes listed above are enabled and in the stated order.
4. Select an empty output folder outside the Unity project.
5. Choose **Build**.

Do not commit the generated build folder, `Library`, `Temp`, `Logs`, or `UserSettings`.

## Repository structure

| Path | Purpose |
| --- | --- |
| `Assets/Scripts/AI` | Module 1 enemy decision states and movement boundary |
| `Assets/Scripts/GameFlow` | Stage rules, run data, guidance, persistence, scene routing, Portal and Shop interactions |
| `Assets/Scripts/Enemy`, `Player`, `Weapons`, `Pooling` | Combat execution, statistics, rewards, weapons, player systems, and pooling |
| `Assets/Prefab/Gameplay/GameplayCore.prefab` | Shared gameplay composition used by authored scenes |
| `Assets/Resources` | Runtime-loaded Portal, environment, weapon, pickup, Shop, font, and UI assets |
| `Assets/Scenes` | Production scenes plus source and performance-test scenes |
| `Packages` | Unity package manifest and lock file |
| `ProjectSettings` | Unity project and Build Settings |

## Persistence

The Continue save is written as `module1_run_save.json` under Unity's platform-specific `Application.persistentDataPath`. It is runtime data and is not stored in this repository.

## Asset and licence information

Font licences and known asset provenance are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Any externally sourced asset must be added there with its author, source URL, licence, and the files in which it is used before the final academic submission.

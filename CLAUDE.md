# SE Custom Scripts

Scripts in this repo run inside Space Engineers **Programmable Blocks**, not as mods. PB scripts have different constraints and APIs than game mods.

## Script Structure
Every script compiles as a single class body extending `MyGridProgram`. No `using` directives, namespaces, or outer class wrappers — the script body is pasted directly into the PB.

- `Program()` — constructor; runs once on load or recompile
- `Main(string argument, UpdateType updateSource)` — entry point; called each tick or on trigger
- `Save()` — called on world save; persist state via the `Storage` string property

## Runtime Constraints
- Instruction limit: 10,000 per `Main()` call (soft cap); 50,000 hard cap
- `Runtime.UpdateFrequency` controls tick rate: `Update1` (every frame), `Update10`, `Update100`, or `None`
- `Runtime.LastRunTimeMs` and `Runtime.CurrentInstructionCount` available for profiling

## Key PB APIs
- `GridTerminalSystem` — enumerate and get blocks on the grid
- `Me` — reference to the programmable block itself
- `Runtime` — tick and instruction info
- `Echo(string)` — write to the PB detail panel
- `Storage` — single string persisted across saves; serialize/deserialize manually
- `IGC` — Inter-Grid Communication; send and receive messages between grids

## Script Organization
Each script lives in its own subfolder. The `.cs` file contains the raw script body only — valid to paste directly into a PB.

## Deployment
Scripts are deployed to the in-game local script browser at:

`C:\Users\cavanhorn\AppData\Roaming\SpaceEngineers\IngameScripts\local\`

Each script gets a folder there whose name matches the script's repo folder name (e.g. `TrackCartController`). Inside that folder the script file must be named **`Script.cs`** — that exact name is what makes it appear in the in-game script browser.

Deploy on every script change: copy the repo's `<Name>/<Name>.cs` to `IngameScripts\local\<Name>\Script.cs`.

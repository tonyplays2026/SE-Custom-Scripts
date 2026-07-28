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

## Grid Scoping (gotcha)
`GridTerminalSystem` spans grids docked by **connector**, not just the local grid. When a grid is connected to others (e.g. a cart docked to a base, or multiple carts docked to the same base), `GetBlocksOfType` returns blocks from all of them. To restrict a lookup to the block's own construct (its grid + mechanical subgrids via rotors/pistons/hinges, excluding connector-docked grids), pass a predicate: `GetBlocksOfType(list, b => b.IsSameConstructAs(Me))`. Block groups can likewise span docked grids — filter the same way. Note: merge blocks fuse grids into one, so `IsSameConstructAs` does not separate those.

## Script Organization
Each script lives in its own subfolder. The `.cs` file contains the raw script body only — valid to paste directly into a PB.

## Deployment
Scripts are deployed to the in-game local script browser at:

`C:\Users\cavanhorn\AppData\Roaming\SpaceEngineers\IngameScripts\local\`

Each script gets a folder there whose name matches the script's repo folder name (e.g. `RailCartController`). Inside that folder the script file must be named **`Script.cs`** — that exact name is what makes it appear in the in-game script browser.

Deploy on every change: copy the repo's `<Name>/<Name>.cs` to `IngameScripts\local\<Name>\Script.cs`, and alongside it the publishable companions if present — `modinfo.sb`, `Workshop.txt`, `thumb.png` (kept under their own names). The `.claude/hooks/deploy-ingame-script.py` hook does this automatically on Write/Edit of any of those files.

## Documentation & Comments
The `.cs` file is deployed verbatim into the PB, so in-script comments consume the block's character budget. Keep in-script comments lean — non-obvious "why" only (SE quirks, safety rationale), not narrative usage docs.

Each script carries a `Workshop.txt` alongside it, written in **Steam BBCode**. This is the source of truth for user-facing documentation (overview, setup, commands, config reference, tuning, troubleshooting) and doubles as the Steam Workshop description. Put detailed docs there, not in the script.

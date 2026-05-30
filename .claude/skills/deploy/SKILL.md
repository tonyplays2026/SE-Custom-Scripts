---
name: deploy
description: Deploy SE Programmable Block scripts to the Space Engineers in-game local scripts folder. Use when the user asks to "deploy", "copy to game", or "publish scripts to SE".
disable-model-invocation: true
---

Deploy scripts from this repo to `$env:APPDATA\SpaceEngineers\IngameScripts\local\`.

Each script lives in its own subfolder of the repo (e.g. `AutoCloseDoors\AutoCloseDoors.cs`).
The structure SE expects is `local\<ScriptName>\Script.cs`.

## Steps

1. Find all `.cs` files one level deep under the project root — each subfolder is one script.
2. If `$ARGUMENTS` names a specific script (e.g. `AutoCloseDoors`), deploy only that one. Otherwise deploy all.
3. For each `.cs` file to deploy:
   a. Derive the script name from its parent folder name (e.g. `AutoCloseDoors`)
   b. Ensure `$env:APPDATA\SpaceEngineers\IngameScripts\local\<ScriptName>\` exists (create if needed)
   c. Copy the `.cs` file to that folder as `Script.cs`, overwriting any existing file
4. Report each file deployed as `<source> → <destination>`, then confirm the total count.

Use PowerShell for all file operations.

#!/usr/bin/env python
"""PostToolUse hook: deploy a repo PB script to the in-game local scripts folder.

When a Write/Edit touches a script file of the form <Name>/<Name>.cs in this
repo, copy it to:

    %APPDATA%\\SpaceEngineers\\IngameScripts\\local\\<Name>\\Script.cs

so it shows up in the in-game Programmable Block script browser. The file must
be named Script.cs for the game to list it. Non-matching edits (ExampleConfig,
helper .cs, etc.) are ignored. See CLAUDE.md -> Deployment.
"""
import json
import os
import shutil
import sys


def main():
    try:
        data = json.load(sys.stdin)
    except Exception:
        return

    tool_input = data.get("tool_input") or {}
    fp = tool_input.get("file_path") or ""
    if not fp:
        return

    fp = os.path.normpath(fp)
    stem, ext = os.path.splitext(fp)
    if ext.lower() != ".cs":
        return

    # Only deploy the canonical script file: <Name>/<Name>.cs
    name = os.path.basename(stem)
    parent = os.path.basename(os.path.dirname(fp))
    if not name or name != parent:
        return

    appdata = os.environ.get("APPDATA")
    if not appdata:
        print(json.dumps({"systemMessage": "Deploy skipped: APPDATA not set."}))
        return

    dest_dir = os.path.join(appdata, "SpaceEngineers", "IngameScripts", "local", name)
    dest = os.path.join(dest_dir, "Script.cs")
    try:
        if not os.path.isdir(dest_dir):
            os.makedirs(dest_dir)
        shutil.copyfile(fp, dest)
    except Exception as e:
        print(json.dumps({"systemMessage": "Deploy FAILED for " + name + ": " + str(e)}))
        return

    print(json.dumps({"systemMessage": "Deployed " + name + " -> IngameScripts/local/" + name + "/Script.cs"}))


if __name__ == "__main__":
    main()

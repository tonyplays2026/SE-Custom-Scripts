#!/usr/bin/env python
"""PostToolUse hook: deploy a repo PB script folder to the in-game local folder.

When a Write/Edit touches a deployable file in a script folder (a repo subfolder
<Name> that contains <Name>.cs), sync that folder's publishable files to:

    %APPDATA%\\SpaceEngineers\\IngameScripts\\local\\<Name>\\

  <Name>.cs     -> Script.cs    (the name the game lists in the script browser)
  modinfo.sb    -> modinfo.sb
  Workshop.txt  -> Workshop.txt
  thumb.png     -> thumb.png

Only files that exist are copied. Editing any one of them re-syncs the set so
the local folder stays publish-ready. Other edits (ExampleConfig.ini, .claude,
etc.) are ignored. See CLAUDE.md -> Deployment.
"""
import json
import os
import shutil
import sys

COMPANIONS = ["modinfo.sb", "Workshop.txt", "thumb.png"]


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

    folder = os.path.dirname(fp)
    name = os.path.basename(folder)
    if not name:
        return
    script_cs = os.path.join(folder, name + ".cs")
    if not os.path.isfile(script_cs):
        return  # not a script folder (needs <Name>/<Name>.cs)

    # Only deploy when a publishable file changed (not ExampleConfig.ini, etc.).
    if os.path.basename(fp) not in [name + ".cs"] + COMPANIONS:
        return

    appdata = os.environ.get("APPDATA")
    if not appdata:
        print(json.dumps({"systemMessage": "Deploy skipped: APPDATA not set."}))
        return

    dest_dir = os.path.join(appdata, "SpaceEngineers", "IngameScripts", "local", name)
    try:
        if not os.path.isdir(dest_dir):
            os.makedirs(dest_dir)
        copied = ["Script.cs"]
        shutil.copyfile(script_cs, os.path.join(dest_dir, "Script.cs"))
        for f in COMPANIONS:
            src = os.path.join(folder, f)
            if os.path.isfile(src):
                shutil.copyfile(src, os.path.join(dest_dir, f))
                copied.append(f)
    except Exception as e:
        print(json.dumps({"systemMessage": "Deploy FAILED for " + name + ": " + str(e)}))
        return

    print(json.dumps({"systemMessage": "Deployed " + name + ": " + ", ".join(copied)}))


if __name__ == "__main__":
    main()

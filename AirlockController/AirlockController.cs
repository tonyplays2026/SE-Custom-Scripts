// Airlock Controller.
// Each block group named with the configured prefix is one airlock: air vents
// plus interior and exterior doors. Only one side is ever unlocked. Buttons run
// this block with "pressurize <name>" / "depressurize <name>" / "toggle <name>"
// so the doors can be shut before the pressure moves.

// ---- Configuration (parsed from Custom Data [Airlocks]) --------------------
string _groupPrefix = "Airlock";
string _interiorKeyword = "Interior";
string _exteriorKeyword = "Exterior";
double _closeDelay = 1.0;
double _pressurizedLevel = 0.9;
double _depressurizedLevel = 0.05;
double _stallTimeout = 10.0;
double _stallEpsilon = 0.01;
double _refreshInterval = 30.0;
bool _sameConstructOnly = true;
string _statusPanelName = "";

const double DISPLAY_INTERVAL = 1.0;

// ---- Model -----------------------------------------------------------------
class DoorState
{
    public IMyDoor Door;
    public double OpenedAt; // -1 when the door is not standing open
    public DoorState(IMyDoor door)
    {
        Door = door;
        OpenedAt = -1.0;
    }
}

class Airlock
{
    public string GroupName;
    public string Name;
    public string Error;

    public List<IMyAirVent> Vents = new List<IMyAirVent>();
    public List<DoorState> Interior = new List<DoorState>();
    public List<DoorState> Exterior = new List<DoorState>();
    public List<IMyTextPanel> Panels = new List<IMyTextPanel>();

    // Cycle state — carried across refreshes, used from increment 3 onward.
    public bool TargetDepressurized;
    public bool Cycling;
    public bool Stalled;
    public bool Initialized;
    public double LastProgressO2;
    public double LastProgressTime;
}

// ---- State -----------------------------------------------------------------
Dictionary<string, Airlock> _airlocks = new Dictionary<string, Airlock>();
List<Airlock> _ordered = new List<Airlock>();
List<string> _stale = new List<string>();
double _elapsed = 0.0;
double _lastRefresh = -99999.0;
double _lastDisplay = -99999.0;
IMyTextSurface _pbSurface;
IMyTextPanel _statusPanel;

// Scratch buffers, reused every scan to keep allocation off the tick path.
List<IMyBlockGroup> _groups = new List<IMyBlockGroup>();
List<IMyAirVent> _foundVents = new List<IMyAirVent>();
List<IMyDoor> _foundDoors = new List<IMyDoor>();
List<IMyTextPanel> _foundPanels = new List<IMyTextPanel>();
List<IMyDoor> _interiorDoors = new List<IMyDoor>();
List<IMyDoor> _exteriorDoors = new List<IMyDoor>();
StringBuilder _sb = new StringBuilder();
StringBuilder _err = new StringBuilder();

public Program()
{
    _pbSurface = Me.GetSurface(0);
    _pbSurface.ContentType = ContentType.TEXT_AND_IMAGE;
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
}

public void Save() { } // Nothing to persist — state is re-derived from the vents.

public void Main(string argument, UpdateType updateSource)
{
    _elapsed += Runtime.TimeSinceLastRun.TotalSeconds;

    // Config is re-read on the same cadence as the scan so thresholds can be
    // tuned in Custom Data without recompiling.
    if (_elapsed - _lastRefresh >= _refreshInterval)
    {
        ParseConfig();
        RefreshAirlocks();
        _lastRefresh = _elapsed;
    }

    if (!string.IsNullOrWhiteSpace(argument) &&
        (updateSource & (UpdateType.Trigger | UpdateType.Terminal | UpdateType.Script)) != 0)
        HandleCommand(argument.Trim());

    for (int i = 0; i < _ordered.Count; i++) UpdateAirlock(_ordered[i]);

    if (_elapsed - _lastDisplay >= DISPLAY_INTERVAL)
    {
        UpdateDisplays();
        _lastDisplay = _elapsed;
    }
}

// ---- Commands --------------------------------------------------------------

void HandleCommand(string argument)
{
    int split = argument.IndexOf(' ');
    string verb = split < 0 ? argument : argument.Substring(0, split);
    string name = split < 0 ? "" : argument.Substring(split + 1).Trim();

    bool depressurize = false;
    bool toggle = false;
    if (verb.Equals("depressurize", StringComparison.OrdinalIgnoreCase)) depressurize = true;
    else if (verb.Equals("pressurize", StringComparison.OrdinalIgnoreCase)) depressurize = false;
    else if (verb.Equals("toggle", StringComparison.OrdinalIgnoreCase)) toggle = true;
    else { Echo("Unknown command: " + verb); return; }

    Airlock airlock = FindAirlock(name);
    if (airlock == null)
    {
        Echo(name.Length == 0
            ? "Command needs an airlock name."
            : "No airlock matching \"" + name + "\".");
        return;
    }
    if (airlock.Error != null) { Echo(airlock.Name + ": " + airlock.Error); return; }
    if (airlock.Cycling) { Echo(airlock.Name + " is cycling - command ignored."); return; }

    IMyAirVent vent = ActiveVent(airlock);
    if (vent == null) { Echo(airlock.Name + ": no functional Air Vent."); return; }

    if (toggle) depressurize = !airlock.TargetDepressurized;

    // Already settled where it was asked to go. Repeating the command while
    // stalled is a deliberate retry, so that case falls through.
    if (depressurize == airlock.TargetDepressurized && !airlock.Stalled &&
        TargetReached(airlock, vent.GetOxygenLevel())) return;

    BeginCycle(airlock, depressurize);
}

Airlock FindAirlock(string name)
{
    if (name.Length == 0) return _ordered.Count == 1 ? _ordered[0] : null;
    for (int i = 0; i < _ordered.Count; i++)
        if (_ordered[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return _ordered[i];
    for (int i = 0; i < _ordered.Count; i++)
        if (_ordered[i].GroupName.Equals(name, StringComparison.OrdinalIgnoreCase)) return _ordered[i];
    return null;
}

// ---- Cycle and door control ------------------------------------------------

void UpdateAirlock(Airlock airlock)
{
    IMyAirVent vent = ActiveVent(airlock);

    // A broken airlock is a sealed airlock.
    if (airlock.Error != null || vent == null)
    {
        ApplySide(airlock.Interior, false);
        ApplySide(airlock.Exterior, false);
        return;
    }

    double oxygen = vent.GetOxygenLevel();

    if (airlock.Cycling) { RunCycle(airlock, vent, oxygen); return; }

    // Settled, so a vent toggled from its own terminal is a real instruction.
    if (vent.Depressurize != airlock.TargetDepressurized)
    {
        BeginCycle(airlock, vent.Depressurize);
        RunCycle(airlock, vent, oxygen);
        return;
    }

    if (airlock.Stalled)
    {
        // The unlocked door finishes what the vent could not — exterior dumps
        // the remaining air, interior equalises from the base. Once the level
        // actually arrives the airlock is simply normal again.
        if (!TargetReached(airlock, oxygen))
        {
            ApplySide(airlock.Interior, !airlock.TargetDepressurized);
            ApplySide(airlock.Exterior, airlock.TargetDepressurized);
            return;
        }
        airlock.Stalled = false;
    }
    else if (!TargetReached(airlock, oxygen))
    {
        // Sitting between the thresholds with both sides locked is a trap; drive
        // back to the target, which either completes or trips the stall watchdog.
        BeginCycle(airlock, airlock.TargetDepressurized);
        RunCycle(airlock, vent, oxygen);
        return;
    }

    ApplySide(airlock.Interior, oxygen >= _pressurizedLevel);
    ApplySide(airlock.Exterior, oxygen <= _depressurizedLevel);
}

void BeginCycle(Airlock airlock, bool depressurize)
{
    airlock.TargetDepressurized = depressurize;
    airlock.Cycling = true;
    airlock.Stalled = false;
    airlock.LastProgressO2 = -1.0; // seeded on the first tick the chamber is shut
    airlock.LastProgressTime = _elapsed;
}

void RunCycle(Airlock airlock, IMyAirVent vent, double oxygen)
{
    // Doors first, every tick: nothing may move while the pressure is changing,
    // and a terminal toggle mid-cycle is overridden rather than obeyed.
    ApplySide(airlock.Interior, false);
    ApplySide(airlock.Exterior, false);

    if (!SideClosed(airlock.Interior) || !SideClosed(airlock.Exterior))
    {
        // The vent is not touched until the chamber is actually shut, and the
        // stall clock starts with it so a slow hangar door cannot burn it.
        airlock.LastProgressO2 = oxygen;
        airlock.LastProgressTime = _elapsed;
        return;
    }

    SetVents(airlock, airlock.TargetDepressurized);

    if (TargetReached(airlock, oxygen))
    {
        airlock.Cycling = false;
        airlock.Stalled = false;
        return;
    }

    // A chamber that cannot seal will never pressurise, so do not wait it out.
    if (!airlock.TargetDepressurized && !vent.CanPressurize)
    {
        airlock.Cycling = false;
        airlock.Stalled = true;
        return;
    }

    if (airlock.LastProgressO2 < 0.0) airlock.LastProgressO2 = oxygen;

    // Progress accumulates across ticks — a slow chamber may need several before
    // it clears the epsilon, and resetting per tick would call that a stall.
    double moved = airlock.TargetDepressurized
        ? airlock.LastProgressO2 - oxygen
        : oxygen - airlock.LastProgressO2;
    if (moved >= _stallEpsilon)
    {
        airlock.LastProgressO2 = oxygen;
        airlock.LastProgressTime = _elapsed;
    }
    else if (_elapsed - airlock.LastProgressTime >= _stallTimeout)
    {
        airlock.Cycling = false;
        airlock.Stalled = true; // vent stays on target and keeps trying
    }
}

bool TargetReached(Airlock airlock, double oxygen)
{
    return airlock.TargetDepressurized
        ? oxygen <= _depressurizedLevel
        : oxygen >= _pressurizedLevel;
}

// Damaged doors are skipped rather than blocking forever: the vent then fails to
// reach its target and the stall watchdog releases the chamber.
bool SideClosed(List<DoorState> side)
{
    for (int i = 0; i < side.Count; i++)
    {
        IMyDoor door = side[i].Door;
        if (door.IsFunctional && door.Status != DoorStatus.Closed) return false;
    }
    return true;
}

void SetVents(Airlock airlock, bool depressurize)
{
    for (int i = 0; i < airlock.Vents.Count; i++)
        if (airlock.Vents[i].Depressurize != depressurize)
            airlock.Vents[i].Depressurize = depressurize;
}

void ApplySide(List<DoorState> side, bool unlocked)
{
    for (int i = 0; i < side.Count; i++) ApplyDoor(side[i], unlocked);
}

void ApplyDoor(DoorState state, bool unlocked)
{
    IMyDoor door = state.Door;
    if (!door.IsFunctional) return;
    DoorStatus status = door.Status;

    if (unlocked)
    {
        if (!door.Enabled) door.Enabled = true;

        // The hold starts at full open, not at Opening — a 1s delay measured
        // from the start of travel would begin closing the moment it finished.
        if (status == DoorStatus.Open)
        {
            if (state.OpenedAt < 0.0) state.OpenedAt = _elapsed;
            else if (_elapsed - state.OpenedAt >= _closeDelay) door.CloseDoor();
        }
        else if (status != DoorStatus.Opening) state.OpenedAt = -1.0;
        return;
    }

    state.OpenedAt = -1.0;

    // A disabled door cannot move. Close it first and only cut power once it
    // reports shut, or it freezes part-open and the chamber leaks for good.
    if (status == DoorStatus.Open || status == DoorStatus.Opening)
    {
        if (!door.Enabled) door.Enabled = true;
        door.CloseDoor();
    }
    else if (status == DoorStatus.Closed && door.Enabled) door.Enabled = false;
}

// ---- Configuration ---------------------------------------------------------

void ParseConfig()
{
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();

    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result))
    {
        Echo("CONFIG WARNING: could not parse Custom Data (" + result.Error + "). Using defaults.");
        return;
    }

    const string S = "Airlocks";
    _groupPrefix = ini.Get(S, "GroupPrefix").ToString(_groupPrefix);
    _interiorKeyword = ini.Get(S, "InteriorKeyword").ToString(_interiorKeyword);
    _exteriorKeyword = ini.Get(S, "ExteriorKeyword").ToString(_exteriorKeyword);
    _closeDelay = ini.Get(S, "CloseDelay").ToDouble(_closeDelay);
    _pressurizedLevel = ini.Get(S, "PressurizedLevel").ToDouble(_pressurizedLevel);
    _depressurizedLevel = ini.Get(S, "DepressurizedLevel").ToDouble(_depressurizedLevel);
    _stallTimeout = ini.Get(S, "StallTimeout").ToDouble(_stallTimeout);
    _stallEpsilon = ini.Get(S, "StallEpsilon").ToDouble(_stallEpsilon);
    _refreshInterval = ini.Get(S, "RefreshInterval").ToDouble(_refreshInterval);
    _sameConstructOnly = ini.Get(S, "SameConstructOnly").ToBoolean(_sameConstructOnly);
    _statusPanelName = ini.Get(S, "StatusPanel").ToString(_statusPanelName);

    if (string.IsNullOrWhiteSpace(_groupPrefix)) _groupPrefix = "Airlock";
    if (string.IsNullOrWhiteSpace(_interiorKeyword)) _interiorKeyword = "Interior";
    if (string.IsNullOrWhiteSpace(_exteriorKeyword)) _exteriorKeyword = "Exterior";
    if (_closeDelay < 0.0) _closeDelay = 1.0;
    if (_stallTimeout <= 0.0) _stallTimeout = 10.0;
    if (_stallEpsilon <= 0.0) _stallEpsilon = 0.01;
    if (_refreshInterval < 1.0) _refreshInterval = 30.0;

    // Overlapping thresholds would let both sides unlock at once.
    if (_depressurizedLevel >= _pressurizedLevel)
    {
        Echo("CONFIG WARNING: DepressurizedLevel must be below PressurizedLevel. Using defaults.");
        _pressurizedLevel = 0.9;
        _depressurizedLevel = 0.05;
    }
}

void WriteConfigTemplate()
{
    Me.CustomData =
        "[Airlocks]\n" +
        "; Block groups whose name starts with this are treated as airlocks.\n" +
        "GroupPrefix=Airlock\n" +
        "; Substrings identifying each door's side, matched in the door's own name.\n" +
        "InteriorKeyword=Interior\n" +
        "ExteriorKeyword=Exterior\n" +
        "; Seconds an unlocked door may stay open before it is closed again.\n" +
        "CloseDelay=1\n" +
        "; Oxygen level at/above which the interior side is usable.\n" +
        "PressurizedLevel=0.9\n" +
        "; Oxygen level at/below which the exterior side is usable.\n" +
        "DepressurizedLevel=0.05\n" +
        "; Seconds without oxygen progress before a cycle is called stalled.\n" +
        "StallTimeout=10\n" +
        "; Oxygen movement toward the target that counts as progress.\n" +
        "StallEpsilon=0.01\n" +
        "; Seconds between re-scans of the grid for airlock groups.\n" +
        "RefreshInterval=30\n" +
        "; Ignore blocks on grids docked by connector.\n" +
        "SameConstructOnly=true\n" +
        "; Optional LCD name for the all-airlock summary. Leave blank for none.\n" +
        "StatusPanel=\n";
}

// ---- Discovery -------------------------------------------------------------

void RefreshAirlocks()
{
    _groups.Clear();
    GridTerminalSystem.GetBlockGroups(_groups,
        g => g.Name.StartsWith(_groupPrefix, StringComparison.OrdinalIgnoreCase));

    _ordered.Clear();
    for (int i = 0; i < _groups.Count; i++)
    {
        IMyBlockGroup group = _groups[i];
        Airlock airlock;
        if (!_airlocks.TryGetValue(group.Name, out airlock))
        {
            airlock = new Airlock();
            airlock.GroupName = group.Name;
            _airlocks[group.Name] = airlock;
        }
        airlock.Name = FriendlyName(group.Name);
        Populate(airlock, group);
        _ordered.Add(airlock);
    }

    // Drop airlocks whose group was renamed or deleted.
    _stale.Clear();
    foreach (KeyValuePair<string, Airlock> pair in _airlocks)
        if (!_ordered.Contains(pair.Value)) _stale.Add(pair.Key);
    for (int i = 0; i < _stale.Count; i++) _airlocks.Remove(_stale[i]);

    _statusPanel = null;
    if (!string.IsNullOrWhiteSpace(_statusPanelName))
    {
        _statusPanel = GridTerminalSystem.GetBlockWithName(_statusPanelName) as IMyTextPanel;
        if (_statusPanel != null) _statusPanel.ContentType = ContentType.TEXT_AND_IMAGE;
    }
}

void Populate(Airlock airlock, IMyBlockGroup group)
{
    _foundVents.Clear();
    _foundDoors.Clear();
    _foundPanels.Clear();
    group.GetBlocksOfType<IMyAirVent>(_foundVents, b => InScope(b));
    group.GetBlocksOfType<IMyDoor>(_foundDoors, b => InScope(b));
    group.GetBlocksOfType<IMyTextPanel>(_foundPanels, b => InScope(b));

    airlock.Vents.Clear();
    for (int i = 0; i < _foundVents.Count; i++) airlock.Vents.Add(_foundVents[i]);

    // Adopt whatever the vent is already doing the first time an airlock is
    // seen, so a recompile never kicks every chamber on the grid into a cycle.
    if (!airlock.Initialized && airlock.Vents.Count > 0)
    {
        airlock.TargetDepressurized = airlock.Vents[0].Depressurize;
        airlock.Initialized = true;
    }

    airlock.Panels.Clear();
    for (int i = 0; i < _foundPanels.Count; i++)
    {
        _foundPanels[i].ContentType = ContentType.TEXT_AND_IMAGE;
        airlock.Panels.Add(_foundPanels[i]);
    }

    _interiorDoors.Clear();
    _exteriorDoors.Clear();
    int ambiguous = 0;
    int unclassified = 0;
    for (int i = 0; i < _foundDoors.Count; i++)
    {
        IMyDoor door = _foundDoors[i];
        bool interior = HasKeyword(door.CustomName, _interiorKeyword);
        bool exterior = HasKeyword(door.CustomName, _exteriorKeyword);
        if (interior && exterior) ambiguous++;
        else if (interior) _interiorDoors.Add(door);
        else if (exterior) _exteriorDoors.Add(door);
        else unclassified++;
    }

    SyncDoors(airlock.Interior, _interiorDoors);
    SyncDoors(airlock.Exterior, _exteriorDoors);

    _err.Clear();
    if (airlock.Vents.Count == 0) AppendError("no Air Vent in group");
    if (_interiorDoors.Count == 0) AppendError("no " + _interiorKeyword + " door in group");
    if (_exteriorDoors.Count == 0) AppendError("no " + _exteriorKeyword + " door in group");
    if (ambiguous > 0) AppendError(ambiguous + " door(s) match both keywords");
    if (unclassified > 0) AppendError(unclassified + " door(s) match neither keyword");
    airlock.Error = _err.Length == 0 ? null : _err.ToString();
}

// Rebuild a side's door list while preserving the open-timestamp of doors that
// are still present, so a rescan never restarts a close delay.
void SyncDoors(List<DoorState> side, List<IMyDoor> found)
{
    for (int i = side.Count - 1; i >= 0; i--)
        if (!found.Contains(side[i].Door)) side.RemoveAt(i);

    for (int i = 0; i < found.Count; i++)
    {
        bool known = false;
        for (int j = 0; j < side.Count; j++)
            if (side[j].Door == found[i]) { known = true; break; }
        if (!known) side.Add(new DoorState(found[i]));
    }
}

void AppendError(string message)
{
    if (_err.Length > 0) _err.Append("; ");
    _err.Append(message);
}

bool InScope(IMyTerminalBlock block)
{
    return !_sameConstructOnly || block.IsSameConstructAs(Me);
}

bool HasKeyword(string name, string keyword)
{
    return name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
}

string FriendlyName(string groupName)
{
    if (groupName.Length > _groupPrefix.Length &&
        groupName.StartsWith(_groupPrefix, StringComparison.OrdinalIgnoreCase))
    {
        string trimmed = groupName.Substring(_groupPrefix.Length).Trim().TrimStart('-', ':', '_').Trim();
        if (trimmed.Length > 0) return trimmed;
    }
    return groupName;
}

IMyAirVent ActiveVent(Airlock airlock)
{
    for (int i = 0; i < airlock.Vents.Count; i++)
        if (airlock.Vents[i].IsFunctional) return airlock.Vents[i];
    return null;
}

// ---- Display ---------------------------------------------------------------

void UpdateDisplays()
{
    _sb.Clear();
    _sb.AppendLine("=== Airlock Controller ===");
    if (_ordered.Count == 0)
        _sb.AppendLine("No block groups starting with \"" + _groupPrefix + "\".");

    for (int i = 0; i < _ordered.Count; i++)
    {
        Airlock airlock = _ordered[i];
        IMyAirVent vent = ActiveVent(airlock);
        _sb.Append(airlock.Name.PadRight(10));

        if (airlock.Error != null)
        {
            _sb.AppendLine("[ERROR] " + airlock.Error);
            WritePanels(airlock, "O2 --");
            continue;
        }
        if (vent == null)
        {
            _sb.AppendLine("[ERROR] no functional Air Vent");
            WritePanels(airlock, "O2 --");
            continue;
        }

        double oxygen = vent.GetOxygenLevel();
        string percent = Percent(oxygen);
        _sb.Append("O2 ").Append(percent.PadLeft(4)).Append("   ").AppendLine(StatusText(airlock, oxygen));
        WritePanels(airlock, "O2 " + percent);
    }

    string text = _sb.ToString();
    Echo(text);
    _pbSurface.WriteText(text);
    if (_statusPanel != null) _statusPanel.WriteText(text);
}

// In-group panels are deliberately one short line so they fit a small LCD
// mounted beside the door; lock state is already visible on the doors.
void WritePanels(Airlock airlock, string line)
{
    for (int i = 0; i < airlock.Panels.Count; i++)
        airlock.Panels[i].WriteText(line);
}

string StatusText(Airlock airlock, double oxygen)
{
    // "busy" matters: without it a player who pressed the wrong button just sees
    // an airlock that will not respond, with no reason given.
    if (airlock.Cycling)
        return (airlock.TargetDepressurized ? "DEPRESSURIZING" : "PRESSURIZING") + " - busy";
    if (airlock.Stalled)
        return "STALLED - " + (airlock.TargetDepressurized ? "EXT" : "INT") + " unlocked";
    if (oxygen >= _pressurizedLevel) return "INT unlocked";
    if (oxygen <= _depressurizedLevel) return "EXT unlocked";
    return "both locked";
}

string Percent(double oxygen)
{
    return ((int)Math.Round(oxygen * 100.0)).ToString() + "%";
}

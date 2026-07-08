// Track Cart Controller — Iteration 3: cruise, approach/dock, auto back-and-forth.
// Holds a target speed on a track-guided wheeled cart (steering handled by the
// track); drives up grades, brakes down grades, drives to a captured stop and
// docks, and can run to the other stop automatically ('next'). Setup, commands,
// config, tuning: see Workshop.txt.

// ---- Configuration (parsed from Custom Data [CartController]) ---------------
double _cruiseSpeed = 10.0;               // target travel speed (m/s)
double _maxSpeed = 15.0;                   // emergency-brake threshold, TOTAL speed (m/s)
double _kp = 0.35;                         // proportional gain: override per m/s of error
double _brakeOverspeed = 2.0;              // m/s above target before wheel brakes engage
int _propulsionSign = 1;                   // global wiring flip; -1 if it drives the wrong way
bool _reverse = false;                     // plain-cruise travel direction chooser
string _driveWheelGroup = "Drive Wheels";  // group of drive wheels (excludes guide wheels)
double _crawlSpeed = 1.0;                   // min approach speed near a stop (m/s)
double _approachDecel = 1.5;               // deceleration used for the approach profile (m/s^2)
double _connectDistance = 2.5;             // distance to a stop at which to crawl & connect (m)

// ---- Discovered blocks -----------------------------------------------------
IMyShipController _controller;
List<DriveWheel> _wheels = new List<DriveWheel>();
List<IMyShipConnector> _connectors = new List<IMyShipConnector>();

// ---- Captured stops (Custom Data [Stops]) ----------------------------------
Dictionary<string, StopInfo> _stops = new Dictionary<string, StopInfo>(StringComparer.OrdinalIgnoreCase);

// ---- Runtime state ---------------------------------------------------------
Mode _mode = Mode.Idle;
string _targetStop = null;
string _setupError = null;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.None;
    ParseConfig();
    Discover();
    RestoreState();
    WriteScreen(DockedScreenText());
}

public void Save()
{
    // Persist active mode + target so a mid-trip world reload can resume.
    Storage = ((int)_mode).ToString() + "|" + (_targetStop ?? "");
}

// On world reload the script's fields reset but the wheels keep their saved
// PropulsionOverride — a dead control loop then means a runaway cart. Resume an
// in-progress trip; otherwise make sure the wheels are left neutral.
private void RestoreState()
{
    bool resumed = false;
    if (_setupError == null && !string.IsNullOrEmpty(Storage))
    {
        string[] parts = Storage.Split('|');
        int m;
        if (parts.Length >= 1 && int.TryParse(parts[0], out m))
        {
            Mode saved = (Mode)m;
            string target = parts.Length > 1 ? parts[1] : "";
            if (saved == Mode.Cruise)
            {
                _mode = Mode.Cruise;
                Runtime.UpdateFrequency = UpdateFrequency.Update1;
                resumed = true;
            }
            else if (saved == Mode.Goto && _stops.ContainsKey(target))
            {
                _mode = Mode.Goto;
                _targetStop = target;
                Runtime.UpdateFrequency = UpdateFrequency.Update1;
                resumed = true;
            }
        }
    }

    if (!resumed) ApplyWheels(0.0, true); // no stale override left coasting
}

public void Main(string argument, UpdateType updateSource)
{
    if ((updateSource & UpdateType.Update1) != 0)
    {
        ControlTick();
        return;
    }

    string raw = (argument ?? "").Trim();
    string lower = raw.ToLowerInvariant();

    if (lower == "start") StartCruise();
    else if (lower == "stop") StopAll("Stopped. Brakes holding.");
    else if (lower == "reload") { ParseConfig(); Discover(); PrintSetup(); }
    else if (lower == "liststops") ListStops();
    else if (lower == "next") StartNext();
    else if (lower.StartsWith("setstop")) CaptureStop(raw.Substring(7).Trim());
    else if (lower.StartsWith("goto")) StartGoto(raw.Substring(4).Trim());
    else PrintSetup();
}

// ---- Commands --------------------------------------------------------------

private void StartCruise()
{
    ParseConfig();
    Discover();
    if (_setupError != null) { Echo("Cannot start."); PrintSetup(); return; }
    _mode = Mode.Cruise;
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
    Echo("Cruising. Target " + _cruiseSpeed.ToString("0.0") + " m/s.");
}

private void StartGoto(string name)
{
    ParseConfig();
    Discover();
    if (_setupError != null) { Echo("Cannot go."); PrintSetup(); return; }
    if (string.IsNullOrWhiteSpace(name)) { Echo("Usage: goto <stop name>"); ListStops(); return; }

    StopInfo stop;
    if (!_stops.TryGetValue(name, out stop))
    {
        Echo("Unknown stop '" + name + "'.");
        ListStops();
        return;
    }
    BeginGoto(stop);
}

private void StartNext()
{
    ParseConfig();
    Discover();
    if (_setupError != null) { Echo("Cannot go."); PrintSetup(); return; }
    if (_stops.Count != 2)
    {
        Echo("'next' needs exactly 2 stops (have " + _stops.Count + "). Use goto <name>.");
        ListStops();
        return;
    }

    StopInfo current = CurrentDockedStop();
    StopInfo target = current != null ? OtherStop(current) : NearestStop();
    if (target == null) { Echo("Could not determine a destination stop."); return; }
    BeginGoto(target);
}

// Begin a run to a stop: verify its connector, release any lock, and depart.
private void BeginGoto(StopInfo stop)
{
    if (FindConnector(stop.ConnectorName) == null)
    {
        Echo("Stop '" + stop.Name + "' uses connector '" + stop.ConnectorName + "', which was not found.");
        return;
    }
    for (int i = 0; i < _connectors.Count; i++)
        if (_connectors[i].Status == MyShipConnectorStatus.Connected) _connectors[i].Disconnect();

    _mode = Mode.Goto;
    _targetStop = stop.Name;
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
    Echo("En route to " + stop.Name + ".");
}

// The stop whose connector is currently locked, or null if not docked.
private StopInfo CurrentDockedStop()
{
    for (int i = 0; i < _connectors.Count; i++)
        if (_connectors[i].Status == MyShipConnectorStatus.Connected)
            foreach (StopInfo s in _stops.Values)
                if (s.ConnectorName == _connectors[i].CustomName) return s;
    return null;
}

// The other of exactly two stops.
private StopInfo OtherStop(StopInfo current)
{
    foreach (StopInfo s in _stops.Values)
        if (!string.Equals(s.Name, current.Name, StringComparison.OrdinalIgnoreCase)) return s;
    return null;
}

// The stop whose connector is closest to its dock point (nearest to re-dock).
private StopInfo NearestStop()
{
    StopInfo best = null;
    double bestDist = double.MaxValue;
    foreach (StopInfo s in _stops.Values)
    {
        IMyShipConnector c = FindConnector(s.ConnectorName);
        if (c == null) continue;
        double d = (s.Pos - c.GetPosition()).Length();
        if (d < bestDist) { bestDist = d; best = s; }
    }
    return best;
}

private void CaptureStop(string name)
{
    ParseConfig();
    Discover();
    if (string.IsNullOrWhiteSpace(name)) { Echo("Usage: setstop <stop name>"); return; }
    if (name.Contains(".")) { Echo("Stop names cannot contain '.'"); return; }
    if (_controller == null) { Echo("No controller found."); return; }

    IMyShipConnector docked = null;
    int connectedCount = 0;
    for (int i = 0; i < _connectors.Count; i++)
    {
        if (_connectors[i].Status == MyShipConnectorStatus.Connected)
        {
            if (docked == null) docked = _connectors[i];
            connectedCount++;
        }
    }
    if (docked == null) { Echo("Dock the cart at the stop first (no connector is connected)."); return; }
    if (connectedCount > 1) Echo("Warning: multiple connectors connected; using '" + docked.CustomName + "'.");

    SaveStop(name, docked.GetPosition(), docked.CustomName);
    ParseConfig(); // reload so _stops reflects the new entry
    Echo("Saved stop '" + name + "' at connector '" + docked.CustomName + "'.");
}

private void ListStops()
{
    if (_stops.Count == 0) { Echo("No stops captured. Dock and run: setstop <name>"); return; }
    Echo("Stops (" + _stops.Count + "):");
    foreach (StopInfo s in _stops.Values)
        Echo("  " + s.Name + "  [" + s.ConnectorName + "]");
}

// ---- Control loop ----------------------------------------------------------

private void ControlTick()
{
    if (_setupError != null || _controller == null || _wheels.Count == 0)
    {
        Discover();
        if (_setupError != null) { StopAll("Setup error, halted: " + _setupError); return; }
    }

    if (_mode == Mode.Cruise) CruiseControl();
    else if (_mode == Mode.Goto) GotoControl();
    else { ApplyWheels(0.0, true); Runtime.UpdateFrequency = UpdateFrequency.None; } // idle: don't coast
}

private void CruiseControl()
{
    int tripDir = _reverse ? -1 : 1;
    double travelSpeed, totalSpeed, command;
    bool brake, emergency;
    DriveTowards(_cruiseSpeed, tripDir, out travelSpeed, out totalSpeed, out command, out brake, out emergency);

    Echo("== Track Cart — CRUISING ==");
    Echo("Target : " + _cruiseSpeed.ToString("0.0") + " m/s" + (_reverse ? "  (reverse)" : ""));
    Echo("Travel : " + travelSpeed.ToString("0.00") + " m/s");
    Echo("Total  : " + totalSpeed.ToString("0.00") + " m/s" + (brake ? "   [BRAKES]" : ""));
    if (emergency) Echo("!! EMERGENCY BRAKE — over MaxSpeed. If wrong way, set PropulsionSign = -1.");
    Echo("Send 'stop' to halt.");
    WriteScreen("CRUISING\n" + travelSpeed.ToString("0") + " m/s");
}

private void GotoControl()
{
    StopInfo stop;
    if (!_stops.TryGetValue(_targetStop, out stop)) { StopAll("Target stop lost."); return; }
    IMyShipConnector conn = FindConnector(stop.ConnectorName);
    if (conn == null) { StopAll("Connector '" + stop.ConnectorName + "' not found."); return; }

    if (conn.Status == MyShipConnectorStatus.Connected)
    {
        ApplyWheels(0.0, true);
        _mode = Mode.Idle;
        Runtime.UpdateFrequency = UpdateFrequency.None;
        Echo("Docked at " + stop.Name + ".");
        if (_stops.Count == 2)
        {
            StopInfo other = OtherStop(stop);
            if (other != null) Echo("Trigger 'next' to depart to " + other.Name + ".");
        }
        WriteScreen(DockedScreenText());
        return;
    }

    Vector3D toStop = stop.Pos - conn.GetPosition();
    double distance = toStop.Length();
    int tripDir = Vector3D.Dot(toStop, _controller.WorldMatrix.Forward) >= 0 ? 1 : -1;

    // Kinematic deceleration profile: slow smoothly as the stop nears.
    double allowed = Math.Sqrt(2.0 * _approachDecel * Math.Max(distance - _connectDistance, 0.0));
    double target = Clamp(allowed, _crawlSpeed, _cruiseSpeed);

    if (conn.Status == MyShipConnectorStatus.Connectable) conn.Connect();

    double travelSpeed, totalSpeed, command;
    bool brake, emergency;
    DriveTowards(target, tripDir, out travelSpeed, out totalSpeed, out command, out brake, out emergency);

    Echo("== Track Cart — GOTO " + stop.Name + " ==");
    Echo("Distance: " + distance.ToString("0.0") + " m");
    Echo("Target  : " + target.ToString("0.00") + " m/s");
    Echo("Travel  : " + travelSpeed.ToString("0.00") + " m/s");
    Echo("Total   : " + totalSpeed.ToString("0.00") + " m/s" + (brake ? "   [BRAKES]" : ""));
    Echo("Connector: " + conn.CustomName + " (" + conn.Status + ")");
    if (emergency) Echo("!! EMERGENCY BRAKE — over MaxSpeed.");
    Echo("Send 'stop' to abort.");
    WriteScreen("EN ROUTE TO\n" + stop.Name + "\n" + distance.ToString("0") + " m");
}

// Shared speed controller: hold 'target' m/s in the chosen track direction.
private void DriveTowards(double target, int tripDir, out double travelSpeed, out double totalSpeed, out double command, out bool brake, out bool emergency)
{
    Vector3D v = _controller.GetShipVelocities().LinearVelocity;
    totalSpeed = v.Length();
    double signedSpeed = Vector3D.Dot(v, _controller.WorldMatrix.Forward);
    travelSpeed = signedSpeed * tripDir;
    double error = target - travelSpeed;
    double overspeed = travelSpeed - target;
    emergency = totalSpeed > _maxSpeed;

    if (emergency) { command = 0.0; brake = true; }
    else if (overspeed > _brakeOverspeed) { command = 0.0; brake = true; }
    else { command = Clamp(_kp * error, -1.0, 1.0); brake = false; }

    ApplyWheels(command * tripDir * _propulsionSign, brake);
}

// baseOverride already folds in tripDir and the wiring flip; SideSign mirrors L/R.
private void ApplyWheels(double baseOverride, bool brake)
{
    for (int i = 0; i < _wheels.Count; i++)
    {
        DriveWheel dw = _wheels[i];
        dw.Wheel.Propulsion = true;
        dw.Wheel.PropulsionOverride = (float)(baseOverride * dw.SideSign);
        dw.Wheel.Brake = brake;
    }
}

private void StopAll(string reason)
{
    _mode = Mode.Idle;
    Runtime.UpdateFrequency = UpdateFrequency.None;
    ApplyWheels(0.0, true);
    if (reason != null) Echo(reason);
    WriteScreen(DockedScreenText());
}

private IMyShipConnector FindConnector(string customName)
{
    for (int i = 0; i < _connectors.Count; i++)
        if (_connectors[i].CustomName == customName) return _connectors[i];
    return null;
}

// ---- Configuration ---------------------------------------------------------

private void ParseConfig()
{
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();

    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result))
    {
        Echo("CONFIG WARNING: could not parse Custom Data (" + result.Error + "). Using defaults.");
        return;
    }

    const string S = "CartController";
    _cruiseSpeed = ini.Get(S, "CruiseSpeed").ToDouble(_cruiseSpeed);
    _maxSpeed = ini.Get(S, "MaxSpeed").ToDouble(_maxSpeed);
    _kp = ini.Get(S, "Kp").ToDouble(_kp);
    _brakeOverspeed = ini.Get(S, "BrakeOverspeed").ToDouble(_brakeOverspeed);
    _propulsionSign = ini.Get(S, "PropulsionSign").ToInt32(_propulsionSign) < 0 ? -1 : 1;
    _reverse = ini.Get(S, "Reverse").ToBoolean(_reverse);
    _driveWheelGroup = ini.Get(S, "DriveWheelGroup").ToString(_driveWheelGroup);
    _crawlSpeed = ini.Get(S, "CrawlSpeed").ToDouble(_crawlSpeed);
    _approachDecel = ini.Get(S, "ApproachDecel").ToDouble(_approachDecel);
    _connectDistance = ini.Get(S, "ConnectDistance").ToDouble(_connectDistance);

    // Keep control values sane.
    if (_maxSpeed <= _cruiseSpeed) _maxSpeed = _cruiseSpeed + 5.0;
    if (_kp <= 0.0) _kp = 0.35;
    if (_crawlSpeed <= 0.0) _crawlSpeed = 0.5;
    if (_approachDecel <= 0.0) _approachDecel = 1.0;
    if (_connectDistance < 0.0) _connectDistance = 2.0;

    LoadStops(ini);
}

private void LoadStops(MyIni ini)
{
    _stops.Clear();
    const string S = "Stops";
    List<MyIniKey> keys = new List<MyIniKey>();
    ini.GetKeys(S, keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string name = keys[i].Name;
        if (name.Contains(".")) continue; // coordinate sub-key
        string connector = ini.Get(S, name).ToString("");
        double x = ini.Get(S, name + ".X").ToDouble();
        double y = ini.Get(S, name + ".Y").ToDouble();
        double z = ini.Get(S, name + ".Z").ToDouble();
        _stops[name] = new StopInfo(name, new Vector3D(x, y, z), connector);
    }
}

private void SaveStop(string name, Vector3D pos, string connector)
{
    MyIni ini = new MyIni();
    MyIniParseResult result;
    ini.TryParse(Me.CustomData, out result);
    const string S = "Stops";
    ini.Set(S, name, connector);
    ini.Set(S, name + ".X", pos.X);
    ini.Set(S, name + ".Y", pos.Y);
    ini.Set(S, name + ".Z", pos.Z);
    Me.CustomData = ini.ToString();
}

private void WriteConfigTemplate()
{
    MyIni ini = new MyIni();
    const string S = "CartController";
    ini.Set(S, "CruiseSpeed", _cruiseSpeed);
    ini.Set(S, "MaxSpeed", _maxSpeed);
    ini.Set(S, "Kp", _kp);
    ini.Set(S, "BrakeOverspeed", _brakeOverspeed);
    ini.Set(S, "PropulsionSign", _propulsionSign);
    ini.Set(S, "Reverse", _reverse);
    ini.Set(S, "DriveWheelGroup", _driveWheelGroup);
    ini.Set(S, "CrawlSpeed", _crawlSpeed);
    ini.Set(S, "ApproachDecel", _approachDecel);
    ini.Set(S, "ConnectDistance", _connectDistance);
    ini.SetComment(S, "CruiseSpeed", "Target travel speed (m/s).");
    ini.SetComment(S, "MaxSpeed", "Emergency-brake threshold on total speed (m/s).");
    ini.SetComment(S, "Kp", "Proportional gain: propulsion override per m/s of error.");
    ini.SetComment(S, "BrakeOverspeed", "m/s above target before wheel brakes engage.");
    ini.SetComment(S, "PropulsionSign", "Global wiring flip. Set -1 if it emergency-brakes / drives the wrong way.");
    ini.SetComment(S, "Reverse", "Plain-cruise travel direction (true/false). Not used by goto.");
    ini.SetComment(S, "DriveWheelGroup", "Block group holding ONLY the drive wheels (exclude alignment wheels).");
    ini.SetComment(S, "CrawlSpeed", "Minimum approach speed near a stop (m/s).");
    ini.SetComment(S, "ApproachDecel", "Deceleration used to plan the approach slowdown (m/s^2).");
    ini.SetComment(S, "ConnectDistance", "Distance to the stop at which to crawl and connect (m).");
    Me.CustomData = ini.ToString();
}

// ---- Block discovery -------------------------------------------------------

private void Discover()
{
    _setupError = null;
    _controller = null;
    _wheels.Clear();
    _connectors.Clear();

    // Scope every lookup to THIS cart. The terminal system spans grids docked
    // by connector (the base and any other carts), so IsSameConstructAs(Me)
    // restricts results to the cart + its mechanical subgrids only.
    GridTerminalSystem.GetBlocksOfType(_connectors, c => c.IsSameConstructAs(Me));

    // Prefer a Remote Control, else any ship controller on this cart.
    List<IMyShipController> controllers = new List<IMyShipController>();
    GridTerminalSystem.GetBlocksOfType(controllers, c => c.IsSameConstructAs(Me));
    for (int i = 0; i < controllers.Count; i++)
        if (controllers[i] is IMyRemoteControl) { _controller = controllers[i]; break; }
    if (_controller == null && controllers.Count > 0) _controller = controllers[0];
    if (_controller == null) { _setupError = "No ship controller (remote/cockpit) found."; return; }

    if (string.IsNullOrWhiteSpace(_driveWheelGroup)) { _setupError = "DriveWheelGroup is not set in Custom Data."; return; }
    IMyBlockGroup grp = GridTerminalSystem.GetBlockGroupWithName(_driveWheelGroup);
    if (grp == null) { _setupError = "Drive wheel group '" + _driveWheelGroup + "' not found."; return; }
    List<IMyMotorSuspension> raw = new List<IMyMotorSuspension>();
    grp.GetBlocksOfType(raw, w => w.IsSameConstructAs(Me)); // group can span docked grids too
    if (raw.Count == 0) { _setupError = "Group '" + _driveWheelGroup + "' contains no wheel suspensions."; return; }

    // Side sign from position vs the controller's Right axis: left +1, right -1
    // (mirrored wheels need opposite override signs).
    Vector3D ctrlPos = _controller.GetPosition();
    Vector3D right = _controller.WorldMatrix.Right;
    for (int i = 0; i < raw.Count; i++)
    {
        double side = Vector3D.Dot(raw[i].GetPosition() - ctrlPos, right);
        float sideSign = side < 0 ? 1f : -1f;
        _wheels.Add(new DriveWheel(raw[i], sideSign));
    }
}

// ---- Status output ---------------------------------------------------------

private void PrintSetup()
{
    Echo("== Track Cart Controller (Iteration 3) ==");
    Echo("Mode: " + _mode);
    Echo("");
    Echo("Config:");
    Echo("  CruiseSpeed     = " + _cruiseSpeed.ToString("0.0") + " m/s");
    Echo("  MaxSpeed        = " + _maxSpeed.ToString("0.0") + " m/s");
    Echo("  Kp              = " + _kp.ToString("0.00"));
    Echo("  BrakeOverspeed  = " + _brakeOverspeed.ToString("0.0") + " m/s");
    Echo("  CrawlSpeed      = " + _crawlSpeed.ToString("0.0") + " m/s");
    Echo("  ApproachDecel   = " + _approachDecel.ToString("0.0") + " m/s2");
    Echo("  ConnectDistance = " + _connectDistance.ToString("0.0") + " m");
    Echo("  PropulsionSign  = " + _propulsionSign + "   Reverse = " + _reverse);
    Echo("  DriveWheelGroup = " + _driveWheelGroup);
    Echo("");
    if (_setupError != null)
    {
        Echo("SETUP ERROR: " + _setupError);
    }
    else
    {
        int left = 0, rightCount = 0;
        for (int i = 0; i < _wheels.Count; i++)
            if (_wheels[i].SideSign > 0) left++; else rightCount++;
        Echo("Controller : " + _controller.CustomName);
        Echo("Drive wheels: " + _wheels.Count + "  (left " + left + " / right " + rightCount + ")");
        Echo("Connectors : " + _connectors.Count);
        for (int i = 0; i < _connectors.Count; i++)
            Echo("  - " + _connectors[i].CustomName + " (" + _connectors[i].Status + ")");
    }
    Echo("");
    ListStops();
    if (_setupError == null && _stops.Count == 2)
    {
        StopInfo current = CurrentDockedStop();
        if (current != null)
        {
            StopInfo other = OtherStop(current);
            Echo("Docked at " + current.Name + (other != null ? " — 'next' departs to " + other.Name : "") + ".");
        }
    }
    Echo("");
    Echo("Commands: next | goto <name> | start | stop | setstop <name> | liststops | reload");
    WriteScreen(DockedScreenText());
}

// ---- Helpers ---------------------------------------------------------------

private static double Clamp(double x, double lo, double hi)
{
    if (x < lo) return lo;
    if (x > hi) return hi;
    return x;
}

// Draw a short status to the PB's own screen.
private void WriteScreen(string text)
{
    if (Me.SurfaceCount == 0) return;
    IMyTextSurface s = Me.GetSurface(0);
    s.ContentType = ContentType.TEXT_AND_IMAGE;
    s.Alignment = TextAlignment.CENTER;
    s.FontSize = 1.6f;
    s.WriteText(text, false);
}

// Screen text for the idle/docked state.
private string DockedScreenText()
{
    StopInfo current = CurrentDockedStop();
    if (current == null) return "IDLE";
    StopInfo other = _stops.Count == 2 ? OtherStop(current) : null;
    return "DOCKED\n" + current.Name + (other != null ? "\n\nNext: " + other.Name : "");
}

private enum Mode { Idle, Cruise, Goto }

// A drive wheel with its side sign (left = +1, right = -1).
private class DriveWheel
{
    public IMyMotorSuspension Wheel;
    public float SideSign;

    public DriveWheel(IMyMotorSuspension wheel, float sideSign)
    {
        Wheel = wheel;
        SideSign = sideSign;
    }
}

// A captured stop: world position + the connector that docks there.
private class StopInfo
{
    public string Name;
    public Vector3D Pos;
    public string ConnectorName;

    public StopInfo(string name, Vector3D pos, string connectorName)
    {
        Name = name;
        Pos = pos;
        ConnectorName = connectorName;
    }
}

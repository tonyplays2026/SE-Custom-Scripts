// ============================================================================
// Track Shuttle Controller
// Iteration 1 — Cruise speed controller
//
// Holds a configurable target speed for a track-guided wheeled shuttle, driving
// up grades and braking down grades. Steering is assumed to be handled by the
// physical track, so this script only manages propulsion and wheel brakes on a
// defined set of DRIVE wheels (alignment/guide wheels are excluded).
//
// Left and right drive wheels require OPPOSITE PropulsionOverride signs (an SE
// quirk: the override is applied raw and does not auto-account for mirrored
// placement). The script determines each wheel's side from geometry and signs
// them automatically.
//
// Later iterations add: position-based approach & docking, and automatic
// destination selection / trip sequencing.
//
// USAGE (Programmable Block argument):
//   start   - begin cruising toward CruiseSpeed
//   stop    - halt and hold with brakes
//   reload  - re-read Custom Data config and re-discover blocks
//   (none)  - print status / setup info
//
// Config lives in this block's Custom Data (section [ShuttleController]); a
// template is written automatically the first time if it is empty.
// ============================================================================

// ---- Configuration (parsed from Custom Data) -------------------------------
double _cruiseSpeed = 10.0;               // target travel speed (m/s)
double _maxSpeed = 15.0;                   // emergency-brake threshold, TOTAL speed (m/s)
double _kp = 0.35;                         // proportional gain: override per m/s of error
double _brakeOverspeed = 2.0;              // m/s above cruise before wheel brakes engage
int _propulsionSign = 1;                   // global wiring flip; -1 if it drives the wrong way
bool _reverse = false;                     // travel direction chooser (flips cleanly)
string _driveWheelGroup = "Drive Wheels";  // group holding the drive wheels (excludes guides)

// ---- Discovered blocks -----------------------------------------------------
IMyShipController _controller;
List<DriveWheel> _wheels = new List<DriveWheel>();

// ---- Runtime state ---------------------------------------------------------
bool _running = false;
string _setupError = null;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.None;
    ParseConfig();
    Discover();
}

public void Save() { }

public void Main(string argument, UpdateType updateSource)
{
    // Control ticks arrive via Update1; commands arrive via terminal/trigger.
    if ((updateSource & UpdateType.Update1) != 0)
    {
        ControlTick();
        return;
    }

    string arg = (argument ?? "").Trim().ToLowerInvariant();
    switch (arg)
    {
        case "start":
            StartCruise();
            break;
        case "stop":
            StopCruise();
            break;
        case "reload":
            ParseConfig();
            Discover();
            PrintSetup();
            break;
        default:
            PrintSetup();
            break;
    }
}

// ---- Commands --------------------------------------------------------------

private void StartCruise()
{
    ParseConfig();
    Discover();
    if (_setupError != null)
    {
        Echo("Cannot start.");
        PrintSetup();
        return;
    }
    _running = true;
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
    Echo("Cruising started. Target " + _cruiseSpeed.ToString("0.0") + " m/s.");
}

private void StopCruise()
{
    _running = false;
    Runtime.UpdateFrequency = UpdateFrequency.None;
    for (int i = 0; i < _wheels.Count; i++)
    {
        _wheels[i].Wheel.PropulsionOverride = 0f;
        _wheels[i].Wheel.Brake = true;
    }
    Echo("Stopped. Brakes holding.");
}

// ---- Control loop ----------------------------------------------------------

private void ControlTick()
{
    if (_setupError != null || _controller == null || _wheels.Count == 0)
    {
        // Something we depend on went away (block destroyed, config broke).
        Discover();
        if (_setupError != null)
        {
            StopCruise();
            Echo("Setup error, cruise halted: " + _setupError);
            return;
        }
    }

    int tripDir = _reverse ? -1 : 1;

    Vector3D v = _controller.GetShipVelocities().LinearVelocity;
    double totalSpeed = v.Length();
    double signedSpeed = Vector3D.Dot(v, _controller.WorldMatrix.Forward);
    double travelSpeed = signedSpeed * tripDir;     // speed toward chosen travel direction
    double error = _cruiseSpeed - travelSpeed;
    double overspeed = travelSpeed - _cruiseSpeed;

    double command;   // drive effort in the travel direction, [-1..1]
    bool brake;
    bool emergency = totalSpeed > _maxSpeed;

    if (emergency)
    {
        // Sign-independent safety net: kill thrust and brake hard.
        command = 0.0;
        brake = true;
    }
    else if (overspeed > _brakeOverspeed)
    {
        // Well over target: let the wheel brakes do the work.
        command = 0.0;
        brake = true;
    }
    else
    {
        // Drive when under target; a slight negative command gives gentle
        // engine braking when just barely over.
        command = Clamp(_kp * error, -1.0, 1.0);
        brake = false;
    }

    // Convert the travel-direction command into a per-wheel override:
    //   tripDir        - travel direction chooser
    //   _propulsionSign - global wiring flip
    //   SideSign        - left/right mirror (opposite signs per side)
    double baseOverride = command * tripDir * _propulsionSign;
    for (int i = 0; i < _wheels.Count; i++)
    {
        DriveWheel dw = _wheels[i];
        dw.Wheel.Propulsion = true;
        dw.Wheel.PropulsionOverride = (float)(baseOverride * dw.SideSign);
        dw.Wheel.Brake = brake;
    }

    PrintRunning(travelSpeed, totalSpeed, command, brake, emergency);
}

// ---- Configuration ---------------------------------------------------------

private void ParseConfig()
{
    if (string.IsNullOrWhiteSpace(Me.CustomData))
    {
        WriteConfigTemplate();
    }

    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result))
    {
        Echo("CONFIG WARNING: could not parse Custom Data (" + result.Error + "). Using defaults.");
        return;
    }

    const string S = "ShuttleController";
    _cruiseSpeed = ini.Get(S, "CruiseSpeed").ToDouble(_cruiseSpeed);
    _maxSpeed = ini.Get(S, "MaxSpeed").ToDouble(_maxSpeed);
    _kp = ini.Get(S, "Kp").ToDouble(_kp);
    _brakeOverspeed = ini.Get(S, "BrakeOverspeed").ToDouble(_brakeOverspeed);
    _propulsionSign = ini.Get(S, "PropulsionSign").ToInt32(_propulsionSign) < 0 ? -1 : 1;
    _reverse = ini.Get(S, "Reverse").ToBoolean(_reverse);
    _driveWheelGroup = ini.Get(S, "DriveWheelGroup").ToString(_driveWheelGroup);

    // Guard against nonsensical values that would defeat the safety net.
    if (_maxSpeed <= _cruiseSpeed) _maxSpeed = _cruiseSpeed + 5.0;
    if (_kp <= 0.0) _kp = 0.35;
}

private void WriteConfigTemplate()
{
    MyIni ini = new MyIni();
    const string S = "ShuttleController";
    ini.Set(S, "CruiseSpeed", _cruiseSpeed);
    ini.Set(S, "MaxSpeed", _maxSpeed);
    ini.Set(S, "Kp", _kp);
    ini.Set(S, "BrakeOverspeed", _brakeOverspeed);
    ini.Set(S, "PropulsionSign", _propulsionSign);
    ini.Set(S, "Reverse", _reverse);
    ini.Set(S, "DriveWheelGroup", _driveWheelGroup);
    ini.SetComment(S, "CruiseSpeed", "Target travel speed (m/s).");
    ini.SetComment(S, "MaxSpeed", "Emergency-brake threshold on total speed (m/s).");
    ini.SetComment(S, "Kp", "Proportional gain: propulsion override per m/s of error.");
    ini.SetComment(S, "BrakeOverspeed", "m/s above cruise before wheel brakes engage.");
    ini.SetComment(S, "PropulsionSign", "Global wiring flip. Set -1 if it emergency-brakes / drives the wrong way.");
    ini.SetComment(S, "Reverse", "Flip travel direction along the track (true/false).");
    ini.SetComment(S, "DriveWheelGroup", "Block group holding ONLY the drive wheels (exclude alignment wheels).");
    Me.CustomData = ini.ToString();
}

// ---- Block discovery -------------------------------------------------------

private void Discover()
{
    _setupError = null;
    _controller = null;
    _wheels.Clear();

    // Controller: prefer a Remote Control, else any ship controller on this
    // construct. (Connector-docked grids are separate constructs, so other
    // carts / the base are never included.)
    List<IMyShipController> controllers = new List<IMyShipController>();
    GridTerminalSystem.GetBlocksOfType(controllers);
    for (int i = 0; i < controllers.Count; i++)
    {
        if (controllers[i] is IMyRemoteControl)
        {
            _controller = controllers[i];
            break;
        }
    }
    if (_controller == null && controllers.Count > 0) _controller = controllers[0];
    if (_controller == null)
    {
        _setupError = "No ship controller (remote/cockpit) found.";
        return;
    }

    // Drive wheels: from the configured group only.
    if (string.IsNullOrWhiteSpace(_driveWheelGroup))
    {
        _setupError = "DriveWheelGroup is not set in Custom Data.";
        return;
    }
    IMyBlockGroup grp = GridTerminalSystem.GetBlockGroupWithName(_driveWheelGroup);
    if (grp == null)
    {
        _setupError = "Drive wheel group '" + _driveWheelGroup + "' not found.";
        return;
    }
    List<IMyMotorSuspension> raw = new List<IMyMotorSuspension>();
    grp.GetBlocksOfType(raw);
    if (raw.Count == 0)
    {
        _setupError = "Group '" + _driveWheelGroup + "' contains no wheel suspensions.";
        return;
    }

    // Assign each wheel a side sign from its position relative to the
    // controller's Right axis: left = +1, right = -1 (opposite override signs).
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

private void PrintRunning(double travelSpeed, double totalSpeed, double command, bool brake, bool emergency)
{
    Echo("== Track Shuttle — CRUISING ==");
    Echo("Target : " + _cruiseSpeed.ToString("0.0") + " m/s" + (_reverse ? "  (reverse)" : ""));
    Echo("Travel : " + travelSpeed.ToString("0.00") + " m/s");
    Echo("Total  : " + totalSpeed.ToString("0.00") + " m/s");
    Echo("Command: " + command.ToString("0.00") + (brake ? "   [BRAKES]" : ""));
    if (emergency)
        Echo("!! EMERGENCY BRAKE — over MaxSpeed. If it drove the wrong way, set PropulsionSign = -1.");
    Echo("Drive wheels: " + _wheels.Count);
    Echo("Send 'stop' to halt.");
}

private void PrintSetup()
{
    Echo("== Track Shuttle Controller (Iteration 1) ==");
    Echo("State: " + (_running ? "CRUISING" : "IDLE"));
    Echo("");
    Echo("Config:");
    Echo("  CruiseSpeed     = " + _cruiseSpeed.ToString("0.0") + " m/s");
    Echo("  MaxSpeed        = " + _maxSpeed.ToString("0.0") + " m/s");
    Echo("  Kp              = " + _kp.ToString("0.00"));
    Echo("  BrakeOverspeed  = " + _brakeOverspeed.ToString("0.0") + " m/s");
    Echo("  PropulsionSign  = " + _propulsionSign);
    Echo("  Reverse         = " + _reverse);
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
        {
            if (_wheels[i].SideSign > 0) left++; else rightCount++;
        }
        Echo("Controller  : " + _controller.CustomName);
        Echo("Drive wheels: " + _wheels.Count + "  (left " + left + " / right " + rightCount + ")");
    }
    Echo("");
    Echo("Commands: start | stop | reload");
}

// ---- Helpers ---------------------------------------------------------------

private static double Clamp(double x, double lo, double hi)
{
    if (x < lo) return lo;
    if (x > hi) return hi;
    return x;
}

// Pairs a drive wheel with its side sign (left = +1, right = -1).
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

// Robotic Arm Controller v2 — drives hinges, rotors, pistons and on/off blocks
// through named positions and sequences. Setup, commands, config: see Workshop.txt.

// ===========================================================================
//  BLOCK TYPES
//  To support a block this script doesn't know about: subclass ArmBlock<T>,
//  implement the three methods, and add one line to RegisterBlockTypes().
//  Nothing outside this section needs to change.
//
//  Any block with an on/off switch already works — add it to the registry as
//  ToggleBlock<IMyWhatever>; no new class needed.
// ===========================================================================

private struct MotionTarget
{
    public float Position;
    public float Velocity;
    public MyRotationDirection Direction;
}

private abstract class ArmBlock
{
    protected Program P;
    private IMyTerminalBlock _resolved;

    public string Name { get; private set; }
    public string DeclaredType { get; private set; }

    public void Init(Program program, string name, string declaredType)
    {
        P = program;
        Name = name;
        DeclaredType = declaredType;
    }

    // Returns null on success, or the reason the config line was rejected.
    public abstract string LoadConfig(string location, string[] fields);
    public abstract void MoveTo(string location);
    public abstract bool IsAtTarget(string location);

    // Only successful lookups are cached, so a block welded on later is still found.
    protected T Find<T>() where T : class
    {
        if (_resolved == null) _resolved = P.GridTerminalSystem.GetBlockWithName(Name);
        return _resolved as T;
    }

    protected void WarnMissing()
    {
        P.PrintMessage($"WARNING: Could not find the {DeclaredType} labeled {Name}.");
    }
}

private abstract class ArmBlock<TTarget> : ArmBlock
{
    protected readonly Dictionary<string, TTarget> Targets = new Dictionary<string, TTarget>();

    protected string AddTarget(string location, TTarget target)
    {
        if (Targets.ContainsKey(location)) return $"{Name} is defined more than once in [{location}]";
        Targets[location] = target;
        return null;
    }
}

private class PistonBlock : ArmBlock<MotionTarget>
{
    public const float DEFAULT_TOLERANCE = 0.05f; // metres
    public static float Tolerance = DEFAULT_TOLERANCE;

    public override string LoadConfig(string location, string[] fields)
    {
        if (fields.Length < 4) return $"missing target/velocity for {DeclaredType}";
        MotionTarget t = new MotionTarget();
        t.Position = GetFloatValue(fields[2]);
        t.Velocity = GetFloatValue(fields[3]);
        return AddTarget(location, t);
    }

    public override void MoveTo(string location)
    {
        MotionTarget t;
        if (!Targets.TryGetValue(location, out t)) return;

        var piston = Find<IMyPistonBase>();
        if (piston == null) { WarnMissing(); return; }
        piston.MoveToPosition(t.Position, t.Velocity);
    }

    public override bool IsAtTarget(string location)
    {
        MotionTarget t;
        if (!Targets.TryGetValue(location, out t)) return true;

        var piston = Find<IMyPistonBase>();
        if (piston == null) return false;

        float difference = Math.Abs(piston.CurrentPosition - t.Position);
        if (difference > Tolerance)
        {
            P.PrintMessage($"Piston {Name}: {piston.CurrentPosition:F3}m vs {t.Position:F3}m (diff {difference:F3})", true);
            return false;
        }
        return true;
    }
}

private class StatorBlock : ArmBlock<MotionTarget>
{
    public const float DEFAULT_TOLERANCE = 0.5f; // degrees
    public static float Tolerance = DEFAULT_TOLERANCE;

    public override string LoadConfig(string location, string[] fields)
    {
        if (fields.Length < 4) return $"missing target/velocity for {DeclaredType}";
        MotionTarget t = new MotionTarget();
        t.Position = GetFloatValue(fields[2]);
        t.Velocity = GetFloatValue(fields[3]);
        t.Direction = GetRotationDirection(fields);
        return AddTarget(location, t);
    }

    public override void MoveTo(string location)
    {
        MotionTarget t;
        if (!Targets.TryGetValue(location, out t)) return;

        var stator = Find<IMyMotorStator>();
        if (stator == null) { WarnMissing(); return; }
        stator.RotateToAngle(t.Direction, t.Position, t.Velocity);
    }

    public override bool IsAtTarget(string location)
    {
        MotionTarget t;
        if (!Targets.TryGetValue(location, out t)) return true;

        var stator = Find<IMyMotorStator>();
        if (stator == null) return false;

        float currentDegrees = MathHelper.ToDegrees(stator.Angle);
        float difference = Math.Abs(currentDegrees - t.Position);

        // Angle is 0-360 but config targets are signed, so fold the long way round.
        if (difference > 180f) difference = 360f - difference;

        if (difference > Tolerance)
        {
            P.PrintMessage($"{DeclaredType} {Name}: {currentDegrees:F1}° vs {t.Position:F1}° (diff {difference:F1}°)", true);
            return false;
        }
        return true;
    }
}

private class ToggleBlock<T> : ArmBlock<bool> where T : class, IMyFunctionalBlock
{
    public override string LoadConfig(string location, string[] fields)
    {
        if (fields.Length < 3) return $"missing enable field for {DeclaredType}";
        return AddTarget(location, GetBoolValue(fields[2]));
    }

    public override void MoveTo(string location)
    {
        bool shouldEnable;
        if (!Targets.TryGetValue(location, out shouldEnable)) return;

        var block = Find<T>();
        if (block == null) { WarnMissing(); return; }
        block.Enabled = shouldEnable;
    }

    public override bool IsAtTarget(string location)
    {
        bool shouldEnable;
        if (!Targets.TryGetValue(location, out shouldEnable)) return true;

        var block = Find<T>();
        if (block == null) return true;

        if (block.Enabled != shouldEnable)
        {
            P.PrintMessage($"{DeclaredType} {Name}: {block.Enabled} vs {shouldEnable}", true);
            return false;
        }
        return true;
    }
}

// The hinge base a detachable tool hangs from. Attach/Detach only - declare the
// block as a Hinge instead if you want to drive its angle.
private class ToolHingeBlock : ArmBlock<bool>
{
    public override string LoadConfig(string location, string[] fields)
    {
        if (fields.Length < 3) return $"missing Attach/Detach for {DeclaredType}";
        if (String.Compare(fields[2], "Attach", StringComparison.InvariantCultureIgnoreCase) == 0)
            return AddTarget(location, true);
        if (String.Compare(fields[2], "Detach", StringComparison.InvariantCultureIgnoreCase) == 0)
            return AddTarget(location, false);
        return $"{DeclaredType} expects Attach or Detach, got {fields[2]}";
    }

    public override void MoveTo(string location)
    {
        bool shouldAttach;
        if (!Targets.TryGetValue(location, out shouldAttach)) return;

        var stator = Find<IMyMotorStator>();
        if (stator == null) { WarnMissing(); return; }

        if (shouldAttach) stator.Attach();
        else stator.Detach();
    }

    public override bool IsAtTarget(string location)
    {
        bool shouldAttach;
        if (!Targets.TryGetValue(location, out shouldAttach)) return true;

        var stator = Find<IMyMotorStator>();
        if (stator == null) return false;
        if (stator.IsAttached == shouldAttach) return true;

        if (shouldAttach) ReissueAttach(stator);

        P.PrintMessage($"{DeclaredType} {Name}: attached={stator.IsAttached} pending={stator.PendingAttachment}, want {shouldAttach}", true);
        return false;
    }

    // Attach needs time and repeated attempts before it takes, so the single call
    // from MoveTo is not enough; detach succeeds first try. Deliberately a write
    // from inside the check - it is the only place that runs on every tick.
    private void ReissueAttach(IMyMotorStator stator)
    {
        stator.Attach();
    }
}

private class LandingGearBlock : ArmBlock<bool>
{
    public override string LoadConfig(string location, string[] fields)
    {
        if (fields.Length < 3) return $"missing Lock/Unlock for {DeclaredType}";
        if (String.Compare(fields[2], "Lock", StringComparison.InvariantCultureIgnoreCase) == 0)
            return AddTarget(location, true);
        if (String.Compare(fields[2], "Unlock", StringComparison.InvariantCultureIgnoreCase) == 0)
            return AddTarget(location, false);
        return $"{DeclaredType} expects Lock or Unlock, got {fields[2]}";
    }

    public override void MoveTo(string location)
    {
        bool shouldLock;
        if (!Targets.TryGetValue(location, out shouldLock)) return;

        var gear = Find<IMyLandingGear>();
        if (gear == null) { WarnMissing(); return; }

        if (shouldLock) gear.Lock();
        else gear.Unlock();
    }

    public override bool IsAtTarget(string location)
    {
        bool shouldLock;
        if (!Targets.TryGetValue(location, out shouldLock)) return true;

        var gear = Find<IMyLandingGear>();
        if (gear == null) return false;
        if (gear.IsLocked == shouldLock) return true;

        // Same timing problem as ToolHinge attach: locking needs the target in
        // range and settled. LockMode distinguishes "out of range" from "not yet".
        if (shouldLock) gear.Lock();

        P.PrintMessage($"{DeclaredType} {Name}: {gear.LockMode}, want locked={shouldLock}", true);
        return false;
    }
}

private void RegisterBlockTypes()
{
    _blockTypes = new Dictionary<string, Func<ArmBlock>>(StringComparer.OrdinalIgnoreCase)
    {
        { "Piston",      () => new PistonBlock() },
        { "Rotor",       () => new StatorBlock() },
        { "Hinge",       () => new StatorBlock() },
        { "ToolHinge",   () => new ToolHingeBlock() },
        { "LandingGear", () => new LandingGearBlock() },
        { "Welder",      () => new ToggleBlock<IMyShipWelder>() },
        { "Projector",   () => new ToggleBlock<IMyProjector>() },
        { "Light",       () => new ToggleBlock<IMyReflectorLight>() },
    };
}

// ===========================================================================
//  PROGRAM
// ===========================================================================

private const double MOVE_TIMEOUT_SECONDS = 60.0;

private bool DEBUG_MODE = false;
private Dictionary<string, Func<ArmBlock>> _blockTypes;
private Dictionary<string, ArmBlock> _blocks;
private List<string> _locations;
private Dictionary<string, List<string>> _sequences;
private SequenceState _state;
private double _elapsed = 0.0;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Once;
    RegisterBlockTypes();
    // CustomData changes require a recompile to take effect
    LoadConfig(Me.CustomData);
    LoadState();
}

public void Save() { }

public void Main(string argument, UpdateType updateSource)
{
    _elapsed += Runtime.TimeSinceLastRun.TotalSeconds;

    try
    {
        string arg = (argument ?? "").Trim();

        if (string.IsNullOrWhiteSpace(arg))
        {
            PrintMessage($"Config loaded OK ({_locations.Count} positions, {_blocks.Count} blocks, {_sequences.Count} sequences).");
        }
        else if (string.Compare(arg, "stop", StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            StopSequence();
            PrintMessage("Current sequence stopped.");
        }
        else if (arg.StartsWith("sequence "))
        {
            StartSequence(arg.Substring(9).Trim());
        }
        else if (_locations.Contains(arg))
        {
            MoveTo(arg);
        }
        else
        {
            PrintMessage("Unknown location: " + arg);
            PrintMessage("Available: " + string.Join(", ", _locations));
        }

        if ((updateSource & UpdateType.Update100) != 0)
        {
            ProcessSequence();
        }
    }
    catch (Exception ex)
    {
        PrintMessage($"ERROR: {ex.Message}");
    }
}

private void LoadConfig(string data)
{
    _blocks = new Dictionary<string, ArmBlock>();
    _locations = new List<string>();
    _sequences = new Dictionary<string, List<string>>();

    // Reset everything a [settings] line can set, so deleting that line reverts
    // it. The tolerances are static and would otherwise outlive the config.
    DEBUG_MODE = false;
    PistonBlock.Tolerance = PistonBlock.DEFAULT_TOLERANCE;
    StatorBlock.Tolerance = StatorBlock.DEFAULT_TOLERANCE;

    string currentSection = null;

    foreach (var line in data.Replace("\r", "").Split(new char[] {'\n'}, StringSplitOptions.RemoveEmptyEntries))
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith("#"))
            continue;

        if (trimmed.StartsWith("["))
        {
            if (trimmed.EndsWith("]"))
            {
                currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (String.Compare(currentSection, "sequences", StringComparison.InvariantCultureIgnoreCase) != 0 &&
                    String.Compare(currentSection, "settings", StringComparison.InvariantCultureIgnoreCase) != 0)
                {
                    if (!_locations.Contains(currentSection)) _locations.Add(currentSection);
                }

                continue;
            }
            else
            {
                Echo($"CONFIG ERROR: Malformed section header: {trimmed}");
                continue;
            }
        }

        if (currentSection == null) { Echo($"CONFIG ERROR: Entry has no section header: {trimmed}"); continue; }

        if (String.Compare(currentSection, "settings", StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            LoadSetting(trimmed);
        }
        else if (String.Compare(currentSection, "sequences", StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            var sequenceDefinition = trimmed.Split(new char[] {'='}, StringSplitOptions.RemoveEmptyEntries);

            if (sequenceDefinition.Length != 2) { Echo($"CONFIG ERROR: Invalid sequence definition: {trimmed}"); continue; }

            if (!_sequences.ContainsKey(sequenceDefinition[0]))
            {
                _sequences[sequenceDefinition[0]] = sequenceDefinition[1].Split(new char[] {':'}, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else
            {
                PrintMessage($"WARNING: Duplicate sequence definitions for {sequenceDefinition[0]} - duplicates are ignored.");
            }
        }
        else
        {
            LoadBlockDefinition(currentSection, trimmed);
        }
    }
}

private void LoadSetting(string trimmed)
{
    var setting = trimmed.Split(new char[] {'='}, StringSplitOptions.RemoveEmptyEntries);
    if (setting.Length < 2) return;

    string key = setting[0].Trim();
    string value = setting[1].Trim();

    if (String.Compare(key, "debug", StringComparison.InvariantCultureIgnoreCase) == 0)
        DEBUG_MODE = GetBoolValue(value);
    else if (String.Compare(key, "pistonTolerance", StringComparison.InvariantCultureIgnoreCase) == 0)
        PistonBlock.Tolerance = GetFloatValue(value);
    else if (String.Compare(key, "angleTolerance", StringComparison.InvariantCultureIgnoreCase) == 0)
        StatorBlock.Tolerance = GetFloatValue(value);
}

private void LoadBlockDefinition(string location, string trimmed)
{
    var fields = trimmed.Split(new char[] {':'}, StringSplitOptions.RemoveEmptyEntries);

    if (fields.Length < 2) { Echo($"CONFIG ERROR: Incomplete block definition: {trimmed}"); return; }

    string name = fields[0];
    string typeName = fields[1];

    ArmBlock block;
    if (!_blocks.TryGetValue(name, out block))
    {
        Func<ArmBlock> factory;
        if (!_blockTypes.TryGetValue(typeName, out factory))
        {
            Echo($"CONFIG ERROR: {name} has an unknown type of {typeName}.");
            return;
        }

        block = factory();
        block.Init(this, name, typeName);
        _blocks[name] = block;
    }
    else if (String.Compare(block.DeclaredType, typeName, StringComparison.InvariantCultureIgnoreCase) != 0)
    {
        Echo($"CONFIG ERROR: {name} is declared as both {block.DeclaredType} and {typeName}.");
        return;
    }

    string error = block.LoadConfig(location, fields);
    if (error != null) Echo($"CONFIG ERROR: {error}: {trimmed}");
}

private void StartSequence(string name)
{
    if (!_sequences.ContainsKey(name)) throw new InvalidOperationException($"No sequence labeled {name} exists.");

    _state = new SequenceState
    {
        SequenceName = name,
        StepIndex = 0
    };
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
    SaveState();

    MoveTo(_sequences[name][0]);
}

private void StopSequence()
{
    _state = new SequenceState
    {
        SequenceName = "-",
        StepIndex = -1
    };
    Runtime.UpdateFrequency = UpdateFrequency.None;
    SaveState();
}

private void SaveState()
{
    Storage = $"{_state.SequenceName}|{_state.StepIndex}";
}

private void LoadState()
{
    if (string.IsNullOrEmpty(Storage))
    {
        _state = new SequenceState
        {
            SequenceName = "-",
            StepIndex = -1,
        };
    }
    else
    {
        var values = Storage.Split(new char[] {'|'}, StringSplitOptions.RemoveEmptyEntries);

        if (values.Length == 2)
        {
            _state = new SequenceState
            {
                SequenceName = values[0],
                StepIndex = int.Parse(values[1])
            };

            Runtime.UpdateFrequency = UpdateFrequency.Update100;

            if (_state.SequenceName != "-" && _state.StepIndex >= 0 &&
                _sequences.ContainsKey(_state.SequenceName) &&
                _state.StepIndex < _sequences[_state.SequenceName].Count)
            {
                MoveTo(_sequences[_state.SequenceName][_state.StepIndex]);
            }
        }
    }
}

private void ProcessSequence()
{
    if (string.IsNullOrWhiteSpace(_state.SequenceName))
    {
        StopSequence();
        PrintMessage("WARNING: State was invalid while trying to process the step.");
        return;
    }

    if (!_sequences.ContainsKey(_state.SequenceName))
    {
        StopSequence();
        PrintMessage("WARNING: State was invalid while trying to process the step.");
        return;
    }

    var sequence = _sequences[_state.SequenceName];
    if (_state.StepIndex >= sequence.Count)
    {
        PrintMessage($"Sequence {_state.SequenceName} completed.", true);
        StopSequence();
        return;
    }

    var position = sequence[_state.StepIndex];

    if (IsPositionReached(position))
    {
        PrintMessage($"Step {_state.StepIndex + 1}/{sequence.Count} ({position}) reached.", true);
        _state.StepIndex++;
        if (_state.StepIndex < sequence.Count) MoveTo(sequence[_state.StepIndex]);
        SaveState();
    }
    else if (_elapsed > MOVE_TIMEOUT_SECONDS)
    {
        PrintMessage($"WARNING: Timeout while attempting to move to {position}. Stopping sequence.");
        StopSequence();
    }
}

private void MoveTo(string location)
{
    PrintMessage($"Moving to location {location}.", true);
    if (!_locations.Contains(location)) throw new InvalidOperationException($"The location {location} specified does not exist.");

    foreach (var block in _blocks.Values) block.MoveTo(location);

    _elapsed = 0.0;
}

private bool IsPositionReached(string location)
{
    PrintMessage($"Checking if location {location} has been reached.", true);
    if (!_locations.Contains(location)) return true;

    foreach (var block in _blocks.Values)
    {
        if (!block.IsAtTarget(location)) return false;
    }
    return true;
}

private static float GetFloatValue(string input)
{
    float value;
    float.TryParse(input, out value);
    return value;
}

private static MyRotationDirection GetRotationDirection(string[] blockDefinition)
{
    MyRotationDirection direction;
    if (blockDefinition.Length == 5 && Enum.TryParse(blockDefinition[4], out direction)) return direction;
    return MyRotationDirection.AUTO;
}

private static bool GetBoolValue(string input)
{
    bool value;
    bool.TryParse(input, out value);
    return value;
}

private void PrintMessage(string message, bool isDebugMessage = false)
{
    if (isDebugMessage)
    {
        if (DEBUG_MODE) Echo(message);
    }
    else
    {
        Echo(message);
    }
}

private struct SequenceState
{
    public string SequenceName { get; set; }
    public int StepIndex { get; set; }
}

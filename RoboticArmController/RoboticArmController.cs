private const float TOLERANCE = 0.05f;
private const double MOVE_TIMEOUT_SECONDS = 60.0;

private bool DEBUG_MODE = false;
private Dictionary<string, List<BlockConfig>> _positions;
private Dictionary<string, List<string>> _sequences;
private SequenceState _state;
private double _elapsed = 0.0;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Once;
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
            PrintMessage($"Config loaded OK ({_positions.Count} positions, {_sequences.Count} sequences).");
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
        else if (_positions.ContainsKey(arg))
        {
            MoveTo(arg);
        }
        else
        {
            PrintMessage("Unknown location: " + arg);
            PrintMessage("Available: " + string.Join(", ", _positions.Keys));
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
    _positions = new Dictionary<string, List<BlockConfig>>();
    _sequences = new Dictionary<string, List<string>>();

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
                if (String.Compare(currentSection, "sequences", StringComparison.InvariantCultureIgnoreCase) == 0 ||
                    String.Compare(currentSection, "settings", StringComparison.InvariantCultureIgnoreCase) == 0)
                {
                    // Special sections; no position list needed
                }
                else
                {
                    if (!_positions.ContainsKey(currentSection)) _positions[currentSection] = new List<BlockConfig>();
                }

                continue;
            }
            else
            {
                throw new InvalidOperationException("Section header in CustomData is not defined properly.");
            }
        }

        if (currentSection == null) throw new InvalidOperationException("Configuration in CustomData is not defined properly. Missing section header.");

        if (String.Compare(currentSection, "settings", StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            var setting = trimmed.Split(new char[] {'='}, StringSplitOptions.RemoveEmptyEntries);
            if (setting.Length >= 2 && String.Compare(setting[0].Trim(), "debug", StringComparison.InvariantCultureIgnoreCase) == 0)
                DEBUG_MODE = GetBoolValue(setting[1].Trim());
        }
        else if (String.Compare(currentSection, "sequences", StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            var sequenceDefinition = trimmed.Split(new char[] {'='}, StringSplitOptions.RemoveEmptyEntries);

            if (sequenceDefinition.Length != 2) throw new InvalidOperationException("Sequence Defitions are invalid.");

            if (!_sequences.ContainsKey(sequenceDefinition[0]))
            {
                _sequences[sequenceDefinition[0]] = sequenceDefinition[1].Split(new char[] {':'}, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else
            {
                PrintMessage($"WARNING: Duplicate sequence definitions for {sequenceDefinition[0]} - duplicates are ingored.");
            }
        }
        else
        {
            var blockDefinition = trimmed.Split(new char[] {':'}, StringSplitOptions.RemoveEmptyEntries);

            if (blockDefinition.Length < 2)
            {
                throw new InvalidOperationException("Configuration in CustomData is not defined properly.  Missing info for block definition.");
            }
            else
            {
                switch (blockDefinition[1])
                {
                    case "Welder":
                    case "Projector":
                    case "Light":
                        _positions[currentSection].Add(new BlockConfig
                        {
                            Name = blockDefinition[0],
                            Type = blockDefinition[1],
                            ShouldEnable = GetBoolValue(blockDefinition[2])
                        });
                        break;
                    default:
                        _positions[currentSection].Add(new BlockConfig
                        {
                            Name = blockDefinition[0],
                            Type = blockDefinition[1],
                            Target = GetFloatValue(blockDefinition[2]),
                            Velocity = GetFloatValue(blockDefinition[3]),
                            RotationDirection = GetRotationDirection(blockDefinition)
                        });
                        break;
                }
            }
        }
    }
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
    if (!_positions.ContainsKey(location)) throw new InvalidOperationException($"The location {location} specified does not exist.");

    var blockConfig = _positions[location];

    foreach (var block in blockConfig)
    {
        switch (block.Type)
        {
            case "Piston":
                var piston = GridTerminalSystem.GetBlockWithName(block.Name) as IMyPistonBase;
                if (piston != null)
                {
                    piston.MoveToPosition(block.Target, block.Velocity);
                }
                else
                {
                    PrintMessage($"WARNING: Could not find the piston labeled {block.Name}.");
                }
                break;
            case "Hinge":
            case "Rotor":
                var stator = GridTerminalSystem.GetBlockWithName(block.Name) as IMyMotorStator;
                if (stator != null)
                {
                    stator.RotateToAngle(block.RotationDirection, block.Target, block.Velocity);
                }
                else
                {
                    PrintMessage($"WARNING: Could not find the {block.Type} labeled {block.Name}.");
                }
                break;
            case "Welder":
                var welder = GridTerminalSystem.GetBlockWithName(block.Name) as IMyShipWelder;
                if (welder != null)
                {
                    welder.Enabled = block.ShouldEnable;
                }
                else
                {
                    PrintMessage($"WARNING: Could not find the {block.Type} labeled {block.Name}.");
                }
                break;
            case "Projector":
                var projector = GridTerminalSystem.GetBlockWithName(block.Name) as IMyProjector;
                if (projector != null)
                {
                    projector.Enabled = block.ShouldEnable;
                }
                else
                {
                    PrintMessage($"WARNING: Could not find the {block.Type} labeled {block.Name}.");
                }
                break;
            case "Light":
                var light = GridTerminalSystem.GetBlockWithName(block.Name) as IMyReflectorLight;
                if (light != null)
                {
                    light.Enabled = block.ShouldEnable;
                }
                else
                {
                    PrintMessage($"WARNING: Could not find the {block.Type} labeled {block.Name}.");
                }
                break;
            default:
                PrintMessage($"WARNING: {block.Name} has an unknown type of {block.Type}.");
                break;
        }
    }

    _elapsed = 0.0;
}

private bool IsPositionReached(string location)
{
    PrintMessage($"Checking if location {location} has been reached.", true);
    if (!_positions.ContainsKey(location)) return true;

    foreach (var block in _positions[location])
    {
        switch (block.Type)
        {
            case "Piston":
                var piston = GridTerminalSystem.GetBlockWithName(block.Name) as IMyPistonBase;
                if (piston == null) return false;
                var pistonDifference = Math.Abs(piston.CurrentPosition - block.Target);
                if (pistonDifference > TOLERANCE)
                {
                    PrintMessage($"Piston {block.Name}: {piston.CurrentPosition:F3}m vs {block.Target:F3}m (diff {pistonDifference:F3})", true);
                    return false;
                }
                break;
            case "Hinge":
            case "Rotor":
                var stator = GridTerminalSystem.GetBlockWithName(block.Name) as IMyMotorStator;
                if (stator == null) return false;

                float currentDegrees = MathHelper.ToDegrees(stator.Angle);
                float difference = Math.Abs(currentDegrees - block.Target);

                if (difference > 180f) difference = 360f - difference;

                if (difference > TOLERANCE)
                {
                    PrintMessage($"{block.Type} {block.Name}: {currentDegrees:F1}° vs {block.Target:F1}° (diff {difference:F1}°)", true);
                    return false;
                }
                break;
            case "Welder":
                var welder = GridTerminalSystem.GetBlockWithName(block.Name) as IMyShipWelder;
                if (welder != null && block.ShouldEnable != welder.Enabled)
                {
                    PrintMessage($"{block.Type} {block.Name}: {welder.Enabled} vs {block.ShouldEnable}", true);
                    return false;
                }
                break;
            case "Projector":
                var projector = GridTerminalSystem.GetBlockWithName(block.Name) as IMyProjector;
                if (projector != null && block.ShouldEnable != projector.Enabled)
                {
                    PrintMessage($"{block.Type} {block.Name}: {projector.Enabled} vs {block.ShouldEnable}", true);
                    return false;
                }
                break;
            case "Light":
                var light = GridTerminalSystem.GetBlockWithName(block.Name) as IMyReflectorLight;
                if (light != null && block.ShouldEnable != light.Enabled)
                {
                    PrintMessage($"{block.Type} {block.Name}: {light.Enabled} vs {block.ShouldEnable}", true);
                    return false;
                }
                break;
        }
    }
    return true;
}

private float GetFloatValue(string input)
{
    float value;
    float.TryParse(input, out value);
    return value;
}

private MyRotationDirection GetRotationDirection(string[] blockDefinition)
{
    MyRotationDirection direction;
    if (blockDefinition.Length == 5 && Enum.TryParse(blockDefinition[4], out direction)) return direction;
    return MyRotationDirection.AUTO;
}

private bool GetBoolValue(string input)
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

private struct BlockConfig
{
    public string Name { get; set; }
    public string Type { get; set; }
    public float Target { get; set; }
    public float Velocity { get; set; }
    public MyRotationDirection RotationDirection { get; set; }
    public bool ShouldConnect { get; set; }
    public bool ShouldEnable { get; set; }
}

private struct SequenceState
{
    public string SequenceName { get; set; }
    public int StepIndex { get; set; }
}

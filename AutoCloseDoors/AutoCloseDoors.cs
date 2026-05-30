// === Configuration ===
string GROUP_NAME = "Auto Close Doors";
double CLOSE_DELAY = 3.0;          // seconds before an open door is closed
double DOOR_CACHE_REFRESH = 30.0;  // seconds between door list refreshes

// === State ===
struct DoorEntry
{
    public IMyDoor Door;
    public double TimeOpened;
    public DoorEntry(IMyDoor door, double timeOpened)
    {
        Door = door;
        TimeOpened = timeOpened;
    }
}

Queue<DoorEntry> _queue = new Queue<DoorEntry>();
HashSet<long> _tracked = new HashSet<long>();
List<IMyDoor> _doors = new List<IMyDoor>();
double _elapsed = 0.0;
double _lastRefresh = -999.0; // force refresh on first run
bool _groupFound = false;
IMyTextSurface _display;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
    _display = Me.GetSurface(0);
    _display.ContentType = ContentType.TEXT_AND_IMAGE;
}

public void Save() { }

public void Main(string argument, UpdateType updateSource)
{
    _elapsed += Runtime.TimeSinceLastRun.TotalSeconds;

    // Close any doors that have been open long enough
    while (_queue.Count > 0)
    {
        DoorEntry entry = _queue.Peek();
        if (_elapsed - entry.TimeOpened < CLOSE_DELAY) break;
        _queue.Dequeue();
        _tracked.Remove(entry.Door.EntityId);
        if (entry.Door.IsFunctional &&
            (entry.Door.Status == DoorStatus.Open || entry.Door.Status == DoorStatus.Opening))
            entry.Door.CloseDoor();
    }

    if (_queue.Count == 0) _elapsed = 0.0;

    // Refresh cached door list when interval elapses; retry every tick while group is missing
    if (_elapsed - _lastRefresh >= DOOR_CACHE_REFRESH)
    {
        _doors.Clear();
        IMyBlockGroup group = GridTerminalSystem.GetBlockGroupWithName(GROUP_NAME);
        _groupFound = group != null;
        if (_groupFound)
        {
            group.GetBlocksOfType<IMyDoor>(_doors);
            _lastRefresh = _elapsed;
        }
    }

    // Enqueue any newly opened functional doors
    if (_groupFound)
    {
        foreach (IMyDoor door in _doors)
        {
            if (!door.IsFunctional) continue;
            if (_tracked.Contains(door.EntityId)) continue;
            if (door.Status == DoorStatus.Open || door.Status == DoorStatus.Opening)
            {
                _tracked.Add(door.EntityId);
                _queue.Enqueue(new DoorEntry(door, _elapsed));
            }
        }
    }

    // Update display
    StringBuilder sb = new StringBuilder();
    sb.AppendLine("=== Auto Close Doors ===");
    if (!_groupFound)
        sb.AppendLine("[ERROR] Group not found: " + GROUP_NAME);
    if (_queue.Count == 0)
        sb.AppendLine("All doors closed.");
    else
        foreach (DoorEntry entry in _queue)
            sb.AppendLine(entry.Door.CustomName);

    string output = sb.ToString();
    Echo(output);
    _display.WriteText(output);
}

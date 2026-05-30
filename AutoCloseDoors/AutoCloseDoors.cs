// === Configuration ===
string GROUP_NAME = "Auto Close Doors";
double CLOSE_DELAY = 3.0; // seconds before an open door is closed

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
double _elapsed = 0.0;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
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
        if (entry.Door.Status == DoorStatus.Open || entry.Door.Status == DoorStatus.Opening)
            entry.Door.CloseDoor();
    }

    // Enqueue any newly opened doors
    IMyBlockGroup group = GridTerminalSystem.GetBlockGroupWithName(GROUP_NAME);
    if (group == null)
    {
        Echo("Group not found: " + GROUP_NAME);
        return;
    }

    List<IMyDoor> doors = new List<IMyDoor>();
    group.GetBlocksOfType<IMyDoor>(doors);

    foreach (IMyDoor door in doors)
    {
        if (_tracked.Contains(door.EntityId)) continue;
        if (door.Status == DoorStatus.Open || door.Status == DoorStatus.Opening)
        {
            _tracked.Add(door.EntityId);
            _queue.Enqueue(new DoorEntry(door, _elapsed));
        }
    }

    Echo("Tracking: " + _queue.Count + " door(s)");
}

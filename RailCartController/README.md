# Rail Cart Controller — development notes

Repo-only notes. Not deployed to the game and not published to the Workshop —
the deploy hook only copies `RailCartController.cs`, `modinfo.sb`, `Workshop.txt`
and `thumb.png`. User-facing documentation lives in `Workshop.txt`.

Published as Steam Workshop item `3763265914`, so **any change has to keep
working for existing subscribers without them editing anything** — their Custom
Data (including captured `[Stops]`), block group names, and wheel groupings all
have to stay valid.

## Why both wheel groups drive at +override

Space Engineers used to require wheels on opposite sides of a grid to take
opposite propulsion override signs. The script compensated by driving the
`RightWheelGroup` at `-override`. A game update fixed the underlying behaviour,
which turned that compensation into a bug — one side of the cart drove backwards.

Both groups now drive at `+override` (`Discover()`). `LeftWheelGroup` and
`RightWheelGroup` kept their names purely so existing configs keep working; they
are now just two drive-wheel group slots with no directional meaning.

`DriveWheel.Sign` was deliberately retained even though it is `+1` everywhere, so
a reversed subset can be reintroduced cheaply (see below). **Do not "simplify" it
away** without reading that section first.

---

## Deferred: robustness pass (recommended next)

Three pre-existing gaps, all independent of the wheel-sign change. Line numbers
are as of commit `0526372`.

**1. Stale overrides survive a setup error → runaway cart.**
`RestoreState()` (`:51-79`) comments that it leaves the wheels neutral when it
can't resume a trip, but it can't deliver on that. `Discover()` runs first in the
constructor; on a setup error `_wheels` is empty, so the `ApplyWheels(0.0, true)`
at `:78` iterates nothing and the wheels keep the `PropulsionOverride` saved with
the world. This re-opens exactly the failure commit `4a74511` was written to
prevent, for the case where the groups are missing. `StopAll()` (`:341`) has the
same hole.

Fix: a fail-safe helper that sweeps *all* `IMyMotorSuspension` on the construct —
not just grouped ones — zeroing override and setting brakes. Call it on setup
error and from `StopAll()`. This also covers wheels that were dropped from a
group mid-session, which currently keep their last override forever.

**2. A missing group fails silently.**
`AddWheelsFromGroup()` (`:487`) returns quietly when the named group doesn't
exist, and `_setupError` is only set when *both* groups came back empty (`:483`).
Typo one group name, or rename it in game, and the script reports a clean setup
while driving one side only. Before the group rework this errored loudly.

This matters more now than it looks: the "pulls to one side" troubleshooting
entry in `Workshop.txt` tells users to check that every drive wheel is in a
group, but the script still won't tell them when a whole group name is wrong.

Fix: set `_setupError` (or at minimum echo a warning) per group when it is
missing or contains no suspensions.

**3. No dedupe across groups.**
A wheel in both groups is added twice (`:494-495`). Harmless for the override
math now that both signs are `+1`, but it inflates the status panel's counts and
would silently misbehave again if a reversed group is ever added. Dedupe by
`EntityId` and warn.

## Deferred: reversed wheel subset

The game fix normalises propulsion direction per block orientation, **not across
subgrid boundaries**. A drive wheel on a rotor/hinge subgrid rotated 180°
relative to the hull still drives opposite to the rest of the cart.

Nobody has reported this, so it was left out rather than shipping a config knob
on speculation. If it comes up: add an optional `ReversedWheelGroup` (default
empty) and feed it through `AddWheelsFromGroup(..., -1f)`. `DriveWheel.Sign` and
`ApplyWheels()` already support it — the change is one config read plus one call.

Note the current workaround available to users is the wheel's own **Invert
Propulsion** checkbox, which is why the status panel reports rather than clears
it.

## Deferred: config naming cleanup

`LeftWheelGroup` / `RightWheelGroup` no longer describe what they do. Renaming is
blocked by the subscriber constraint: a single `DriveWheelGroup` key can't absorb
two existing block groups without users re-grouping blocks in game.

If it's ever worth doing, the path is a `DriveWheelGroup` that accepts a
comma-separated list of group names, with the old keys read as a silent fallback
and a one-time migration that rewrites Custom Data in place (preserving
`[Stops]` — see `SaveStop()` for the read-modify-write pattern). Low value,
non-trivial risk; deliberately not scheduled.

---

## Unscheduled observations

Found during review, not investigated further:

- **Approach can oscillate indefinitely.** In `GotoControl()` (`:291-292`), inside
  `ConnectDistance` the kinematic `allowed` speed is 0 but `Clamp` floors it at
  `_crawlSpeed`, so the cart never actually stops. If the connector never reaches
  `Connectable` (misalignment, wrong connector named), the cart crawls past the
  stop, `tripDir` flips, and it hunts forever. There is no timeout or retry limit.
- **Controller orientation is an undocumented requirement.** Direction sensing
  uses `_controller.WorldMatrix.Forward` (`:288`, `:316`), so a Remote Control
  mounted across the rail breaks `goto` entirely. `Workshop.txt` never states
  this, and the symptom ("drives the wrong way") sends users to `PropulsionSign`,
  which makes it worse.
- **`ApplyWheels()` forces `Propulsion = true` every tick** (`:335`), overriding a
  wheel the player disabled deliberately.
- **Connector name matching is case-sensitive.** `FindConnector()` and
  `CurrentDockedStop()` compare with `==` while stop *names* use
  `OrdinalIgnoreCase`. Inconsistent, though renaming a connector already breaks
  the stop by design.

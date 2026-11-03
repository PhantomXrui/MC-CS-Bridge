# Disable dragon breath for the MC-CS world

Targets the installed Minecraft Java 26.3 (data pack format 121.0).

Copy this directory into the active save's `datapacks` directory, then run
`reload`. Verify `datapack list enabled` includes `file/dragon-breath-off` and
check the game log for function parse errors. A Minecraft restart is unnecessary.
The load function clears existing dragon fireballs and dragon-breath clouds;
the tick function continues clearing them in the three vanilla dimensions.
Dragon movement, targeting, contact attacks and all other mobs are untouched.
Ordinary lingering potion clouds are retained.

## Implementation checked against the installed game

- The bundled `version.json` identifies data version 121.0 (resource version
  97.1 is a different value and must not be used for this data pack).
- `MinecraftServer.tickChildren` invokes `ServerFunctionManager.tick` before
  `ServerLevel.tick` in each dimension.
- `EntityTickList` iterates a stable snapshot. Newly added dragon fireballs do
  not tick during the iteration that created them; the next command-function
  tick removes them before their first move/collision tick.
- `DragonFireball.onHit` creates a cloud, not immediate direct damage.
- Both `DragonFireball` and `DragonSittingFlamingPhase` configure clouds with
  the dragon-breath particle, instantaneous-damage effects, and the default
  20-tick wait before the first damage application.
- `AreaEffectCloud` saves the particle in `custom_particle`. The type remains
  identifiable even if its owner has died or been unloaded.

This prevents native dragon-breath damage while loaded. The client may briefly
render a newly spawned projectile between its spawn and removal packets; this
data pack does not prevent the dragon AI from attempting to fire or playing
the firing sound. Disabling those visual/audio attempts completely requires
a code hook, if desired later. Custom mod dimensions are outside this pack.

## Verification

`tests/verify-bridge.json` is a BridgeControl command list. Run only after this
pack is enabled. It creates two harmless clouds and a stationary fireball high
above the player, checks that dragon breath/fireball vanish while a normal
cloud survives, then cleans up only the entities bearing its unique test tags.
Read the game log for the three `[dragon-breath-test] ... PASS` messages.
These files have not been executed by the authoring sub-agent.

Disable with `datapack disable "file/dragon-breath-off"`; reload the world data
to stop the tick function. Existing entities already removed are not restored.

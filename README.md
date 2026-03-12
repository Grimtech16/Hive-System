# Hive-System
Enemy AI system, which expands and spreads across a given space, evolving units and components over time, routinely attacking the player and recalling units to increase performance.

Hive Overmind = Monitors global resource pool, evolution and messages sent from other components, and initiates expansion of the Hive.

Hive Core = Local producer of resources which are added to the pool, pings the Overmind for expansion when thresholds are reached; and applies node evolution based on number of nodes spawned. (Example: Every Node is T1, Every 5th = T2 and every 10th = T3)

Hive Node = Unit Producer, unit pool monitor, spawner, resource multiplier and detectors.
Primary task is to detect the presence of the player’s assets in a given area, spawn units and relay the initial position of the player.
Node keeps a log of all units pooled “inside” and converts excess units back into resources.

Rupture = Spawns periodically near the players base, spawning units and then despawning; acting solely to apply pressure. 
Enemy Unit = Seek and recall logic, with collision based attacks

Enemy Unit = Basic tag based seeker AI with collision trigger attack, with logic for recall and pooling and attribute updating from global evolution level (Only while pooled)

Hive Tier = Config for nodes to determine what units can be spawned, based on Evolution Level of Node

Enemy Type = Determines stats for enemy prefabs

Unit Hive Binding = Binds individual units to Node they spawned from, and setting what node they will attempt to return to when recalling; or setting new bind if old bind is out of range or destroyed.

IMPORTANT: This system was built as part of a larger RTS project, but may only partially function as an independent system.

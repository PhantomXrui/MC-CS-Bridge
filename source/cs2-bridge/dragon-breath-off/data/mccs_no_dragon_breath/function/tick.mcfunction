# Native dragons create projectiles during entity iteration. The next command-function
# tick removes them before their first entity tick, so they cannot collide or hurt.
execute in minecraft:overworld run function mccs_no_dragon_breath:clear_dimension
execute in minecraft:the_nether run function mccs_no_dragon_breath:clear_dimension
execute in minecraft:the_end run function mccs_no_dragon_breath:clear_dimension

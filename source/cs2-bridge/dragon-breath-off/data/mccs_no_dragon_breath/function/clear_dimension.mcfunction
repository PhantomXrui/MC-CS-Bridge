kill @e[type=minecraft:dragon_fireball]
# 26.3 writes custom_particle, not the old Particle field. Native fireball and
# sitting-breath clouds both use this type; ordinary lingering potion clouds do not.
# This continues to identify clouds after their owner dragon dies.
kill @e[type=minecraft:area_effect_cloud,nbt={custom_particle:{type:"minecraft:dragon_breath"}}]

"""Read bounded original-model rendering diagnostics without changing the game."""
import json
import struct
from shared_map import open_existing

data = open_existing('Local\\MC_CS_PawnDance', 4096)
fields = {
    'on': ('I', 24), 'actors': ('I', 28),
    'body_draws': ('Q', 3088), 'body_skips': ('I', 3100),
    'body_programs': ('I', 3132), 'owner_candidates': ('I', 3192),
    'weapon_programs': ('I', 3220), 'weapon_draws': ('Q', 3224),
    'weapon_skips': ('Q', 3232), 'weapon_last_reason': ('I', 3240),
    'verified_seen': ('I', 3296), 'verified_body': ('I', 3300),
    'verified_weapon': ('I', 3304),
}
result = {key: struct.unpack_from('<'+fmt, data, offset)[0]
          for key, (fmt, offset) in fields.items()}
result['body_reasons'] = list(struct.unpack_from('<12I', data, 3136))
result['weapon_reasons'] = list(struct.unpack_from('<12I', data, 3248))
print(json.dumps(result))

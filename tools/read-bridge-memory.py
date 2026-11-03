import json,struct
from shared_map import open_existing
m=open_existing('Local\\MC_CS_Bridge_Pose',4096)
def at(fmt, off): return struct.unpack_from(fmt,m,off)[0]
print(json.dumps(dict(magic=hex(at('<I',0)),active=at('<i',24),build=at('<i',28),keys=at('<q',96),ui=at('<i',136),focused=at('<i',164),ui_written=at('<q',176),ui_read=at('<q',184))))

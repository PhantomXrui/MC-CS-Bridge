import json, struct, statistics, sys
from datetime import datetime, timezone
from shared_map import open_existing
m=open_existing('Local\\MC_CS_Bridge_Pose',4096)
count=struct.unpack_from('<Q',m,192)[0]
samples=list(struct.unpack_from('<128f',m,2048))
valid=sorted(x for x in samples if 0<x<10000)
if not valid:
    raise SystemExit('No frame samples yet; start the bridge and render several frames before sampling.')
result={'utc':datetime.now(timezone.utc).isoformat(),'frames':count,'n':len(valid),'meanMs':statistics.mean(valid),'p95Ms':valid[min(len(valid)-1,int(len(valid)*.95))],'maxMs':max(valid),'fps':1000/statistics.mean(valid)}
print(json.dumps(result))
if len(sys.argv)>1:
    with open(sys.argv[1],'w',encoding='utf-8') as f:json.dump(result,f,indent=2)

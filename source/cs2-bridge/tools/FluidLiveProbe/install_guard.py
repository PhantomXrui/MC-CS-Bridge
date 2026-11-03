"""Update only HostVitals.class; keep every other installed patch at its current version."""
from pathlib import Path
import hashlib
import os
import zipfile

root = Path(r"D:\MC-CS-Bridge")
target = root / "minecraft/mods/passthrough-0.1.0.jar"
backup = root / "backups/fluid-sync-20261014/passthrough.before.jar"
staged = root / "backups/fluid-sync-20261014/passthrough.fluid-guard.jar"
entry = "dev/rehan/passthrough/HostVitals.class"
patch = (root / "source/universal-modder/examples/minecraft-gta5-passthrough/mc/build/classes/java/main" / entry).read_bytes()
assert hashlib.sha256(backup.read_bytes()).hexdigest() == "34aec5ae09b30689626728c27bafb99e27a16809ec385e9ac5688200d2a23e68"
assert target.read_bytes() == backup.read_bytes(), "Installed JAR changed since backup"
with zipfile.ZipFile(backup) as old, zipfile.ZipFile(staged, "w") as new:
    assert entry in old.namelist()
    for item in old.infolist():
        new.writestr(item, patch if item.filename == entry else old.read(item.filename))
with zipfile.ZipFile(backup) as old, zipfile.ZipFile(staged) as new:
    assert old.namelist() == new.namelist()
    changed = [name for name in old.namelist() if old.read(name) != new.read(name)]
    assert changed == [entry], changed
temp = target.with_suffix(".fluid-update.tmp")
temp.write_bytes(staged.read_bytes())
os.replace(temp, target)
assert target.read_bytes() == staged.read_bytes()
print("Only changed:", entry)
print("Installed SHA256:", hashlib.sha256(target.read_bytes()).hexdigest())

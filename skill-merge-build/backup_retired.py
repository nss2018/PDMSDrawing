"""Back up every ACCIO retired-duplicate dir before moving it out of the skill roots."""
import io
import os
import zipfile

import sys
sys.path.insert(0, r"D:\repos\pdmsDrawing\tools")
import sync_skills_to_accio as s

OUT = r"D:\repos\pdmsDrawing\skill-archive\accio-retired-20261108.zip"
rows = []
for t in s.DEFAULT_TARGETS:
    tag = s.tag_of(t)
    for name in s.RETIRED:
        d = os.path.join(t, name)
        if os.path.isdir(d):
            files = [os.path.join(dp, fn)
                     for dp, dns, fns in os.walk(d)
                     for fn in fns]
            rows.append((tag, name, d, len(files),
                         sum(os.path.getsize(f) for f in files)))

print("will archive %d dirs:" % len(rows))
for tag, name, d, n, size in rows:
    print("  %-22s %-28s %3d files %8dB  %s" % (tag, name, n, size, d))

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    for tag, name, d, _n, _size in rows:
        for dp, dns, fns in os.walk(d):
            for fn in sorted(fns):
                full = os.path.join(dp, fn)
                rel = os.path.relpath(full, d).replace(os.sep, "/")
                z.write(full, "%s/%s/%s" % (tag, name, rel))
with zipfile.ZipFile(OUT) as z:
    assert z.testzip() is None, "backup zip corrupted"
    print("backup -> %s  (%d entries, %d bytes)" % (
        OUT, len(z.namelist()), os.path.getsize(OUT)))

with io.open(r"D:\repos\pdmsDrawing\skill-merge-build\retired_manifest.txt",
             "w", encoding="utf-8", newline="\n") as f:
    for tag, name, d, n, size in rows:
        f.write("%s\t%s\t%s\t%d\t%d\n" % (tag, name, d, n, size))
print("manifest written")

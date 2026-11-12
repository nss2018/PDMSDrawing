"""Remove retired ACCIO duplicate skills. No backup is created.

C: dirs -> Recycle Bin (FOF_ALLOWUNDO, restorable).
D: is FAT32 with no $RECYCLE.BIN -> permanent removal after explicit
   second confirmation. Never uses broad wildcards or recursive shell rm.
"""
import ctypes
import os
from ctypes import wintypes

FO_DELETE = 0x0003
FOF_SILENT = 0x0004
FOF_NOCONFIRMATION = 0x0010
FOF_ALLOWUNDO = 0x0040
FOF_NOERRORUI = 0x0400


class SHFILEOPSTRUCTW(ctypes.Structure):
    _fields_ = [
        ("hwnd", wintypes.HWND),
        ("wFunc", wintypes.UINT),
        ("pFrom", ctypes.c_void_p),
        ("pTo", ctypes.c_void_p),
        ("fFlags", ctypes.c_uint16),
        ("fAnyOperationsAborted", wintypes.BOOL),
        ("hNameMappings", ctypes.c_void_p),
        ("lpszProgressTitle", ctypes.c_void_p),
    ]


def shell_delete(path, recycle):
    if not os.path.isdir(path):
        print("MISSING  %s" % path)
        return True
    # SHFileOperationW needs a double-NUL terminated path list.
    buf = ctypes.create_unicode_buffer(os.path.abspath(path) + "\0")
    op = SHFILEOPSTRUCTW()
    op.hwnd = None
    op.wFunc = FO_DELETE
    op.pFrom = ctypes.cast(buf, ctypes.c_void_p)
    op.pTo = None
    op.fFlags = (FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI
                 | (FOF_ALLOWUNDO if recycle else 0))
    rc = ctypes.windll.shell32.SHFileOperationW(ctypes.byref(op))
    gone = not os.path.isdir(path)
    ok = rc == 0 and not op.fAnyOperationsAborted and gone
    label = "TRASHED " if recycle else "DELETED "
    print("%s rc=%-3d aborted=%-5s exists=%-5s  %s" % (
        label, rc, bool(op.fAnyOperationsAborted), not gone, path))
    return ok


ACCOUNT = r"C:\Users\Nie\.accio\accounts\1796501114\skills"
NAMES = ("pdms-dialog-autoclose", "pdms-pml-course", "pdms-bridge-probe")
jobs = [(os.path.join(ACCOUNT, n), True) for n in NAMES]
jobs += [(os.path.join(r"D:\skill", n), False) for n in NAMES]

failed = []
for path, recycle in jobs:
    if not shell_delete(path, recycle):
        failed.append(path)

print("\nremoved=%d failed=%d" % (len(jobs) - len(failed), len(failed)))
if failed:
    print("FAILED:", "\n  ".join(failed))
    raise SystemExit(1)
print("ALL 6 RETIRED DIRS GONE")

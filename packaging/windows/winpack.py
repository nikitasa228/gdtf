#!/usr/bin/env python3
"""Windows packaging helper of Candela (standard library only; used by build-installer.sh and build-installer.ps1).

  winpack.py stage <repo-root> <publish-dir> <generated-dir>
      Completes the published folder into the installed layout (see docs/install.md and Candela.Engine.InstallLayout):
      data/specs, samples (+ starter fixtures), docs, licences, read-me files, icon, command prompt script; writes
      <generated-dir>/uninstall-files.nsh (one Delete per installed file, one RMDir per folder, deepest first) and
      <generated-dir>/files.txt (the file list with sizes).
  winpack.py check <publish-dir>
      Checks that the folder runs on Windows x64: every native DLL and EXE is x64, and every DLL they import is either
      shipped in the folder, part of Windows 10/11, or one of the expected external components (vulkan-1.dll from the
      graphics driver; vcruntime140*.dll from the Microsoft Visual C++ runtime, which Setup installs). Exit code 1 lists
      what is missing.
  winpack.py resources <file.exe>...    icon, version strings and manifest of executables
  winpack.py imports <file>...          imported DLLs (normal and delay-load)
  winpack.py version <file.exe>         the file version (a.b.c.d) of an executable
"""
import os
import shutil
import struct
import sys

RESOURCE_TYPES = {1: "CURSOR", 3: "ICON", 14: "GROUP_ICON", 16: "VERSION", 24: "MANIFEST", 6: "STRING", 10: "RCDATA"}
MACHINES = {0x8664: "x64", 0x14C: "x86", 0xAA64: "arm64"}

# DLLs every Windows 10/11 x64 installation has (System32), as imported by the published files.
WINDOWS_DLLS = {
    "advapi32.dll", "bcrypt.dll", "cfgmgr32.dll", "comctl32.dll", "comdlg32.dll", "crypt32.dll", "d3d11.dll",
    "d3d12.dll", "d3dcompiler_47.dll", "dbghelp.dll", "dwmapi.dll", "dxgi.dll", "gdi32.dll", "hid.dll", "imm32.dll",
    "iphlpapi.dll", "kernel32.dll", "mscoree.dll", "msvcrt.dll", "mswsock.dll", "ncrypt.dll", "netapi32.dll",
    "normaliz.dll", "ntdll.dll", "ole32.dll", "oleaut32.dll", "opengl32.dll", "psapi.dll", "rpcrt4.dll", "secur32.dll",
    "setupapi.dll", "shell32.dll", "shlwapi.dll", "sspicli.dll", "ucrtbase.dll", "user32.dll", "userenv.dll",
    "uxtheme.dll", "version.dll", "winhttp.dll", "winmm.dll", "wldap32.dll", "ws2_32.dll", "wtsapi32.dll",
}
# Not shipped, provided by something else on purpose.
EXTERNAL_DLLS = {
    "vulkan-1.dll": "the graphics driver (Vulkan runtime)",
    "vcruntime140.dll": "Microsoft Visual C++ 2015-2022 runtime (installed by Setup)",
    "vcruntime140_1.dll": "Microsoft Visual C++ 2015-2022 runtime (installed by Setup)",
    "msvcp140.dll": "Microsoft Visual C++ 2015-2022 runtime (installed by Setup)",
}


class PeFile:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.data = f.read()
        if self.data[:2] != b"MZ":
            raise ValueError("not a PE file")
        pe = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("no PE signature")
        self.machine, nsections = struct.unpack_from("<HH", self.data, pe + 4)
        optional_size = struct.unpack_from("<H", self.data, pe + 20)[0]
        optional = pe + 24
        magic = struct.unpack_from("<H", self.data, optional)[0]
        directories = optional + (112 if magic == 0x20B else 96)
        self.directories = [struct.unpack_from("<II", self.data, directories + 8 * i) for i in range(16)]
        self.sections = []
        table = optional + optional_size
        for i in range(nsections):
            name, vsize, vaddr, rawsize, rawptr = struct.unpack_from("<8sIIII", self.data, table + 40 * i)
            self.sections.append((vaddr, max(vsize, rawsize), rawptr))

    @property
    def is_managed(self):
        """True for .NET assemblies (they have a CLR header); their imports are the runtime's business."""
        return self.directories[14][0] != 0

    def offset(self, rva):
        for vaddr, size, rawptr in self.sections:
            if vaddr <= rva < vaddr + size:
                return rva - vaddr + rawptr
        return None

    def cstring(self, rva):
        off = self.offset(rva)
        if off is None:
            return ""
        return self.data[off:self.data.index(b"\0", off)].decode("ascii", "replace")

    def imports(self):
        normal, delayed = [], []
        rva = self.directories[1][0]
        off = self.offset(rva) if rva else None
        while off is not None:
            fields = struct.unpack_from("<IIIII", self.data, off)
            if fields == (0, 0, 0, 0, 0):
                break
            normal.append(self.cstring(fields[3]))
            off += 20
        rva = self.directories[13][0]
        off = self.offset(rva) if rva else None
        while off is not None:
            fields = struct.unpack_from("<IIIIIIII", self.data, off)
            if fields[1] == 0:
                break
            delayed.append(self.cstring(fields[1]))
            off += 32
        return normal, delayed

    def resources(self):
        rva = self.directories[2][0]
        if not rva:
            return {}
        base = self.offset(rva)
        found = {}

        def entries(directory):
            named, ids = struct.unpack_from("<HH", self.data, directory + 12)
            for i in range(named + ids):
                yield struct.unpack_from("<II", self.data, directory + 16 + 8 * i)

        for type_id, target in entries(base):
            if not target & 0x80000000:
                continue
            leaves = []
            for _, name_target in entries(base + (target & 0x7FFFFFFF)):
                if name_target & 0x80000000:
                    for _, lang_target in entries(base + (name_target & 0x7FFFFFFF)):
                        if not lang_target & 0x80000000:
                            leaves.append(struct.unpack_from("<II", self.data, base + lang_target))
            found[RESOURCE_TYPES.get(type_id, str(type_id)) if not type_id & 0x80000000 else "named"] = leaves
        return found

    def resource_bytes(self, kind):
        leaves = self.resources().get(kind, [])
        if not leaves:
            return b""
        data_rva, size = leaves[0]
        off = self.offset(data_rva)
        return self.data[off:off + size]

    def version_strings(self):
        blob = self.resource_bytes("VERSION").decode("utf-16-le", "replace")
        result = {}
        for key in ("CompanyName", "FileDescription", "FileVersion", "ProductName", "ProductVersion", "LegalCopyright"):
            at = blob.find(key + "\0")
            if at >= 0:
                result[key] = blob[at + len(key):].lstrip("\0").split("\0", 1)[0]
        return result

    def file_version(self):
        raw = self.resource_bytes("VERSION")
        at = raw.find(struct.pack("<I", 0xFEEF04BD))
        if at < 0:
            return None
        ms, ls = struct.unpack_from("<II", raw, at + 8)
        return f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"


def copy(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)


def stage(root, publish, generated):
    here = os.path.join(root, "packaging", "windows")
    # Researched fixture data
    copy(os.path.join(root, "_specs", "fixtures_index.json"), os.path.join(publish, "data", "specs", "fixtures_index.json"))
    copy(os.path.join(root, "_specs", "SCHEMA.md"), os.path.join(publish, "data", "specs", "SCHEMA.md"))
    # Sample scene and the GDTF files it uses
    copy(os.path.join(root, "stages", "Фокина.mvr"), os.path.join(publish, "samples", "Фокина.mvr"))
    with open(os.path.join(here, "starter-fixtures.txt"), encoding="utf-8") as f:
        starters = [line.strip() for line in f if line.strip() and not line.startswith("#")]
    for name in starters:
        copy(os.path.join(root, name), os.path.join(publish, "samples", "fixtures", name))
    # Documentation (Help → Documentation opens this folder)
    docs = os.path.join(publish, "docs")
    if os.path.isdir(docs):
        shutil.rmtree(docs)
    shutil.copytree(os.path.join(root, "docs"), docs)
    # Licences, read-me files, icon, the command prompt script (CRLF for cmd.exe)
    for name in ("Inter-OFL.txt", "Lucide-LICENSE.txt"):
        copy(os.path.join(root, "src", "Candela.App", "Assets", "Fonts", name), os.path.join(publish, "licenses", name))
    copy(os.path.join(here, "THIRD-PARTY-NOTICES.txt"), os.path.join(publish, "THIRD-PARTY-NOTICES.txt"))
    copy(os.path.join(here, "readme-en.txt"), os.path.join(publish, "README.txt"))
    copy(os.path.join(here, "readme-ru.txt"), os.path.join(publish, "README-ru.txt"))
    copy(os.path.join(here, "icon", "candela.ico"), os.path.join(publish, "candela.ico"))
    with open(os.path.join(here, "candela-prompt.cmd"), encoding="utf-8") as f:
        script = f.read().replace("\r\n", "\n").replace("\n", "\r\n")
    with open(os.path.join(publish, "candela-prompt.cmd"), "w", encoding="ascii", newline="") as f:
        f.write(script)
    # Build leftovers that do not belong in an installation
    for dirpath, _, files in os.walk(publish):
        for name in files:
            if name.endswith(".xml") and name.startswith("Candela."):
                os.remove(os.path.join(dirpath, name))
    for name in ("candela-app.xml",):
        path = os.path.join(publish, name)
        if os.path.exists(path):
            os.remove(path)

    # The uninstaller's file list: exactly what was installed, nothing else in the folder is touched.
    os.makedirs(generated, exist_ok=True)
    files, folders = [], set()
    for dirpath, dirnames, filenames in os.walk(publish):
        rel_dir = os.path.relpath(dirpath, publish)
        if rel_dir != ".":
            folders.add(rel_dir)
        for name in filenames:
            rel = os.path.normpath(os.path.join(rel_dir, name)) if rel_dir != "." else name
            files.append((rel, os.path.getsize(os.path.join(dirpath, name))))
    files.sort()
    with open(os.path.join(generated, "uninstall-files.nsh"), "w", encoding="utf-8") as f:
        f.write("; Generated by winpack.py stage: the files and folders of this installation.\n")
        for rel, _ in files:
            f.write('Delete "$INSTDIR\\%s"\n' % rel.replace("/", "\\"))
        for rel in sorted(folders, key=lambda p: (-p.count(os.sep), p)):
            f.write('RMDir "$INSTDIR\\%s"\n' % rel.replace("/", "\\"))
    with open(os.path.join(generated, "files.txt"), "w", encoding="utf-8") as f:
        for rel, size in files:
            f.write("%12d  %s\n" % (size, rel))
    total = sum(size for _, size in files)
    print("staged %d files, %.1f MB" % (len(files), total / 1e6))
    return 0


def check(publish):
    shipped = {name.lower() for name in os.listdir(publish)}
    problems, external, natives = [], {}, 0
    for dirpath, _, filenames in os.walk(publish):
        for name in sorted(filenames):
            if not name.lower().endswith((".dll", ".exe")):
                continue
            path = os.path.join(dirpath, name)
            try:
                pe = PeFile(path)
            except (OSError, ValueError, struct.error) as e:
                problems.append("%s: unreadable (%s)" % (name, e))
                continue
            if pe.is_managed:
                if pe.machine not in (0x8664, 0x14C):
                    problems.append("%s: managed assembly for %s" % (name, MACHINES.get(pe.machine, hex(pe.machine))))
                continue
            natives += 1
            if pe.machine != 0x8664:
                problems.append("%s: built for %s, not x64" % (name, MACHINES.get(pe.machine, hex(pe.machine))))
            normal, delayed = pe.imports()
            for dll in normal + delayed:
                low = dll.lower()
                if low in shipped or low in WINDOWS_DLLS or low.startswith("api-ms-win-") or low.startswith("ext-ms-"):
                    continue
                if low in EXTERNAL_DLLS:
                    external.setdefault(low, set()).add(name)
                    continue
                problems.append("%s imports %s, which is neither shipped nor part of Windows" % (name, dll))
    print("checked %d native files" % natives)
    for dll, users in sorted(external.items()):
        print("  %s (%s) is needed by %s" % (dll, EXTERNAL_DLLS[dll], ", ".join(sorted(users))))
    linux_only = [n for n in shipped if n.endswith((".so", ".dylib")) or ".so." in n]
    for name in linux_only:
        problems.append("%s is a Linux/macOS library in a Windows build" % name)
    for problem in problems:
        print("PROBLEM: " + problem)
    return 1 if problems else 0


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    command = argv[1]
    if command == "stage" and len(argv) == 5:
        return stage(argv[2], argv[3], argv[4])
    if command == "check" and len(argv) == 3:
        return check(argv[2])
    if command == "version" and len(argv) == 3:
        version = PeFile(argv[2]).file_version()
        print(version or "")
        return 0 if version else 1
    status = 0
    for path in argv[2:]:
        try:
            pe = PeFile(path)
        except (OSError, ValueError, struct.error) as e:
            print("%s: %s" % (path, e))
            status = 1
            continue
        if command == "imports":
            normal, delayed = pe.imports()
            print("%s: %s%s" % (path, " ".join(normal), "" if not delayed else "  [delay-load: " + " ".join(delayed) + "]"))
        elif command == "resources":
            types = pe.resources()
            print("%s: %s; resources %s" % (path, MACHINES.get(pe.machine, hex(pe.machine)),
                                           ", ".join("%sx%d" % (k, len(v)) for k, v in sorted(types.items()))))
            for key, value in pe.version_strings().items():
                print("  %s: %s" % (key, value))
            manifest = pe.resource_bytes("MANIFEST").decode("utf-8", "replace")
            if manifest:
                print("  manifest: " + ("per-monitor DPI aware (v2)" if "PerMonitorV2" in manifest else "no DPI settings")
                      + (", long paths" if "longPathAware" in manifest else ""))
        else:
            print(__doc__)
            return 2
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv))

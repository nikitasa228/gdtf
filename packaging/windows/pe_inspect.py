#!/usr/bin/env python3
"""Inspects Windows PE files of a published Candela build without Windows tools.

  pe_inspect.py resources <file.exe>   resource types (icon, version, manifest) and the version strings
  pe_inspect.py imports <file.dll>...  the DLLs each file imports (normal and delay-load import tables)
  pe_inspect.py machine <file>...      the target machine (x64 / x86 / arm64) of each file

Used by build-installer.sh to check that the published folder is complete (every imported DLL is either shipped,
part of Windows, or one of the expected system components such as vulkan-1.dll and the VC++ runtime) and that the
executables carry their icon, version information and manifest. Standard library only.
"""
import struct
import sys

RESOURCE_TYPES = {1: "CURSOR", 3: "ICON", 14: "GROUP_ICON", 16: "VERSION", 24: "MANIFEST", 6: "STRING", 10: "RCDATA"}
MACHINES = {0x8664: "x64", 0x14C: "x86", 0xAA64: "arm64"}


class PeFile:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.data = f.read()
        if self.data[:2] != b"MZ":
            raise ValueError(f"{path}: not a PE file")
        pe = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[pe:pe + 4] != b"PE\0\0":
            raise ValueError(f"{path}: no PE signature")
        self.machine, nsections = struct.unpack_from("<HH", self.data, pe + 4)
        optional_size = struct.unpack_from("<H", self.data, pe + 20)[0]
        optional = pe + 24
        magic = struct.unpack_from("<H", self.data, optional)[0]
        directories = optional + (112 if magic == 0x20B else 96)
        self.is64 = magic == 0x20B
        self.directories = [struct.unpack_from("<II", self.data, directories + 8 * i) for i in range(16)]
        self.sections = []
        table = optional + optional_size
        for i in range(nsections):
            name, vsize, vaddr, rawsize, rawptr = struct.unpack_from("<8sIIII", self.data, table + 40 * i)
            self.sections.append((name.rstrip(b"\0").decode("ascii", "replace"), vaddr, max(vsize, rawsize), rawptr))

    def offset(self, rva):
        for _, vaddr, size, rawptr in self.sections:
            if vaddr <= rva < vaddr + size:
                return rva - vaddr + rawptr
        return None

    def cstring(self, rva):
        off = self.offset(rva)
        if off is None:
            return ""
        end = self.data.index(b"\0", off)
        return self.data[off:end].decode("ascii", "replace")

    def imports(self):
        names = []
        rva, size = self.directories[1]
        if rva:
            off = self.offset(rva)
            while off is not None:
                fields = struct.unpack_from("<IIIII", self.data, off)
                if fields == (0, 0, 0, 0, 0):
                    break
                names.append(self.cstring(fields[3]))
                off += 20
        delayed = []
        rva, size = self.directories[13]
        if rva:
            off = self.offset(rva)
            while off is not None:
                fields = struct.unpack_from("<IIIIIIII", self.data, off)
                if fields[1] == 0:
                    break
                delayed.append(self.cstring(fields[1]))
                off += 32
        return names, delayed

    def resources(self):
        rva, size = self.directories[2]
        if not rva:
            return {}
        base = self.offset(rva)
        found = {}

        def entries(directory):
            named, ids = struct.unpack_from("<HH", self.data, directory + 12)
            for i in range(named + ids):
                name, target = struct.unpack_from("<II", self.data, directory + 16 + 8 * i)
                yield name, target

        for type_id, target in entries(base):
            if not target & 0x80000000:
                continue
            leaves = []
            for _, name_target in entries(base + (target & 0x7FFFFFFF)):
                if name_target & 0x80000000:
                    for _, lang_target in entries(base + (name_target & 0x7FFFFFFF)):
                        if not lang_target & 0x80000000:
                            data_rva, data_size = struct.unpack_from("<II", self.data, base + lang_target)
                            leaves.append((data_rva, data_size))
            label = RESOURCE_TYPES.get(type_id, str(type_id)) if not type_id & 0x80000000 else "named"
            found[label] = leaves
        return found

    def version_strings(self):
        leaves = self.resources().get("VERSION", [])
        if not leaves:
            return {}
        data_rva, data_size = leaves[0]
        off = self.offset(data_rva)
        blob = self.data[off:off + data_size].decode("utf-16-le", "replace")
        result = {}
        for key in ("CompanyName", "FileDescription", "FileVersion", "ProductName", "ProductVersion", "LegalCopyright",
                    "OriginalFilename"):
            at = blob.find(key + "\0")
            if at < 0:
                continue
            rest = blob[at + len(key):].lstrip("\0")
            result[key] = rest.split("\0", 1)[0]
        return result

    def manifest(self):
        leaves = self.resources().get("MANIFEST", [])
        if not leaves:
            return ""
        data_rva, data_size = leaves[0]
        off = self.offset(data_rva)
        return self.data[off:off + data_size].decode("utf-8", "replace")


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    command, files = argv[1], argv[2:]
    status = 0
    for path in files:
        try:
            pe = PeFile(path)
        except (OSError, ValueError, struct.error) as e:
            print(f"{path}: {e}")
            status = 1
            continue
        if command == "imports":
            normal, delayed = pe.imports()
            print(f"{path}: " + " ".join(normal) + ("" if not delayed else "  [delay-load: " + " ".join(delayed) + "]"))
        elif command == "machine":
            print(f"{path}: {MACHINES.get(pe.machine, hex(pe.machine))}")
        elif command == "resources":
            types = pe.resources()
            print(f"{path}: resources " + ", ".join(f"{k}x{len(v)}" for k, v in sorted(types.items())))
            for key, value in pe.version_strings().items():
                print(f"  {key}: {value}")
            manifest = pe.manifest()
            if manifest:
                print("  manifest: " + ("per-monitor DPI aware" if "PerMonitorV2" in manifest else "no DPI settings")
                      + (", long paths" if "longPathAware" in manifest else ""))
        else:
            print(__doc__)
            return 2
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv))

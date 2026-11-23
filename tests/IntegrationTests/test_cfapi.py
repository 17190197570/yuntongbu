"""Real CFAPI integration tests. Creates only isolated fixtures under artifacts/tests."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import unittest
import uuid

REPO = Path(__file__).resolve().parents[2]
HOST = REPO / "artifacts/bin/CloudProviderHost.exe"
KERNEL = ctypes.WinDLL("kernel32", use_last_error=True) if os.name == "nt" else None
if KERNEL:
    KERNEL.CreateEventW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_wchar_p]
    KERNEL.CreateEventW.restype = ctypes.c_void_p
    KERNEL.SetEvent.argtypes = [ctypes.c_void_p]
    KERNEL.CloseHandle.argtypes = [ctypes.c_void_p]


def command(*args, success=True):
    try:
        result = subprocess.run([str(HOST), *map(str, args)], capture_output=True,
                                encoding="utf-8", errors="replace", timeout=30)
    except subprocess.TimeoutExpired as error:
        print("Command timed out; captured output:", error.stdout, error.stderr, flush=True)
        raise
    if success and result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    if not success and not result.returncode:
        raise AssertionError("Unsafe command unexpectedly succeeded: " + repr(args))
    return result


class Fixture:
    def __init__(self):
        self.base = REPO / "artifacts/tests" / uuid.uuid4().hex
        self.source, self.root = self.base / "source", self.base / "cloud"
        self.source.mkdir(parents=True)
        self.root.mkdir()
        self.payloads = {
            "中文资料/测试 文件.bin": os.urandom(3 * 1024 * 1024 + 123),
            "empty.txt": b"",
            "dirty.txt": b"original local data\n",
            "changed-source.txt": b"source version one\n",
            "offline.txt": b"offline recovery content\n",
        }
        for name, payload in self.payloads.items():
            path = self.source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        self.event_name = "Local\\CloudDriveLite.Test." + uuid.uuid4().hex
        self.event = None
        self.provider = None
        self.log = None
        self.log_path = self.base / "provider.log"

    def start(self):
        self.event = KERNEL.CreateEventW(None, True, False, self.event_name)
        if not self.event:
            raise ctypes.WinError(ctypes.get_last_error())
        self.log = self.log_path.open("w", encoding="utf-8")
        self.provider = subprocess.Popen([str(HOST), "run", "--source", str(self.source),
                                         "--root", str(self.root), "--stop-event", self.event_name],
                                        stdout=self.log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            text = self.log_path.read_text(encoding="utf-8", errors="replace")
            if "READY root=" in text:
                return
            if self.provider.poll() is not None:
                raise AssertionError("Provider exited before READY:\n" + text)
            time.sleep(0.05)
        raise AssertionError("Provider startup timeout: " + self.log_path.read_text(encoding="utf-8"))

    def stop(self):
        if self.event:
            KERNEL.SetEvent(self.event)
        if self.provider and self.provider.poll() is None:
            try:
                self.provider.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.provider.kill()
                self.provider.wait(timeout=10)
                raise AssertionError("Provider did not stop within 15 seconds.")
        if self.event:
            KERNEL.CloseHandle(self.event)
            self.event = None
        if self.log:
            self.log.close()
            self.log = None

    def status(self, name):
        return json.loads(command("status", "--root", self.root, "--path", name.replace("/", "\\")).stdout)

    def read(self, name, success=True):
        # A child with a deadline detects missing callback completion without hanging the runner.
        result = subprocess.run([sys.executable, "-c",
                                 "import hashlib,pathlib,sys;print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())",
                                 str(self.root / name)], capture_output=True, encoding="utf-8", timeout=30)
        if success:
            if result.returncode:
                raise AssertionError(result.stderr)
            return result.stdout.strip()
        if not result.returncode:
            raise AssertionError("Reading an unavailable version unexpectedly succeeded.")

    def cleanup_generated_cloud(self):
        # Delete only exact generated fixture names, and only after all tests passed.
        expected = {Path(name) for name in self.payloads} | {Path("中文资料")}
        actual = {p.relative_to(self.root) for p in self.root.rglob("*")}
        if actual != expected:
            raise AssertionError("Unexpected local entries; test artifacts preserved for inspection.")
        for name in self.payloads:
            (self.root / name).unlink()
        (self.root / "中文资料").rmdir()
        command("unregister", "--root", self.root)
        self.root.rmdir()


F = None


@unittest.skipUnless(os.name == "nt", "Requires Windows CFAPI and NTFS")
class CloudFilesTests(unittest.TestCase):
    def test_01_initial_metadata_only(self):
        for name, payload in F.payloads.items():
            with self.subTest(name=name):
                state = F.status(name)
                self.assertTrue(state["placeholder"])
                self.assertEqual(state["logicalSize"], len(payload))
                self.assertEqual(state["onDiskDataSize"], 0)
                self.assertTrue(state["inSync"])

    def test_02_hydration_and_cache(self):
        name = "中文资料/测试 文件.bin"
        expected = hashlib.sha256(F.payloads[name]).hexdigest()
        self.assertEqual(F.read(name), expected)
        self.assertTrue(F.status(name)["hydrated"])
        before = F.log_path.read_text(encoding="utf-8").count("FETCH_DATA ")
        self.assertEqual(F.read(name), expected)
        self.assertEqual(F.log_path.read_text(encoding="utf-8").count("FETCH_DATA "), before)
        self.assertEqual(F.read("empty.txt"), hashlib.sha256(b"").hexdigest())

    def test_03_verified_release_and_rehydrate(self):
        name = "中文资料/测试 文件.bin"
        F.read(name)
        command("release", "--root", F.root, "--path", name.replace("/", "\\"))
        state = F.status(name)
        self.assertTrue(state["placeholder"])
        self.assertEqual(state["onDiskDataSize"], 0)
        self.assertEqual(F.read(name), hashlib.sha256(F.payloads[name]).hexdigest())

    def test_04_dirty_release_refused(self):
        F.read("dirty.txt")
        local = F.root / "dirty.txt"
        local.write_bytes(b"unsynchronized local edits\n")
        command("release", "--root", F.root, "--path", "dirty.txt", success=False)
        self.assertEqual(local.read_bytes(), b"unsynchronized local edits\n")
        self.assertEqual((F.source / "dirty.txt").read_bytes(), F.payloads["dirty.txt"])

    def test_05_ownership_and_traversal(self):
        command("release", "--root", F.root, "--path", "..\\source\\dirty.txt", success=False)
        command("unregister", "--root", F.source, success=False)
        command("run", "--source", F.source, "--root", F.root, success=False)
        command("run", "--source", F.root, "--root", F.source, success=False)

    def test_06_disconnect_restart_and_safe_unregister(self):
        F.stop()
        command("unregister", "--root", F.root, success=False)  # Nonempty roots must survive.
        F.read("offline.txt", success=False)
        F.start()
        self.assertEqual(F.read("offline.txt"), hashlib.sha256(F.payloads["offline.txt"]).hexdigest())
        self.assertEqual((F.root / "dirty.txt").read_bytes(), b"unsynchronized local edits\n")

    def test_07_source_change_refused(self):
        path = F.source / "changed-source.txt"
        original = path.stat()
        try:
            path.write_bytes(b"source version TWO\n")
            F.read("changed-source.txt", success=False)
            self.assertEqual(F.status("changed-source.txt")["onDiskDataSize"], 0)
        finally:
            path.write_bytes(F.payloads["changed-source.txt"])
            os.utime(path, ns=(original.st_atime_ns, original.st_mtime_ns))

    def test_08_source_unchanged(self):
        for name, expected in F.payloads.items():
            self.assertEqual((F.source / name).read_bytes(), expected)

    def test_09_pin_hydrate_and_unpin(self):
        name = "中文资料/测试 文件.bin"
        relative = name.replace("/", "\\")
        command("release", "--root", F.root, "--path", relative)
        command("pin", "--root", F.root, "--path", relative)
        state = F.status(name)
        self.assertTrue(state["pinned"])
        self.assertTrue(state["hydrated"])
        self.assertEqual(state["hydrationPolicy"], 2)  # FULL
        self.assertEqual(state["populationPolicy"], 3)  # ALWAYS_FULL
        self.assertTrue(state["hydrationModifier"] & 4)
        command("release", "--root", F.root, "--path", relative, success=False)
        command("unpin", "--root", F.root, "--path", relative)
        self.assertFalse(F.status(name)["pinned"])
        command("release", "--root", F.root, "--path", relative)
        self.assertEqual(F.status(name)["onDiskDataSize"], 0)
        self.assertEqual(F.read(name), hashlib.sha256(F.payloads[name]).hexdigest())

    def test_10_existing_root_policy_update_preserves_data(self):
        # Restore the old modifier on this isolated owned fixture, then let normal startup migrate it.
        before_id = F.root.stat().st_ino
        before_dirty = (F.root / "dirty.txt").read_bytes()
        def dirty_state():
            result = subprocess.run([str(HOST), "status", "--root", str(F.root), "--path", "dirty.txt"], capture_output=True, encoding="utf-8", timeout=30)
            if result.returncode:
                self.assertIn("0x80070178", result.stdout + result.stderr)
                return None  # Truncation can turn this user-modified file into an ordinary file.
            value = json.loads(result.stdout)
            self.assertFalse(value["inSync"])
            return value["inSync"]
        before_state = dirty_state()
        F.stop()
        api = ctypes.WinDLL("cldapi")
        buffer = ctypes.create_string_buffer(67000)
        returned = ctypes.c_uint32()
        api.CfGetSyncRootInfoByPath.argtypes = [ctypes.c_wchar_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
        api.CfGetSyncRootInfoByPath.restype = ctypes.c_int32
        self.assertEqual(api.CfGetSyncRootInfoByPath(str(F.root), 1, buffer, len(buffer), ctypes.byref(returned)), 0)
        length = ctypes.c_uint32.from_buffer(buffer, 1052).value
        identity = ctypes.create_string_buffer(buffer.raw[1056:1056 + length])
        class Registration(ctypes.Structure):
            _fields_ = [("size", ctypes.c_uint32), ("name", ctypes.c_wchar_p), ("version", ctypes.c_wchar_p),
                        ("identity", ctypes.c_void_p), ("length", ctypes.c_uint32), ("file_identity", ctypes.c_void_p),
                        ("file_length", ctypes.c_uint32), ("provider", ctypes.c_ubyte * 16)]
        class Policies(ctypes.Structure):
            _fields_ = [("size", ctypes.c_uint32), ("hydration", ctypes.c_uint32), ("population", ctypes.c_uint32),
                        ("insync", ctypes.c_uint32), ("hardlink", ctypes.c_uint32), ("management", ctypes.c_uint32)]
        reg = Registration(ctypes.sizeof(Registration), "CloudDrive Lite Local Prototype", "0.1.0", ctypes.addressof(identity), length,
                           None, 0, (ctypes.c_ubyte * 16).from_buffer_copy(uuid.UUID("03c53dcb-6614-4826-a3da-a536518c518f").bytes_le))
        policies = Policies(ctypes.sizeof(Policies), 2, 3, 0x11, 0, 0)
        api.CfRegisterSyncRoot.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(Registration), ctypes.POINTER(Policies), ctypes.c_uint32]
        api.CfRegisterSyncRoot.restype = ctypes.c_int32
        self.assertEqual(api.CfRegisterSyncRoot(str(F.root), ctypes.byref(reg), ctypes.byref(policies), 1 | 2), 0)
        refused = command("release", "--root", F.root, "--path", "中文资料\\测试 文件.bin", success=False)
        self.assertIn("AUTO_DEHYDRATION_ALLOWED is missing", refused.stdout + refused.stderr)
        F.start()
        state = F.status("中文资料/测试 文件.bin")
        self.assertTrue(state["hydrationModifier"] & 4)
        self.assertEqual(F.root.stat().st_ino, before_id)
        self.assertEqual((F.root / "dirty.txt").read_bytes(), before_dirty)
        self.assertEqual(dirty_state(), before_state)
        self.assertIn("POLICIES_UPDATED", F.log_path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    if os.name != "nt" or not HOST.exists():
        sys.exit("Requires Windows and artifacts/bin/CloudProviderHost.exe; run scripts/build.ps1.")
    F = Fixture()
    print("Isolated fixtures:", F.base, flush=True)
    try:
        F.start()
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(CloudFilesTests))
    except (OSError, AssertionError, subprocess.TimeoutExpired) as error:
        print("BLOCKED/FAILED:", error, file=sys.stderr)
        print("Fixtures and logs preserved:", F.base, file=sys.stderr)
        F.stop()
        sys.exit(1)
    finally:
        F.stop()
    if result.wasSuccessful():
        F.cleanup_generated_cloud()
        print("PASS: generated cloud entries removed, empty root unregistered; source fixtures and logs retained.")
    else:
        print("FAILED: fixtures and root registration preserved for inspection:", F.base)
    sys.exit(0 if result.wasSuccessful() else 1)

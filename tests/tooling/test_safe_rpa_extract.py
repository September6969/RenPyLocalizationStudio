import importlib.util
import os
import pathlib
import pickle
import tempfile
import unittest
import zlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("safe_rpa_extract", ROOT / "tools" / "safe_rpa_extract.py")
SAFE_RPA = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SAFE_RPA)


class SafeRpaExtractTests(unittest.TestCase):
    def test_reads_and_atomically_extracts_normal_rpa2(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            archive = root / "normal.rpa"
            data = b"hello"
            header_length = len(b"RPA-2.0 00000000\n")
            index_offset = header_length + len(data)
            header = b"RPA-2.0 %08x\n" % index_offset
            index = {"dir/a.txt": [(len(header), len(data))]}
            archive.write_bytes(header + data + zlib.compress(pickle.dumps(index, protocol=2)))

            entries = SAFE_RPA.read_index(archive)
            name, segments, _length = entries[0]
            target = SAFE_RPA.safe_target(root / "output", name)
            self.assertTrue(SAFE_RPA.copy_entry(archive, root / "output", target, segments))
            self.assertEqual(data, pathlib.Path(target).read_bytes())

    def test_extracts_multisegment_entry_in_order(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            archive = root / "multi.rpa"
            first, second = b"hello ", b"world"
            header_size = len(b"RPA-2.0 00000000\n")
            index_offset = header_size + len(first) + len(second)
            header = b"RPA-2.0 %08x\n" % index_offset
            index = {"a.txt": [(len(header), len(first)), (len(header) + len(first), len(second), b"!")]}
            archive.write_bytes(header + first + second + zlib.compress(pickle.dumps(index, protocol=2)))

            name, segments, _length = SAFE_RPA.read_index(archive)[0]
            target = SAFE_RPA.safe_target(root / "output", name)
            self.assertTrue(SAFE_RPA.copy_entry(archive, root / "output", target, segments))
            self.assertEqual(b"hello !worl", pathlib.Path(target).read_bytes())

    def test_rejects_pickle_global_without_executing_it(self):
        with tempfile.TemporaryDirectory() as temporary:
            marker = pathlib.Path(temporary) / "executed.txt"

            class Payload:
                def __reduce__(self):
                    return os.system, (f'cmd /c echo owned>"{marker}"',)

            payload = pickle.dumps(Payload(), protocol=2)
            with self.assertRaises(pickle.UnpicklingError):
                SAFE_RPA.RestrictedUnpickler(__import__("io").BytesIO(payload), encoding="latin1").load()
            self.assertFalse(marker.exists())

    def test_rejects_entry_and_total_resource_limits(self):
        old_limit = SAFE_RPA.MAX_ENTRY_BYTES
        try:
            SAFE_RPA.MAX_ENTRY_BYTES = 1
            with tempfile.TemporaryDirectory() as temporary:
                archive = pathlib.Path(temporary) / "large.rpa"
                header_size = len(b"RPA-2.0 00000000\n")
                header = b"RPA-2.0 %08x\n" % (header_size + 2)
                archive.write_bytes(header + b"xx" + zlib.compress(pickle.dumps({"a": [(len(header), 2)]}, protocol=2)))
                with self.assertRaises(ValueError):
                    SAFE_RPA.read_index(archive)
        finally:
            SAFE_RPA.MAX_ENTRY_BYTES = old_limit


if __name__ == "__main__":
    unittest.main()

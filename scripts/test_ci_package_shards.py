"""Regression checks for CI sharding and dependency-cache isolation."""
import itertools
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import test_automation_package as package_tests


class PackageShardTests(unittest.TestCase):
    def test_shards_cover_full_matrix_exactly_once(self):
        expected = list(itertools.product(package_tests.MATRIX["targetFrameworks"],
                                          package_tests.MATRIX["architectures"]))
        shards = [case for framework in package_tests.MATRIX["targetFrameworks"]
                  for case in package_tests.selected_cases(framework)]
        self.assertEqual(expected, package_tests.selected_cases(None))
        self.assertEqual(expected, shards)
        self.assertEqual(len(shards), len(set(shards)))

    def test_cache_excludes_package_under_test_and_preserves_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            work, cache = Path(directory), Path(directory) / "dependencies"
            self.write_package(work, "wpflens.automation", "local package")
            self.write_package(work, "third.party", "dependency")
            package_tests.cache_dependencies(work, cache)
            package_tests.cache_dependencies(work, cache)
            self.assertFalse((cache / "wpflens.automation").exists())
            self.assertEqual("dependency", (cache / "third.party/1.0/data").read_text())

    def test_fallback_and_package_sources_keep_local_sdk_isolated(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            cache = work / "cache & dependencies"
            package_tests.write_nuget_config(work, work / "packages", cache)
            config = ET.parse(work / "NuGet.Config")
            fallback = config.find("fallbackPackageFolders/add")
            self.assertEqual(str(cache.resolve()), fallback.attrib["value"])
            mapping = config.find("packageSourceMapping/packageSource[@key='local-sdk']/package")
            self.assertEqual("WpfLens.Automation", mapping.attrib["pattern"])

    def test_rejects_cache_containing_stale_local_sdk(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            cache = work / "dependencies"
            (cache / "WpfLens.Automation").mkdir(parents=True)
            with self.assertRaisesRegex(ValueError, "must not contain WpfLens"):
                package_tests.write_nuget_config(work, work, cache)

    @staticmethod
    def write_package(work, name, content):
        version = work / "nuget-cache" / name / "1.0"
        version.mkdir(parents=True)
        (version / "data").write_text(content)


if __name__ == "__main__":
    unittest.main()

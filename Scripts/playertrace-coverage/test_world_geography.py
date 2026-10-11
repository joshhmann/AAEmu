import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('prepare_world_geography', Path(__file__).with_name('prepare_world_geography.py'))
geography = importlib.util.module_from_spec(spec)
spec.loader.exec_module(geography)


class GeographyTests(unittest.TestCase):
    def test_server_coordinate_transform_includes_entity_cell_and_world_origin(self):
        self.assertEqual(geography.world_point([2, 3, 4], [10, 20, 30], [12, 13], [1, 2]),
                         [13324, 15383, 34])

    def test_comments_do_not_corrupt_urls_or_quoted_comment_text(self):
        data = geography.lenient_json('[{"source":"https://example/a", "name":"/* quoted */"}, // note\n]')
        self.assertEqual(data, [{'source': 'https://example/a', 'name': '/* quoted */'}])

    def test_invalid_coordinates_are_rejected(self):
        for value in ('nan,0,0', '0,inf,0', '1,2'):
            with self.assertRaises(ValueError):
                geography.vector(value)

    def test_npc_without_altitude_remains_visible_without_invented_height(self):
        self.assertEqual(geography.spawn_point({'X': 100, 'Y': 200}), [100, 200, None])
        self.assertIsNone(geography.spawn_point({'X': 100}))
        self.assertIsNone(geography.spawn_point({'X': 100, 'Y': float('inf')}))

    def test_reference_database_mismatch_stops_export(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            db = root / 'wrong.sqlite3'
            db.write_bytes(b'wrong version')
            with self.assertRaisesRegex(ValueError, 'differs'):
                geography.build(root, root, db)


if __name__ == '__main__':
    unittest.main()

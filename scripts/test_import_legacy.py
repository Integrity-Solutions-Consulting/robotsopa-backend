import base64
import pathlib
import sqlite3
import tempfile
import unittest

from import_legacy_staging import snapshot


class ImportSnapshotTests(unittest.TestCase):
    def test_preserves_null_blob_and_unicode_without_changing_source(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'panel.db'
            with sqlite3.connect(path) as db:
                db.execute('CREATE TABLE users (name TEXT, salt BLOB, value TEXT)')
                db.execute('INSERT INTO users VALUES (?, ?, ?)', ('Ivón', b'\x00\xff', None))
            before = path.read_bytes()
            data = snapshot(path)['users']
            self.assertEqual(data['columns'], ['name', 'salt', 'value'])
            self.assertEqual(data['rows'], [['Ivón', {'base64': base64.b64encode(b'\x00\xff').decode()}, None]])
            self.assertEqual(path.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()

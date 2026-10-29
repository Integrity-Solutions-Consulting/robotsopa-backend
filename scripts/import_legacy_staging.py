"""Importa una copia SQLite/JSON a un área de ensayo PostgreSQL, nunca al modelo activo."""
import argparse
import base64
import json
import pathlib
import sqlite3
import subprocess
import sys


def snapshot(path):
    source = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)
    copy = sqlite3.connect(':memory:')
    try:
        source.backup(copy)
        names = [row[0] for row in copy.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
        result = {}
        for name in names:
            escaped = name.replace('"', '""')
            cursor = copy.execute('SELECT * FROM "' + escaped + '"')
            columns = [column[0] for column in cursor.description]
            def encode(value):
                return {'base64': base64.b64encode(value).decode('ascii')} if isinstance(value, bytes) else value
            result[name] = {'columns': columns, 'rows': [[encode(value) for value in row] for row in cursor]}
        return result
    finally:
        copy.close()
        source.close()


def encoded(value):
    return base64.b64encode(json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8')).decode('ascii')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sqlite', required=True, type=pathlib.Path)
    parser.add_argument('--state', required=True, type=pathlib.Path)
    args = parser.parse_args()
    if not args.sqlite.is_file() or not args.state.is_file():
        parser.error('Ambos archivos de origen deben existir.')
    state = json.loads(args.state.read_text(encoding='utf-8-sig'))
    if not isinstance(state, dict):
        parser.error('El estado debe ser un objeto JSON.')
    sqlite = snapshot(args.sqlite)
    sql = "BEGIN; INSERT INTO sri_legacy_imports(state,sqlite_snapshot) VALUES (convert_from(decode('%s','base64'),'UTF8')::jsonb,convert_from(decode('%s','base64'),'UTF8')::jsonb); COMMIT;" % (encoded(state), encoded(sqlite))
    subprocess.run(['psql', '-X', '-v', 'ON_ERROR_STOP=1'], input=sql, text=True, check=True, stdout=subprocess.DEVNULL)
    print('Copia importada al área de ensayo. No se ha activado ni migrado la aplicación.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, sqlite3.Error, subprocess.CalledProcessError) as error:
        print('Falló la importación: ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)

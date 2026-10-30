"""Import a stopped application's SQLite and JSON into an empty migrated database.

Requires psql and PGHOST/PGPORT/PGDATABASE/PGUSER plus a protected .pgpass.
The backup directory must be new. Never run while any legacy writer is active.
"""
import argparse
import base64
import hashlib
import json
import pathlib
import sqlite3
import subprocess


def literal(value):
    if value is None:
        return 'NULL'
    data = str(value).encode('utf-8')
    return "convert_from(decode('%s','base64'),'UTF8')" % base64.b64encode(data).decode('ascii')


def blob(value):
    return 'NULL' if value is None else "decode('%s','base64')" % base64.b64encode(value).decode('ascii')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--sqlite', type=pathlib.Path, required=True)
    p.add_argument('--state', type=pathlib.Path, required=True)
    p.add_argument('--backup-dir', type=pathlib.Path, required=True)
    a = p.parse_args()
    a.backup_dir.mkdir(mode=0o700, parents=True, exist_ok=False)
    source = sqlite3.connect(a.sqlite.resolve().as_uri() + '?mode=ro', uri=True)
    backup_path = a.backup_dir / 'panel.db'
    with sqlite3.connect(backup_path) as backup:
        source.backup(backup)
    source.close()
    state_bytes = a.state.read_bytes()
    (a.backup_dir / 'state.json').write_bytes(state_bytes)
    state = json.loads(state_bytes.decode('utf-8-sig'))
    if not isinstance(state, dict):
        raise ValueError('state.json debe contener un objeto')
    db = sqlite3.connect(backup_path)
    db.row_factory = sqlite3.Row
    if db.execute('PRAGMA integrity_check').fetchone()[0] != 'ok' or db.execute('PRAGMA foreign_key_check').fetchone():
        raise ValueError('SQLite no supera validación de integridad')
    users = list(db.execute('SELECT * FROM users'))
    accounts = list(db.execute('SELECT * FROM ruc'))
    assignments = list(db.execute('SELECT * FROM asignacion_ruc'))
    preferences = list(db.execute('SELECT * FROM user_preferences'))
    audits = list(db.execute('SELECT * FROM audit'))
    by_id = {x['id']: x for x in state.get('accounts', [])}
    if len(by_id) != len(state.get('accounts', [])):
        raise ValueError('IDs RUC duplicados en JSON')
    active_ids = {r['id'] for r in accounts if r['active']}
    if set(by_id) != active_ids:
        raise ValueError('Cuentas activas divergentes entre SQLite y JSON')
    usernames = {u['username'].lower() for u in users}
    if any(r['username'].lower() not in usernames for r in preferences):
        raise ValueError('Preferencias de usuario inexistente')
    for r in accounts:
        if r['active'] and (r['id'] not in by_id or by_id[r['id']].get('name') != r['name'] or by_id[r['id']].get('taxId') != r['tax_id']):
            raise ValueError('RUC divergente entre SQLite y JSON; reconciliar antes de importar')
    by_audit = {x['id']: x for x in state.setdefault('audit', [])}
    if len(by_audit) != len(state['audit']):
        raise ValueError('IDs de auditoría duplicados en JSON')
    for r in audits:
        event = dict(id=r['id'], user=r['username'], action=r['action'], at=r['at'], account=r['account'], result=r['result'], isTest=bool(r['is_test']), accountName=r['account_name'])
        if r['id'] in by_audit and any(by_audit[r['id']].get(k) != v for k, v in event.items() if k != 'at'):
            raise ValueError('Auditoría divergente; reconciliar antes de importar')
        if r['id'] not in by_audit:
            state['audit'].append(event)
    fingerprint = hashlib.sha256(backup_path.read_bytes() + state_bytes).hexdigest()
    sql = ['BEGIN;', 'SELECT pg_advisory_xact_lock(1096041027);',
           'CREATE TABLE IF NOT EXISTS legacy_import_receipts (fingerprint text PRIMARY KEY, imported_at timestamptz NOT NULL DEFAULT now());',
           "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM legacy_import_receipts WHERE fingerprint='%s') AND (EXISTS(SELECT 1 FROM panel_users) OR EXISTS(SELECT 1 FROM panel_accounts) OR EXISTS(SELECT 1 FROM panel_audit) OR EXISTS(SELECT 1 FROM app_state WHERE id=1 AND payload <> '{}'::jsonb)) THEN RAISE EXCEPTION 'destination is not empty'; END IF; END $$;" % fingerprint,
           "SELECT NOT EXISTS (SELECT 1 FROM legacy_import_receipts WHERE fingerprint='%s') AS should_import \\gset" % fingerprint,
           '\\if :should_import']
    for u in users:
        change = bool(u['must_change']) if 'must_change' in u.keys() else False
        sql.append('INSERT INTO panel_users(username,role,salt,password_hash,must_change) VALUES (%s,%s,%s,%s,%s);' % (literal(u['username']), literal(u['role']), blob(u['salt']), blob(u['password_hash']), str(change).lower()))
    for r in accounts:
        test = bool(by_id.get(r['id'], {}).get('isTest', False))
        sql.append('INSERT INTO panel_accounts(id,name,tax_id,active,is_test) VALUES (%s,%s,%s,%s,%s);' % (literal(r['id']), literal(r['name']), literal(r['tax_id']), str(bool(r['active'])).lower(), str(test).lower()))
    for row in assignments:
        sql.append('INSERT INTO panel_assignments(username,account_id) SELECT username,%s FROM panel_users WHERE lower(username)=lower(%s);' % (literal(row['ruc_id']), literal(row['username'])))
    for row in preferences:
        sql.append('INSERT INTO panel_preferences(username,default_account,download_subfolder) SELECT username,%s,%s FROM panel_users WHERE lower(username)=lower(%s);' % (literal(row['default_account']), literal(row['download_subfolder']), literal(row['username'])))
    for event in state['audit']:
        sql.append('INSERT INTO panel_audit(id,username,action,occurred_at,account_id,result,is_test,account_name) VALUES (%s,%s,%s,%s::timestamptz,%s,%s,%s,%s);' % (literal(event['id']), literal(event['user']), literal(event['action']), literal(event['at']), literal(event.get('account')), literal(event['result']), str(bool(event.get('isTest'))).lower(), literal(event.get('accountName'))))
    sql.append('UPDATE app_state SET payload=%s::jsonb WHERE id=1;' % literal(json.dumps(state, ensure_ascii=False, separators=(',', ':'))))
    sql.append("DO $$ BEGIN IF (SELECT count(*) FROM panel_users) <> %d OR (SELECT count(*) FROM panel_accounts) <> %d OR (SELECT count(*) FROM panel_assignments) <> %d OR (SELECT count(*) FROM panel_preferences) <> %d OR (SELECT count(*) FROM panel_audit) <> %d OR NOT EXISTS (SELECT 1 FROM app_state WHERE id=1) THEN RAISE EXCEPTION 'import count mismatch'; END IF; END $$;" % (len(users), len(accounts), len(assignments), len(preferences), len(state['audit'])))
    sql.append("INSERT INTO legacy_import_receipts(fingerprint) VALUES ('%s');" % fingerprint)
    sql.extend(['\\endif', 'COMMIT;'])
    subprocess.run(['psql', '-X', '-v', 'ON_ERROR_STOP=1'], input='\n'.join(sql) + '\n', text=True, check=True)
    print('Importación completada o ya registrada; copias en', a.backup_dir)
    print('Origen: usuarios=%d RUC=%d asignaciones=%d preferencias=%d auditoría=%d' % (len(users), len(accounts), len(assignments), len(preferences), len(state['audit'])))


if __name__ == '__main__':
    main()

import sqlite3
c = sqlite3.connect('/root/AAEmu/.server_files/AAEmu.Game/Data/compact.sqlite3')
tables = [r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
print(f"TOTAL TABLES: {len(tables)}")
for t in tables:
    print(t)

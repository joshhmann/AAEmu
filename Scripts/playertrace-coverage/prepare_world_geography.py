#!/usr/bin/env python3
"""Build atlas reference layers from bounded client XML and read-only server data.

Never creates playable roads, housing permissions or live NPC observations.
"""
import argparse
import hashlib
import json
import math
import re
import sqlite3
import xml.etree.ElementTree as ET
from pathlib import Path

DB_MD5 = '78b3bdbf038db3b927056106efdf91af'
SERVICES = ('merchant', 'banker', 'auctioneer', 'priest', 'trader', 'specialty',
            'repairman', 'skill_trainer', 'stabler')


def lenient_json(text):
    # Preserve string literals, including URLs, while removing source JSON comments.
    tokens = r'"(?:\\.|[^"\\])*"|//[^\n]*|/\*.*?\*/'
    text = re.sub(tokens, lambda m: m[0] if m[0].startswith('"') else '', text, flags=re.S)
    return json.loads(re.sub(r'"(?:\\.|[^"\\])*"|,\s*(?=[}\]])',
                            lambda m: m[0] if m[0].startswith('"') else '', text))


def vector(text):
    result = [float(n) for n in text.split(',')]
    if len(result) != 3 or not all(math.isfinite(n) for n in result):
        raise ValueError('Expected three finite coordinates')
    return result


def world_point(local, position, origin, cell=(0, 0)):
    return [round(local[0] + position[0] + (origin[0] + cell[0]) * 1024, 6),
            round(local[1] + position[1] + (origin[1] + cell[1]) * 1024, 6),
            round(local[2] + position[2], 6)]


def spawn_point(position):
    point = [position.get(k) for k in ('X', 'Y', 'Z')]
    if not all(type(n) in (int, float) and math.isfinite(n) for n in point[:2]):
        return None
    if point[2] is not None and (type(point[2]) not in (int, float) or not math.isfinite(point[2])):
        return None
    return point  # Missing altitude stays unknown; never manufacture Z=0.


def build(repo, extracted, database):
    if hashlib.md5(database.read_bytes()).hexdigest() != DB_MD5:
        raise ValueError('Reference database differs from the recorded ArcheAge 1.2 baseline')
    with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True) as db:
        db.row_factory = sqlite3.Row
        zones = {r['zone_key']: dict(r) for r in db.execute('SELECT id,zone_key,name,group_id FROM zones')}
        names = {(r['tbl_name'], r['tbl_column_name'], r['idx']): r['en_us'] for r in db.execute(
            "SELECT tbl_name,tbl_column_name,idx,en_us FROM localized_texts WHERE tbl_name IN ('zones','sub_zones','npcs') AND en_us IS NOT NULL AND en_us != ''")}
        subzones = {r['id']: dict(r) for r in db.execute('SELECT id,name,category_id FROM sub_zones')}
        housing = {}
        for row in db.execute('SELECT id,name,comments,housing_group_id FROM housing_areas'):
            housing.setdefault((row['name'], row['comments']), []).append(dict(row))
        npcs = {r['id']: dict(r) for r in db.execute('SELECT id,name,level AS level,' + ','.join(SERVICES) + ' FROM npcs')}
    inventory = json.loads((extracted / 'inventory.json').read_text())
    world_path = 'game/worlds/main_world/world.xml'
    world = ET.parse(extracted / world_path).getroot()
    origins = {int(z.get('id')): (int(z.get('originX')), int(z.get('originY')))
               for z in world.iter('Zone')}
    features, sources, omitted = [], [], []
    for entry in inventory['entries']:
        path = entry['path']
        raw = (extracted / path).read_bytes()
        sources.append({'path': path, 'sha256': hashlib.sha256(raw).hexdigest(), 'bytes': len(raw)})
        match = re.search(r'/zone/(\d+)/', path)
        if not match:
            continue
        key = int(match[1])
        if key not in origins:
            omitted.append({'path': path, 'reason': 'World XML has no zone origin'})
            continue
        zone = zones.get(key, {})
        common = {'world_id': 0, 'world_name': 'main_world', 'zone_key': key,
                  'zone_id': zone.get('id'), 'zone_group_id': zone.get('group_id'),
                  'zone_name': names.get(('zones', 'display_text', zone.get('id')), zone.get('name')),
                  'source': path, 'usable_for_navigation': False}
        root = ET.fromstring(raw)
        if path.endswith('.lyr'):
            for index, obj in enumerate(root.iter('Object')):
                if obj.get('Type') != 'Road':
                    continue
                # Rotations/scales are not resolved. Even identity transforms remain candidates.
                points = [world_point(vector(p.get('Pos')), vector(obj.get('Pos', '0,0,0')), origins[key])
                          for p in obj.findall('Points/Point')]
                features.append(dict(common, id=f'{path}#road-{index}', kind='road', name=obj.get('Name'),
                    points=points, width=float(obj.get('Width', '0')), confidence='heuristic',
                    verification='Editor road; world transform and walkability unverified',
                    editor_transform={k: obj.get(k) for k in ('Rotate', 'Scale')}))
            continue
        kind = 'housing' if path.endswith('housing_area.xml') else 'subzone'
        for entity_index, entity in enumerate(root.findall('Entity')):
            position = vector(entity.get('Pos', '0,0,0'))
            cell = (int(entity.get('cellX', '0')), int(entity.get('cellY', '0')))
            for index, area in enumerate(entity.findall('Area')):
                points = [world_point(vector(p.get('Pos')), position, origins[key], cell)
                          for p in area.findall('Points/Point')]
                if len(points) < 3:
                    continue
                sid = int(area.get('Id', '0'))
                rows = housing.get((zone.get('name'), entity.get('Name')), []) if kind == 'housing' else []
                features.append(dict(common, id=f'{path}#{entity_index}-{index}', kind=kind,
                    name=(names.get(('sub_zones', 'name', sid), subzones.get(sid, {}).get('name'))
                          if kind == 'subzone' else entity.get('Name')),
                    points=points, subzone_id=sid if kind == 'subzone' else None,
                    housing_rule_ids=[r['id'] for r in rows], confidence='exact',
                    verification='Server coordinate transform; reference area only, not occupancy or safety'))
    for path in sorted((repo / 'AAEmu.Game/Data/Worlds/main_world').glob('npc_spawns*.json')):
        raw = path.read_bytes()
        source = path.relative_to(repo).as_posix()
        sources.append({'path': source, 'sha256': hashlib.sha256(raw).hexdigest(), 'bytes': len(raw)})
        for index, spawn in enumerate(lenient_json(raw.decode('utf-8-sig'))):
            template = npcs.get(spawn['UnitId'], {})
            services = [s for s in SERVICES if str(template.get(s, '')).lower() in ('t', '1', 'true')]
            pos = spawn.get('Position', {})
            point = spawn_point(pos)
            if point is None:
                omitted.append({'path': source, 'entry': index, 'reason': 'Invalid spawn coordinates'})
                continue
            features.append({'id': f'{source}#{index}', 'kind': 'npc', 'world_id': 0,
                'npc_template_id': spawn['UnitId'], 'name': names.get(('npcs', 'name', spawn['UnitId']), template.get('name')),
                'level': template.get('level'), 'services': services, 'points': [point], 'source': source,
                'altitude_known': point[2] is not None,
                'confidence': 'exact', 'verification': 'Configured NPC placement; availability and hostility require live observation',
                'usable_for_navigation': False})
    return {'schema_version': 1, 'world_id': 0, 'client_version': '1.2 r208022 (asset label)',
            'database_md5': DB_MD5, 'sources': sources, 'features': features, 'omitted': omitted,
            'coverage': {'zone_origins': len(origins), 'area_files': sum(s['path'].endswith('_area.xml') for s in sources),
                         'layer_files': sum(s['path'].endswith('.lyr') for s in sources),
                         'locale_overrides': 'excluded', 'live_world': 'not sampled'},
            'limitations': ['Reference geography does not prove legal movement, vacant housing or live NPC availability.',
                            'Town names and housing boundaries do not establish PvP protection.',
                            'Editor roads cannot be used as executable travel routes.']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--extracted', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    data = build(args.repo, args.extracted, args.repo / 'AAEmu.Game/Data/compact.sqlite3')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n')
    print(json.dumps({'features': {k: sum(f['kind'] == k for f in data['features'])
                                 for k in ('housing', 'subzone', 'road', 'npc')}, 'coverage': data['coverage']}))


if __name__ == '__main__':
    main()

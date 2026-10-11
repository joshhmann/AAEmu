#!/usr/bin/env python3
"""Map canonical quest references; never selects goals or evaluates live eligibility."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
import xml.etree.ElementTree as ET
from prepare_world_geography import DB_MD5, lenient_json, spawn_point, world_point


def truth(value):
    return value is True or value == 1 or str(value).lower() in ('t', 'true')


def rows(db, table):
    # Table names come exclusively from the fixed type inventory below.
    return {r['id']: dict(r) for r in db.execute('SELECT * FROM ' + table)}


def parse_areas(extracted):
    world = ET.parse(extracted / 'game/worlds/main_world/world.xml').getroot()
    origins = {int(z.get('id')): (int(z.get('originX')), int(z.get('originY'))) for z in world.iter('Zone')}
    result, sources = defaultdict(list), []
    pattern = re.compile(r'^area\s+qtype\s+(\d+)\s+ctype\s+(\d+)\s+pos\s*\(\s*x\s*([^,]+),\s*y\s*([^,]+),\s*z\s*([^\)]+)\)\s+radius\s+(\S+)', re.M | re.I)
    inventory = json.loads((extracted / 'inventory.json').read_text())
    for entry in inventory['entries']:
        path = entry['path']
        raw = (extracted / path).read_bytes()
        sources.append({'path': path, 'sha256': hashlib.sha256(raw).hexdigest(), 'bytes': len(raw)})
        if not path.endswith('quest_sign_sphere.g'):
            continue
        zone = int(re.search(r'/zone/(\d+)/', path)[1])
        text = raw.decode('utf-8-sig')
        matches = list(pattern.finditer(text))
        # Fail visibly rather than silently dropping malformed area declarations.
        if len(matches) != len(re.findall(r'^\s*area\s*$', text, re.M)):
            raise ValueError('Unparsed quest area in ' + path)
        for i, m in enumerate(matches):
            qtype, component = int(m[1]), int(m[2])
            local = [float(m[j]) for j in (3, 4, 5)]
            radius = float(m[6])
            if not all(math.isfinite(v) for v in local + [radius]) or radius <= 0:
                raise ValueError('Invalid quest area in ' + path)
            result[component].append({'id': f'{path}#{i}', 'source': path, 'component_id': component,
                'client_quest_id': qtype, 'zone_key': zone, 'point': world_point(local, [0, 0, 0], origins[zone]),
                'radius': radius, 'confidence': 'exact', 'meaning': 'Client quest marker circle; no live eligibility check'})
    return result, sources


# Match the act type before joining its detail ID: IDs are reused across tables.
DETAILS = {
    'QuestActConAcceptNpc': 'quest_act_con_accept_npcs',
    'QuestActConReportNpc': 'quest_act_con_report_npcs',
    'QuestActObjMonsterHunt': 'quest_act_obj_monster_hunts',
    'QuestActObjMonsterGroupHunt': 'quest_act_obj_monster_group_hunts',
    'QuestActObjTalk': 'quest_act_obj_talks',
    'QuestActObjTalkNpcGroup': 'quest_act_obj_talk_npc_groups',
    'QuestActObjSphere': 'quest_act_obj_spheres',
    'QuestActObjInteraction': 'quest_act_obj_interactions',
    'QuestActObjItemGather': 'quest_act_obj_item_gathers',
    'QuestActObjCompleteQuest': 'quest_act_obj_complete_quests',
}

# Keep details of objectives without a location too; absence on the map is explicit.
for _type in ('AbilityLevel', 'Aggro', 'Cinema', 'Condition', 'Craft', 'Distance',
              'DoodadPhaseCheck', 'EffectFire', 'ExpressFire', 'ItemGroupGather',
              'ItemGroupUse', 'ItemUse', 'Level', 'MateLevel', 'SendMail',
              'ZoneKill', 'ZoneMonsterHunt', 'ZoneNpcTalk', 'ZoneQuestComplete'):
    _snake = re.sub(r'(?<!^)(?=[A-Z])', '_', _type).lower()
    DETAILS['QuestActObj' + _type] = 'quest_act_obj_' + _snake + 's'


def assemble(db, geography, areas, doodad_spawns):
    db.row_factory = sqlite3.Row
    names = {(r['tbl_name'], r['tbl_column_name'], r['idx']): r['en_us'] for r in db.execute(
        "SELECT tbl_name,tbl_column_name,idx,en_us FROM localized_texts WHERE tbl_name IN ('quest_contexts','quest_act_obj_aliases','npcs','doodads') AND en_us IS NOT NULL AND en_us != ''")}
    quests = {r['id']: {'id': r['id'], 'name': names.get(('quest_contexts', 'name', r['id']), r['name']),
        'level': r['LEVEL'], 'repeatable': truth(r['REPEATABLE']), 'zone_id': r['zone_id'],
        'givers': [], 'reporters': [], 'objectives': [], 'start_requirements': [], 'followups': []}
        for r in db.execute('SELECT id,name,LEVEL,REPEATABLE,zone_id FROM quest_contexts')}
    components = rows(db, 'quest_components')
    details = {t: rows(db, table) for t, table in DETAILS.items()}
    placements, npc_index, doodad_index = {}, defaultdict(list), defaultdict(list)
    for f in geography['features']:
        if f['kind'] != 'npc':
            continue
        placements[f['id']] = {'id': f['id'], 'kind': 'npc', 'template_id': f['npc_template_id'],
            'name': f['name'], 'point': f['points'][0], 'source': f['source'], 'confidence': 'exact'}
        npc_index[f['npc_template_id']].append(f['id'])
    for i, f in enumerate(doodad_spawns):
        point = spawn_point(f.get('Position', {}))
        if point is None:
            raise ValueError('Invalid configured doodad position')
        key = f'AAEmu.Game/Data/Worlds/main_world/doodad_spawns.json#{i}'
        tid = f['UnitId']
        placements[key] = {'id': key, 'kind': 'doodad', 'template_id': tid,
            'name': names.get(('doodads', 'name', tid), f.get('Title') or f'Doodad {tid}'),
            'point': point, 'source': key.split('#')[0], 'confidence': 'exact'}
        doodad_index[tid].append(key)
    monster_groups, talk_groups = defaultdict(list), defaultdict(list)
    for r in db.execute('SELECT quest_monster_group_id,npc_id FROM quest_monster_npcs'):
        monster_groups[r[0]].append(r[1])
    for r in db.execute('SELECT npc_group_id,npc_id FROM npc_group_members'):
        talk_groups[r[0]].append(r[1])
    giver_index = defaultdict(set)
    for a in db.execute('SELECT * FROM quest_acts ORDER BY id'):
        c = components.get(a['quest_component_id'])
        if not c or c['quest_context_id'] not in quests:
            continue
        q = quests[c['quest_context_id']]
        kind, typ = c['component_kind_id'], a['act_detail_type']
        role = 'givers' if kind == 2 and typ == 'QuestActConAcceptNpc' else (
            'reporters' if kind == 6 and typ == 'QuestActConReportNpc' else None)
        if role is None and not typ.startswith('QuestActObj'):
            continue
        d = details.get(typ, {}).get(a['act_detail_id'], {})
        item = {'act_id': a['id'], 'component_id': c['id'], 'component_kind': kind,
            'type': typ, 'detail_id': a['act_detail_id'], 'details': d,
            'placement_ids': [], 'areas': [], 'target_npc_ids': [], 'notes': []}
        if truth(d.get('use_alias')):
            item['display_alias'] = names.get(('quest_act_obj_aliases', 'name', d.get('quest_act_obj_alias_id')))
        npc_ids = []
        if role or typ in ('QuestActObjMonsterHunt', 'QuestActObjTalk'):
            npc_ids = [d.get('npc_id', 0)]
        elif typ == 'QuestActObjMonsterGroupHunt':
            npc_ids = monster_groups[d.get('quest_monster_group_id')]
        elif typ == 'QuestActObjTalkNpcGroup':
            npc_ids = talk_groups[d.get('npc_group_id')]
        item['target_npc_ids'] = sorted(set(n for n in npc_ids if n))
        item['placement_ids'] = sorted({p for n in item['target_npc_ids'] for p in npc_index[n]})
        if typ == 'QuestActObjInteraction':
            item['placement_ids'] = list(doodad_index[d.get('doodad_id')])
            item['notes'].append('Configured Doodad; required phase and interaction still need server validation.')
        if typ == 'QuestActObjSphere' and d.get('npc_id', 0):
            item['notes'].append('NPC-relative trigger; no fixed objective circle claimed.')
        else:
            # The server binds .g geometry by component, not stale qtype/quest_id.
            item['areas'] = areas.get(c['id'], [])
            if item['areas'] and typ != 'QuestActObjSphere':
                item['notes'].append('Area is the client component marker, not the boundary of every valid target.')
        if typ == 'QuestActObjItemGather':
            item['notes'].append('Item acquisition sources are not resolved in this atlas; no guessed drop location.')
        if typ == 'QuestActObjCompleteQuest':
            item['notes'].append('Complete quest ' + str(d.get('quest_id')) + '; not a travel objective.')
        if not item['placement_ids'] and not item['areas']:
            item['notes'].append('No world location mapped from the configured main-world sources.')
        q[role or 'objectives'].append(item)
        if role == 'givers':
            for n in item['target_npc_ids']:
                giver_index[n].add(q['id'])
    # Preserve AND/OR and other requirements; these are references, not live quest availability.
    for r in db.execute("SELECT * FROM unit_reqs WHERE owner_type='QuestComponent' ORDER BY id"):
        c = components.get(r['owner_id'])
        if not c or c['component_kind_id'] != 2 or c['quest_context_id'] not in quests:
            continue
        q = quests[c['quest_context_id']]
        req = dict(r, component_id=c['id'], alternatives=truth(c['or_unit_reqs']))
        q['start_requirements'].append(req)
        if r['kind_id'] == 31 and r['value1'] in quests:
            quests[r['value1']]['followups'].append({'quest_id': q['id'], 'requirement_id': r['id'],
                'alternatives': req['alternatives'], 'meaning': 'Start requires completed quest, subject to other requirements'})
    totals = Counter()
    for q in quests.values():
        for o in q['objectives']:
            totals['objectives'] += 1
            totals['objectives_with_locations'] += bool(o['placement_ids'] or o['areas'])
    used = {p for q in quests.values() for key in ('givers', 'reporters', 'objectives') for o in q[key] for p in o['placement_ids']}
    return {'schema_version': 1, 'world_id': 0, 'database_md5': DB_MD5, 'client_version': '1.2 r208022',
        'quests': {str(k): v for k, v in quests.items()},
        'giver_quests': {str(k): sorted(v) for k, v in giver_index.items()},
        'placements': {k: placements[k] for k in sorted(used)},
        'coverage': dict(totals, quests=len(quests), giver_templates=len(giver_index),
            giver_placements=sum(len(npc_index[k]) for k in giver_index),
            quest_area_records=sum(len(v) for v in areas.values())),
        'limits': ['Configured main_world placements, not live spawns or quest eligibility.',
            'Client marker circles mapped by component; raw client quest IDs may be stale.',
            'Item acquisition, dynamic quest spawns and other worlds are not fully located.',
            'Quest links are completed-quest requirements, not assumed linear questlines.']}


def build(repo, database, geography_path, extracted):
    if hashlib.md5(database.read_bytes()).hexdigest() != DB_MD5:
        raise ValueError('Reference database differs from the recorded ArcheAge 1.2 baseline')
    geography = json.loads(geography_path.read_text())
    if geography.get('world_id') != 0 or geography.get('database_md5') != DB_MD5:
        raise ValueError('Geography does not match the canonical main-world database')
    areas, sources = parse_areas(extracted)
    spawn_path = repo / 'AAEmu.Game/Data/Worlds/main_world/doodad_spawns.json'
    raw = spawn_path.read_bytes()
    with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True) as db:
        result = assemble(db, geography, areas, lenient_json(raw.decode('utf-8-sig')))
    result['sources'] = sources + [{'path': str(geography_path.relative_to(repo)),
        'sha256': hashlib.sha256(geography_path.read_bytes()).hexdigest()},
        {'path': str(spawn_path.relative_to(repo)), 'sha256': hashlib.sha256(raw).hexdigest()}]
    return result


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--repo-root', type=Path, default=Path.cwd())
    p.add_argument('--quest-areas', type=Path, required=True)
    p.add_argument('--database', type=Path)
    args = p.parse_args()
    repo = args.repo_root.resolve()
    output = repo / '.client_files/dashboard-map/quests.json'
    data = build(repo, args.database or repo / 'AAEmu.Game/Data/compact.sqlite3',
                 repo / '.client_files/dashboard-map/geography.json', args.quest_areas.resolve())
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(data, separators=(',', ':'), ensure_ascii=False))
    print(json.dumps(data['coverage'], indent=2))
    print('Saved', output, output.stat().st_size, 'bytes; reference files unchanged.')

import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from prepare_quest_atlas import DETAILS, assemble, parse_areas, truth


class QuestAtlasTests(unittest.TestCase):
    def fixture(self):
        db=sqlite3.connect(':memory:')
        db.executescript('''
        CREATE TABLE localized_texts(tbl_name TEXT,tbl_column_name TEXT,idx INT,en_us TEXT);
        CREATE TABLE quest_contexts(id INT,name TEXT,LEVEL INT,REPEATABLE TEXT,zone_id INT);
        CREATE TABLE quest_components(id INT,quest_context_id INT,component_kind_id INT,next_component INT,or_unit_reqs TEXT);
        CREATE TABLE quest_acts(id INT,quest_component_id INT,act_detail_id INT,act_detail_type TEXT);
        CREATE TABLE quest_monster_npcs(quest_monster_group_id INT,npc_id INT);
        CREATE TABLE npc_group_members(npc_group_id INT,npc_id INT);
        CREATE TABLE unit_reqs(id INT,owner_id INT,owner_type TEXT,kind_id INT,value1 INT,value2 INT);
        INSERT INTO quest_contexts VALUES(10,'First',1,'f',1),(20,'Second',2,'t',1);
        INSERT INTO quest_components VALUES(100,10,2,101,'f'),(101,10,4,102,'f'),(200,20,2,201,'t');
        ''')
        for table in set(DETAILS.values()):
            db.execute('CREATE TABLE '+table+'(id INT,npc_id INT,count INT,use_alias TEXT,quest_act_obj_alias_id INT,doodad_id INT,quest_monster_group_id INT,npc_group_id INT)')
        geography={'features':[{'id':'npc#0','kind':'npc','npc_template_id':77,'name':'Tester','points':[[1,2,None]],'source':'npc.json'}]}
        return db,geography

    def test_wrong_act_type_does_not_bind_reused_detail_id(self):
        db,g=self.fixture()
        db.executescript("INSERT INTO quest_act_con_accept_npcs(id,npc_id) VALUES(1,77);INSERT INTO quest_acts VALUES(1,100,1,'QuestActConAcceptItem');")
        data=assemble(db,g,{},[])
        self.assertEqual(data['quests']['10']['givers'],[])
        self.assertEqual(data['giver_quests'],{})

    def test_preserves_all_objectives_and_unknown_altitude(self):
        db,g=self.fixture()
        db.executescript("INSERT INTO quest_act_obj_monster_hunts(id,npc_id,count) VALUES(1,77,3); INSERT INTO quest_acts VALUES(1,101,1,'QuestActObjMonsterHunt'),(2,101,999,'QuestActObjCraft');")
        data=assemble(db,g,{},[])
        self.assertEqual(len(data['quests']['10']['objectives']),2)
        self.assertEqual(data['coverage']['objectives_with_locations'],1)
        self.assertIsNone(data['placements']['npc#0']['point'][2])
        self.assertIn('No world location',data['quests']['10']['objectives'][1]['notes'][-1])

    def test_alias_label_does_not_override_server_npc_target(self):
        db,g=self.fixture()
        db.executescript("INSERT INTO quest_act_obj_monster_hunts(id,npc_id,use_alias,quest_act_obj_alias_id) VALUES(1,77,'t',9); INSERT INTO quest_acts VALUES(1,101,1,'QuestActObjMonsterHunt'); INSERT INTO localized_texts VALUES('quest_act_obj_aliases','name',9,'Enemy');")
        o=assemble(db,g,{},[])['quests']['10']['objectives'][0]
        self.assertEqual(o['display_alias'],'Enemy')
        self.assertEqual(o['target_npc_ids'],[77])

    def test_component_link_does_not_invent_quest_chain(self):
        db,g=self.fixture()
        data=assemble(db,g,{},[])
        self.assertEqual(data['quests']['10']['followups'],[])
        db.execute("INSERT INTO unit_reqs VALUES(1,200,'QuestComponent',31,10,0)")
        data=assemble(db,g,{},[])
        self.assertEqual(data['quests']['10']['followups'][0]['quest_id'],20)
        self.assertTrue(data['quests']['10']['followups'][0]['alternatives'])

    def test_area_matches_component_despite_stale_client_quest_id(self):
        db,g=self.fixture()
        db.executescript("INSERT INTO quest_act_obj_spheres(id,npc_id) VALUES(1,0); INSERT INTO quest_acts VALUES(1,101,1,'QuestActObjSphere');")
        a={'component_id':101,'client_quest_id':999,'point':[1,2,3],'radius':10}
        o=assemble(db,g,{101:[a]},[])['quests']['10']['objectives'][0]
        self.assertEqual(o['areas'],[a])
        db.execute('UPDATE quest_act_obj_spheres SET npc_id=77')
        o=assemble(db,g,{101:[a]},[])['quests']['10']['objectives'][0]
        self.assertEqual(o['areas'],[])
        self.assertIn('NPC-relative',o['notes'][0])

    def test_sphere_origin_transform_and_malformed_input(self):
        with tempfile.TemporaryDirectory() as root:
            root=Path(root);folder=root/'game/worlds/main_world/level_design/zone/129/client';folder.mkdir(parents=True)
            (root/'game/worlds/main_world/world.xml').write_text('<World><Zone id="129" originX="2" originY="3"/></World>')
            path='game/worlds/main_world/level_design/zone/129/client/quest_sign_sphere.g'
            (root/path).write_text('area\n qtype 999\n ctype 101\n pos ( x 10, y 20, z 30 )\n radius 15\n')
            (root/'inventory.json').write_text(json.dumps({'entries':[{'path':path}]}))
            areas,_=parse_areas(root)
            self.assertEqual(areas[101][0]['point'],[2058,3092,30])
            (root/path).write_text('area\n broken\n')
            with self.assertRaises(ValueError): parse_areas(root)

    def test_sqlite_false_is_not_python_true(self):
        self.assertFalse(truth('f'))
        self.assertFalse(truth('0'))
        self.assertTrue(truth('t'))

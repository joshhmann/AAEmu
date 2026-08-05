# Quest Reachability Audit — do the allowlisted / flagged quests exist in 1.2 world data?

*Generated: 2026-08-05 07:39Z — reachability audit card t_5600bbac (Tai). No code changes.*

## TL;DR — Josh's question answered

**The 132 allowlisted quest ids are almost all REAL rows in the 1.2 data — they were NOT 'just left there' by accident — but they are all structurally dead (shells), and 121 of 132 have a quest_contexts row. 11 are ghosts (no context row) that still carry full quest bodies. A handful are REFERENCED by live world data, which makes them data holes with blast radius, not inert rows:**

- **132 allowlisted ids**: 121 have a `quest_contexts` row (name present), 11 do not (cat-34 chain ghosts 1954–1958/1961/2140–2143/2146 — but every one has a full component body + acts). Zero of the 132 is accepted/rewarded by any NPC, item, doodad, schedule, mail, task or effect — the only references are: sphere triggers (2046 ×6, 3750–3757 ×1 each, 3748 ×1, 1728 ×1), 2 real quests referencing the chain ghosts (1959→1958, 2144→2143 via unit_reqs; 1960/2145 accept-act chains), and intra-chain accept acts.

## 1. The 132 allowlisted quest ids — existence + references

Verdict legend: **REAL-DEAD** = exists in 1.2 data (context row) but structurally inert; **GHOST-DEAD** = no context row at all; **REFERENCED** = at least one other entity points at it (blast radius); **PLAYABLE** = context + Start/Progress/Ready/Reward + wiring (none of the allowlist qualifies).


**dead cat-34 chain — GHOST contexts (no quest_contexts row), full bodies present**

| quest | name (quest_contexts) | body | referenced by (table:row, entity) | verdict |
|---|---|---|---|---|
| 1954 | — NO CONTEXT ROW — | 4 comps [Progress,Reward,Start] 8 acts | quest_act_con_accept_components row 54 | GHOST-DEAD / REFERENCED |
| 1955 | — NO CONTEXT ROW — | 4 comps [Progress,Reward,Start] 9 acts | unit_reqs.CompleteQuestContext row 19197 owner=QuestComponent:9780 (quest 1956 None); quest_act_con_accept_components row 56; quest_act_con_accept_components row 69; from quests [1954] | GHOST-DEAD / REFERENCED |
| 1956 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 19 acts | sphere_accept_quest_quests row 3 accept_sphere=2; unit_reqs.CompleteQuestContext row 19198 owner=QuestComponent:9783 (quest 1957 None); quest_act_con_accept_components row 58; quest_act_con_accept_components row 70; from quests [1955] | GHOST-DEAD / REFERENCED |
| 1957 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 19 acts | unit_reqs.CompleteQuestContext row 19205 owner=QuestComponent:9786 (quest 1958 None); quest_act_con_accept_components row 60; quest_act_con_accept_components row 71; from quests [1956] | GHOST-DEAD / REFERENCED |
| 1958 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 18 acts | unit_reqs.CompleteQuestContext row 19201 owner=QuestComponent:9789 (quest 1959 장작을 모아보세요); quest_act_con_accept_components row 61; quest_act_con_accept_components row 72; from quests [1957] | GHOST-DEAD / REFERENCED |
| 1961 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 8 acts | unit_reqs.CompleteQuestContext row 19206 owner=QuestComponent:9910 (quest 2140 None); quest_act_con_accept_components row 75; from quests [1960]; quest_act_con_accept_components row 76 | GHOST-DEAD / REFERENCED |
| 2140 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 7 acts | unit_reqs.CompleteQuestContext row 19207 owner=QuestComponent:9913 (quest 2141 None); quest_act_con_accept_components row 77; from quests [1961]; quest_act_con_accept_components row 78 | GHOST-DEAD / REFERENCED |
| 2141 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 8 acts | unit_reqs.CompleteQuestContext row 19208 owner=QuestComponent:9916 (quest 2142 None); quest_act_con_accept_components row 79; from quests [2140]; quest_act_con_accept_components row 80 | GHOST-DEAD / REFERENCED |
| 2142 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 7 acts | unit_reqs.CompleteQuestContext row 19209 owner=QuestComponent:9919 (quest 2143 None); quest_act_con_accept_components row 81; from quests [2141]; quest_act_con_accept_components row 82 | GHOST-DEAD / REFERENCED |
| 2143 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 7 acts | unit_reqs.CompleteQuestContext row 19210 owner=QuestComponent:9922 (quest 2144 거미줄을 모아보세요); quest_act_con_accept_components row 83; from quests [2142]; quest_act_con_accept_components row 84 | GHOST-DEAD / REFERENCED |
| 2146 | — NO CONTEXT ROW — | 3 comps [Progress,Reward,Start] 7 acts | quest_act_con_accept_components row 89; from quests [2145]; quest_act_con_accept_components row 90 | GHOST-DEAD / REFERENCED |


**dangling-accept live quests (context + body, accept act → ghost)**

| quest | name (quest_contexts) | body | referenced by (table:row, entity) | verdict |
|---|---|---|---|---|
| 1960 | 여행자의 조잡한 공구상자를 설치해보세요 | 3 comps [Progress,Reward,Start] 7 acts | unit_reqs.CompleteQuestContext row 19203 owner=QuestComponent:9873 (quest 1961 None); quest_act_con_accept_components row 66; quest_act_con_accept_components row 74; from quests [1959] | REAL-DEAD / REFERENCED |
| 2145 | 다용도 옷감을 만들어보세요 | 3 comps [Progress,Reward,Start] 7 acts | unit_reqs.CompleteQuestContext row 19212 owner=QuestComponent:9928 (quest 2146 None); quest_act_con_accept_components row 87; from quests [2144]; quest_act_con_accept_components row 88 | REAL-DEAD / REFERENCED |


**tutorial shells — context, Reward-only body, no Start**

| quest | name (quest_contexts) | body | referenced by (table:row, entity) | verdict |
|---|---|---|---|---|
| 1533 | 튜토리얼_아이템_획득 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1535 | 튜토리얼_무기보상 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1536 | 튜토리얼_말_획득 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1537 | 튜토리얼_말_인벤토리 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1538 | 튜토리얼_무기보상_인벤토리 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1539 | 튜토리얼_말_소환 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1540 | 튜토리얼_루팅 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1541 | 튜토리얼_이동데칼 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1542 | 1. 이동 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1543 | 2. NPC | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1544 | 3. 퀘스트 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1545 | 4. 루팅 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1546 | 5. 아이템획득 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1547 | 6. 다이나믹액션바(NPC) | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1548 | 8. 다이나믹액션바(두다드) | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1549 | 9. 아이템사용퀘스트 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1551 | 11. 귀환석바인딩 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1552 | 12. 말 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1553 | 13. 무기보상 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1554 | 14. 메인퀘저널 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1640 | 10. 죽음 | 1 comps [Reward] 2 acts | — | REAL-DEAD |
| 1830 | 미사용 | 1 comps [Reward] 0 acts | — | REAL-DEAD |
| 1831 | 미사용 | 3 comps [Progress,Ready,Reward] 0 acts | — | REAL-DEAD |


**reserve / dummy / cutscene shells — context, ZERO components**

| quest | name (quest_contexts) | body | referenced by (table:row, entity) | verdict |
|---|---|---|---|---|
| 315 | (삭제 금지) 스킬 연결용 퀘스트 | 0 comps | — | REAL-DEAD |
| 1391 | 마을을 지켜라 | 0 comps | — | REAL-DEAD |
| 1576 | dummy | 0 comps | — | REAL-DEAD |
| 1728 | 두다드 스킬 사용전용(삭제하지마시오) | 0 comps | sphere_quests row 567 trigger=1 | REAL-DEAD / REFERENCED |
| 2046 | Unit Req Dummy | 0 comps | sphere_quests row 1096 trigger=1; sphere_quests row 1172 trigger=1; sphere_quests row 595 trigger=1; sphere_quests row 721 trigger=1; sphere_quests row 770 trigger=1; sphere_quests row 780 trigger=1 | REAL-DEAD / REFERENCED |
| 2148 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2149 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2150 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2151 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2152 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2153 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2154 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2155 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2156 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2157 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2158 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2159 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2160 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2161 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2162 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2163 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2164 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2165 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2166 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2167 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2168 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2169 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2170 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2171 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2172 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2173 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2174 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2175 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2176 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2177 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2178 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2179 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2180 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2181 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2182 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2183 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2184 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2185 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2186 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2187 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2188 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2189 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2190 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2191 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2192 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2193 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2194 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2195 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2196 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2197 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2198 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2199 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2200 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2201 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2202 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2203 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2204 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2205 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2206 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2207 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2208 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2209 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2210 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2211 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2212 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2213 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2214 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2215 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2216 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2217 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2218 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2219 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2220 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2221 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2222 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2223 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2224 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2225 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2226 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2227 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2228 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 2229 | 하다보니(reserve) | 0 comps | — | REAL-DEAD |
| 3748 | 수상한 농장 | 0 comps | sphere_quests row 725 trigger=4 sphere=Q176_미심쩍은목소리_진입 | REAL-DEAD / REFERENCED |
| 3750 | 하디르의 농장 인던연출3 | 0 comps | sphere_quests row 727 trigger=3 | REAL-DEAD / REFERENCED |
| 3751 | 하디르의 농장 인던연출4 | 0 comps | sphere_quests row 728 trigger=3 | REAL-DEAD / REFERENCED |
| 3752 | 하디르의 농장 인던연출5 | 0 comps | sphere_quests row 729 trigger=3 | REAL-DEAD / REFERENCED |
| 3753 | 하디르의 농장 인던연출6 | 0 comps | sphere_quests row 730 trigger=3 | REAL-DEAD / REFERENCED |
| 3754 | 하디르의 농장 인던연출7 | 0 comps | sphere_quests row 731 trigger=3 | REAL-DEAD / REFERENCED |
| 3755 | 하디르의 농장 인던연출8 | 0 comps | sphere_quests row 732 trigger=3 | REAL-DEAD / REFERENCED |
| 3756 | 하디르의 농장 인던연출9 | 0 comps | sphere_quests row 733 trigger=3 | REAL-DEAD / REFERENCED |
| 3757 | 하디르의 농장 인던연출10 | 0 comps | sphere_quests row 734 trigger=3 | REAL-DEAD / REFERENCED |


**reserve block 2148–2229 — context, ZERO components**

| quest | name (quest_contexts) | body | referenced by (table:row, entity) | verdict |
|---|---|---|---|---|


## 2. The 28 orphan contexts (quest_components rows referencing a missing quest_context)

All 28 have NO quest_contexts row — the quest engine never loads them. 'Blocking side' = whether the missing id is referenced by anything else (real quest, world entity), i.e. whether deleting/fixing it has blast radius.

| quest | comps | referenced by (table:row, entity) | blocking side | verdict |
|---|---|---|---|---|
| 745 | 10 | doodad_func_require_quests row 476 wi=19; doodad_func_require_quests row 477 wi=19; items.loot_quest_id item 5077(도망자의 찢어진 옷); items.loot_quest_id item 5078(버려진 짐 가방); unit_reqs.ProgressQuestContext row 16000 owner=Skill:12913 (quest 2951 윈란드의 연애편지); unit_reqs.ProgressQuestContext row 16064 owner=Skill:12912 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1421 | 8 | sphere_quests row 418 trigger=1 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1697 | 5 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 1954 | 4 | quest_act_con_accept_components row 54 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1955 | 4 | unit_reqs.CompleteQuestContext row 19197 owner=QuestComponent:9780 (quest 1956 None); quest_act_con_accept_components row 56; quest_act_con_accept_components row 69; from quests [1954] | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1956 | 3 | sphere_accept_quest_quests row 3 accept_sphere=2; unit_reqs.CompleteQuestContext row 19198 owner=QuestComponent:9783 (quest 1957 None); quest_act_con_accept_components row 58; quest_act_con_accept_components row 70; from quests [1955] | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1957 | 3 | unit_reqs.CompleteQuestContext row 19205 owner=QuestComponent:9786 (quest 1958 None); quest_act_con_accept_components row 60; quest_act_con_accept_components row 71; from quests [1956] | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1958 | 3 | unit_reqs.CompleteQuestContext row 19201 owner=QuestComponent:9789 (quest 1959 장작을 모아보세요); quest_act_con_accept_components row 61; quest_act_con_accept_components row 72; from quests [1957] | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 1961 | 3 | unit_reqs.CompleteQuestContext row 19206 owner=QuestComponent:9910 (quest 2140 None); quest_act_con_accept_components row 75; from quests [1960]; quest_act_con_accept_components row 76 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 2140 | 3 | unit_reqs.CompleteQuestContext row 19207 owner=QuestComponent:9913 (quest 2141 None); quest_act_con_accept_components row 77; from quests [1961]; quest_act_con_accept_components row 78 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 2141 | 3 | unit_reqs.CompleteQuestContext row 19208 owner=QuestComponent:9916 (quest 2142 None); quest_act_con_accept_components row 79; from quests [2140]; quest_act_con_accept_components row 80 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 2142 | 3 | unit_reqs.CompleteQuestContext row 19209 owner=QuestComponent:9919 (quest 2143 None); quest_act_con_accept_components row 81; from quests [2141]; quest_act_con_accept_components row 82 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 2143 | 3 | unit_reqs.CompleteQuestContext row 19210 owner=QuestComponent:9922 (quest 2144 거미줄을 모아보세요); quest_act_con_accept_components row 83; from quests [2142]; quest_act_con_accept_components row 84 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 2146 | 3 | quest_act_con_accept_components row 89; from quests [2145]; quest_act_con_accept_components row 90 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 3233 | 4 | unit_reqs.CompleteQuestContext row 27692 owner=QuestComponent:13788 (quest 3234 None) | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 3234 | 4 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 3235 | 4 | unit_reqs.CompleteQuestContext row 27695 owner=QuestComponent:13796 (quest 3236 None) | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 3236 | 4 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 5133 | 3 | item_accept_quests row 177 item=26756(수습 곡예사의 증표); unit_reqs.CompleteQuestContext row 39656 owner=AiEvent:1127 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 5765 | 3 | unit_reqs.ProgressQuestContext row 44369 owner=AiEvent:1973; unit_reqs.ProgressQuestContext row 44370 owner=AiEvent:1974 | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 6014 | 4 | unit_reqs.ExceptCompleteQuestContext row 44931 owner=QuestComponent:25900 (quest 6014 None) | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 6015 | 4 | unit_reqs.ExceptCompleteQuestContext row 44930 owner=QuestComponent:25900 (quest 6014 None) | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 6019 | 3 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 6230 | 5 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 6350 | 3 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 6371 | 10 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |
| 6420 | 4 | item_accept_quests row 547 item=34820(?) | REFERENCED (see refs) | GHOST / REFERENCED — live data hole |
| 6635 | 3 | — | NOT referenced by anything | GHOST — unreferenced, pure dead row |

## 3. The 3 next_component quests (330 / 776 / 777)

next_component is a deprecated 1.0 field the engine never reads for progression. The verifier flags it when the value is not a component of the same quest. Check: the values point at QUEST ids, and all three targets EXIST as quest_contexts rows:

| quest | name | comp | next_component | target exists? | target name |
|---|---|---|---|---|---|
| 330 | 나를 찾는 사람 | 1520 | 3543 | YES — real quest | 파괴신에 맞닿은 발길 |
| 776 | 해적과 오크 | 3480 | 4370 | YES — real quest | 소리 없는 전쟁의 승리를 위해 |
| 777 | 오크의 그늘 아래 | 3488 | 3487 | YES — real quest | 메인스토리_dummy4 |

All three quests themselves are real (context + components). The finding is cosmetic: the field references quest ids, not component ids.

## 4. NEW LAYER — unit_reqs prerequisite references to missing contexts (never checked by the verifier)

`unit_reqs` rows of the quest-prerequisite kinds (31/32/33/36/37/72/73 → Complete/Progress/Ready/ExceptComplete/PreComplete/ExceptProgress/ExceptReady QuestContext) carry `value1` = quest id. Counts of rows referencing a context id with NO quest_contexts row, by owner type:

| owner_type | rows | distinct missing contexts |
|---|---|---|
| QuestComponent | 24 | 20 |
| Skill | 67 | 57 |
| Sphere | 31 | 25 |
| AiEvent | 29 | 21 |

**QuestComponent-owned (the ones that genuinely block real quests):**

| owner quest | name | unit_req row | kind | missing context |
|---|---|---|---|---|
| 1883 | (구 불볕황야)불길한 결말 | 16832 | CompleteQuestContext | 1882 |
| 1922 | (구 불볕황야)장로의 지혜 | 16853 | CompleteQuestContext | 1921 |
| 1836 | 미사용 | 18500 | CompleteQuestContext | 1832 |
| 2054 | (구 불볕황야)가려움의 원인 | 18576 | CompleteQuestContext | 2053 |
| 2056 | (구 불볕황야)보답의 마음 | 18578 | CompleteQuestContext | 1848 |
| 1956 | ORPHAN | 19197 | CompleteQuestContext | 1955 |
| 1957 | ORPHAN | 19198 | CompleteQuestContext | 1956 |
| 1959 | 장작을 모아보세요 | 19201 | CompleteQuestContext | 1958 |
| 1958 | ORPHAN | 19205 | CompleteQuestContext | 1957 |
| 2140 | ORPHAN | 19206 | CompleteQuestContext | 1961 |
| 2141 | ORPHAN | 19207 | CompleteQuestContext | 2140 |
| 2142 | ORPHAN | 19208 | CompleteQuestContext | 2141 |
| 2143 | ORPHAN | 19209 | CompleteQuestContext | 2142 |
| 2144 | 거미줄을 모아보세요 | 19210 | CompleteQuestContext | 2143 |
| 38 | 엘프의 셈법 | 20455 | ExceptCompleteQuestContext | 14 |
| 3234 | ORPHAN | 27692 | CompleteQuestContext | 3233 |
| 3236 | ORPHAN | 27695 | CompleteQuestContext | 3235 |
| 6014 | ORPHAN | 44930 | ExceptCompleteQuestContext | 6015 |
| 6014 | ORPHAN | 44931 | ExceptCompleteQuestContext | 6014 |
| 6587 | 강인한 정신력을 얻기 위해 | 46598 | CompleteQuestContext | 6586 |
| 6589 | 들녘뿌리 퇴치 1단계 | 46603 | CompleteQuestContext | 6586 |
| 6592 | 들녘을 떠도는 에페리움 망령 전사 | 46609 | CompleteQuestContext | 6586 |
| 6594 | 이끼 슬라임 퇴치 1단계 | 46613 | CompleteQuestContext | 6586 |
| 6597 | 바다에서 떠오르는 시체 1단계 | 46619 | CompleteQuestContext | 6586 |

**Skill-owned (gate skill usability, e.g. the 745 case):** skill 12913 '가방 줍기' and skill 12912 require quest 745 IN PROGRESS (unit_reqs 16000/16064). Quest 745 has no context row → the skills can never be usable. **Caveat on the '745→2951' example from the card:** unit_req 16000's owner_id 12913 is a SKILL; the id 12913 collides with quest 2951's Supply component id (12913) — quest 2951's own components/acts do NOT reference 745. The real 745 edges are: items 5077/5078 (`loot_quest_id`), doodad_func_require_quests rows 476/477 (wi 19), and the two skill reqs. So 'quest 745 blocks quest 2951' does NOT hold as a direct data edge; 745 blocks the two skills and two loot drops instead.

## 5. Surfaces with ZERO hits across all 163 audited ids (132+28+3)

- `npc_interaction_sets` + `npc_interactions` — **no quest ids live in this schema** (reverse-engineered: `npc_interactions.skill_id` are merchant/trainer skills, range 21335–25256, none a quest id; no set name mentions quests). NPC quest-giver wiring in compact.sqlite3 is only `quest_components.npc_id` (105 rows / 91 NPCs) + `npcs.engage_combat_give_quest_id` — neither references any audited id.
- `model_quest_cameras` — camera ids only; no quest-id column exists (quest→camera link not present in this DB).
- `accept_quest_effects` (14 rows), `doodad_func_quests` (272), `doodad_func_conditional_uses`, `game_schedule_quests` (96), `npcs.engage_combat_give_quest_id`, `quest_mail_sends`, `quest_task_quests` — zero hits for all 163 ids.
- `game_pak` — not present on this host (no client files); not needed: quest names/text live in `quest_contexts.name` (verified present for all 121 context-having allowlisted ids).

## 6. Bottom line

1. **Allowlist verdict: all 132 are dead shells — none is playable — but 121 genuinely exist in 1.2 data** (context + name) and 11 are ghosts with bodies. They were 'left there' on purpose or by 1.0-era leftovers: tutorial Reward-only shells (23), reserve/dummy/cutscene shells (96), and the dead cat-34 crafting chain (13, incl. 2 live-but-broken quests 1960/2145).
2. **Blast radius is small but real**: sphere triggers reference 2046/1728/3748/3750–3757 (Hadir cutscenes) and 1956 (sphere accept); items reference 745/5133/6420; real quests 1959/2144/1960/2145 are gated on chain ghosts 1958/2143/1961/2146.
3. **The '8 orphaned contexts that block real quests' claim**: the data shows 5 orphans referenced by real quests (745*, 1958, 1961, 2143, 2146 — *via skill reqs, not a direct quest edge) and 10 referenced by any live entity; 8 orphans (1697, 3234, 3236, 6019, 6230, 6350, 6371, 6635) have NO references at all. The card's count does not match any clean cut of the data — the full evidence is in §2.
4. **New finding the verifier misses**: 24 QuestComponent-owned unit_reqs reference 20 missing contexts — 13 real quests are gated on ghosts (1883←1882, 1922←1921, 1836←1832, 2054←2053, 2056←1848, 1959←1958, 2144←2143, 38←14, 6587/6589/6592/6594/6597←6586). Plus 67 Skill-owned / 31 Sphere-owned / 29 AiEvent-owned reqs on missing contexts. Candidate for a verifier finding (ACT_REF_MISSING_QUEST for unit_reqs).
5. **next_component quests 330/776/777: their targets are real quests** (3543 파괴신에 맞닿은 발길, 4370 소리 없는 전쟁의 승리를 위해, 3487 메인스토리_dummy4) — the ids exist; the field is just 1.0-semantics. No data hole.

## Appendix — repeatable queries

```sql
-- orphan contexts (28)
SELECT qc.quest_context_id, COUNT(*) FROM quest_components qc LEFT JOIN quest_contexts q ON q.id = qc.quest_context_id WHERE q.id IS NULL GROUP BY qc.quest_context_id;
-- unit_reqs quest-prerequisite kinds referencing missing contexts
SELECT id, owner_id, owner_type, kind_id, value1 FROM unit_reqs WHERE kind_id IN (31,32,33,36,37,72,73) AND value1 NOT IN (SELECT id FROM quest_contexts);
```

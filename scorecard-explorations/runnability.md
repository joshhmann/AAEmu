# Quest Runnability — M1-5 scenario harness census

Generated: 2026-08-05 02:28Z by QuestScenarioTierTests (M1-5b)

Verdict semantics: **PASS** = full lifecycle driven (start→progress→ready→reward→persist); **FAIL** = a stage assertion or engine exception (name the stage + reason); **SKIP** = not driven (broken refs / unsynthesizable shapes), reason in the detail column.

## Headline

- **T1 golden zone (Solzreed)**: 91 PASS / 6 FAIL / 0 SKIP
- **T2 families (kill-accept/guard/item-group)**: 21 PASS / 8 FAIL / 6 SKIP

## T1 — per-quest verdicts

| quest | name | family | verdict | detail |
|---|---|---|---|---|
| 1525 | 널린 희생자들 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 1650 | 누구 것일까? | golden-zone | Fail | START:Fail (expected step Progress, got Reward; expected status Progress, got Completed); PROGRESS:Fail (expected step Reward, got Drop; expected status Completed, got Dropped); REWARD:Pass; PERSIST:Pass |
| 1652 | 난폭한 선돌 수호자 퇴치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 1725 | 겁먹은 정찰대원 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2239 | 지붕 위로 날아간 닭 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 2245 | 피 묻은 손의 약탈 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2246 | 칼의 행방 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2248 | 피 묻은 손을 물리쳐라 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2249 | 트럼프의 부탁 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2251 | 허풍쟁이 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2255 | 금빛 표지 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2256 | 루키우스의 흔적 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2257 | 심상치 않은 시체들 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2258 | 피 묻은 손의 침입 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2259 | 엄중한 경계령 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2260 | 특명! 백월만 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2261 | 피 묻은 손이 쫓는 것 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2262 | 스콧을 찾아서 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2263 | 살인마에게 자비를 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2264 | 남겨진 실마리들 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2265 | 꽃의 처녀 플로라 | golden-zone | Pass | START:Pass; SUPPLY:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2266 | 범인을 찾아서 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2370 | 백월만의 피 묻은 손 처치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2374 | 불한당 벨포 일당 처치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2393 | 솔즈리드 나룻배 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 2400 | 중년 남성의 희망 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2404 | 누군가의 편지 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2413 | 피 묻은 손 돌격대장 처치! | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 250 | 솔즈리드 여우 처치 | golden-zone | Fail | START:Pass; PROGRESS:Pass; REWARD:Fail (KeyNotFoundException: The given key 'General' was not present in the dictionary.    at System.Collections.Generic.Dictionary`2.get_Item(TKey key)    at AAEmu.Game.Models.Game.Char.CharacterAbilities.AddActiveExp(Int32 exp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/CharacterAbilities.cs:line 55    at AAEmu.Game.Models.Game.Char.Character.AddExp(Int32 expDelta, Boolean shouldAddAbilityExp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/Character.cs:line 1455    at AAEmu.Game.Models.Game.Quests.Acts.QuestActSupplyExp.RunAct(Quest quest, QuestAct questAct, Int32 currentObjectiveCount) in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Acts/QuestActSupplyExp.cs:line 20    at AAEmu.Game.Models.Game.Quests.QuestAct.RunAct() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestAct.cs:line 50    at AAEmu.Game.Models.Game.Quests.QuestComponent.RunComponent() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestComponent.cs:line 65    at AAEmu.Game.Models.Game.Quests.QuestStep.RunComponents() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestStep.cs:line 88    at AAEmu.Game.Models.Game.Quests.Quest.RunCurrentStep() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:line 67    at AAEmu.UnitTests.Game.Quests.Scenario.QuestScenarioDriver.Run(QuestScenarioManifest manifest) in /root/aaemu-dev/AAEmu.UnitTests/Game/Quests/Scenario/QuestScenarioDriver.cs:line 628); PERSIST:Pass |
| 251 | 화난 멧돼지들 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 252 | 숲 되살리기 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 2531 | 시골에 도착한 예언자 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2532 | 낯선 소녀의 연락 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 254 | 엄마의 걱정 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 255 | 제니의 부탁 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 256 | 고집쟁이 제니 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2569 | 두들링 퇴치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 257 | 선돌 연구자의 행방 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2570 | 토끼 학살자 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2573 | 네손가락 도적단 퇴치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2575 | 폐허의 망령 퇴치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2576 | 대담한 도둑질 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 2578 | 평원 거미 퇴치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2579 | 쥐는 잡아야 제맛! | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 259 | 위대한 유산 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 260 | 정체 모를 빛 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 261 | 원혼 달래기 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 263 | 부러진 곡괭이 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 265 | 솔즈리언의 문을 위하여 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 266 | 양치기의 부탁 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 269 | 두들링을 두들겨라 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 270 | 좋은 생각 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 271 | 거대 토끼 처치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 273 | 르네의 반지 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2762 | 집 자랑 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 290 | 발릴리의 고민 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 291 | 브리짓 구출 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 293 | 요정의 정수 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 294 | 타락한 요정의 이름, 밴시 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 295 | 밴시 정화 | golden-zone | Fail | START:Pass; SUPPLY:Pass; PROGRESS:Fail (expected step Ready, got Progress; expected status Ready, got Progress); READY:Fail (expected step Reward, got Progress; expected status Completed, got Progress); REWARD:Fail (expected completed-quest flag set, found not completed); PERSIST:Pass |
| 298 | 릴리엇 구릉지로 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 299 | 네손가락 도적단의 위협 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 300 | 웃는 얼굴 만크스 처치 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 303 | 영혼을 가르는 무기 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 304 | 승천하지 못한 디켄트라 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 324 | 앨런의 도움 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 325 | 로나의 약 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 329 | 불곰을 조심해! | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 330 | 나를 찾는 사람 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 345 | 무엇에 쓰는 약인고 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 346 | 마엘와스가 남긴 것 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 347 | 오빠의 마음 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 350 | 일손 부족 | golden-zone | Fail | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Fail (WriteData -> ReadData round-trip changed quest state (byte mismatch)) |
| 3503 | 희생자를 줄일 기회 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 354 | 미안한 이야기 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 4292 | 망아지 운반 | golden-zone | Fail | START:Pass; SUPPLY:Pass; READY:Pass; REWARD:Pass; PERSIST:Fail (WriteData -> ReadData round-trip changed quest state (byte mismatch)) |
| 4294 | 망아지의 먹이 | golden-zone | Fail | START:Fail (expected step Progress, got Ready; expected status Progress, got Ready); PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 4295 | 여행의 동반자를 얻다! | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 4901 | 마일즈가 연구 중인 선돌 | golden-zone | Pass | START:Pass; REWARD:Pass; PERSIST:Pass |
| 4902 | 선돌 연구자의 집 방문 | golden-zone | Pass | START:Pass; REWARD:Pass; PERSIST:Pass |
| 4903 | 아이들의 비밀장소 | golden-zone | Pass | START:Pass; REWARD:Pass; PERSIST:Pass |
| 5059 | 배심원 자격, 놓치지 않을 거예요 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5095 | 초승달 왕좌의 도서관 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5096 | 실종된 책 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5097 | 장기연체자 로이스터 경 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5098 | 론반 공작의 제안 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5106 | 세상에서 가장 위대했던 도서관 이야기 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5107 | 보관될 수 없는 지식에 관하여 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5146 | 작은 시작 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5166 | 문제 많은 저택 | golden-zone | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5167 | 낯익은 얼굴 | golden-zone | Pass | START:Pass; READY:Pass; PERSIST:Pass |
| 5168 | 특사의 자격 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; PERSIST:Pass |
| 5267 | 이니스테르의 속사정 | golden-zone | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; PERSIST:Pass |
| 5719 | 비틀린 역사의 진실 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 6161 | 달콤한 꿈과 쌉싸름한 현실 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 6249 | 가라앉은 만의 공포 | golden-zone | Pass | START:Pass; PROGRESS:Pass; REWARD:Pass; PERSIST:Pass |
| 6280 | 고난을 부르는 발걸음 | golden-zone | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |

## T2 — per-quest verdicts

| quest | name | family | verdict | detail |
|---|---|---|---|---|
| 1033 | 기억과 쇠 골렘 | mixed-families | Fail | START:Pass; PROGRESS:Fail (expected step Ready, got Progress; expected status Ready, got Progress); READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1057 | 황금 실타래 벌판의 위협 몰아내기 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1079 | 마리아노플의 소매치기들 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1082 | 하피 둥지의 하피들 처치 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1089 | 마리아노플 정원의 습격자들 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1165 | 마른 들판의 날카로운 발톱을 가진 무리 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1176 | 기계 제작소의 골칫거리 해결 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1177 | 거미숲의 주인들 처치 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1178 | 오염의 주범들 처치 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1180 | 코볼트 동굴의 점박이 코볼트 처치 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1181 | 북 데비 강 상류의 야생동물 처치 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1185 | 마리아노플 정원을 위협하는 무리 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1186 | 벌거숭이 언덕의 파수꾼들 몰아내기 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1187 | 놀 습격 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1188 | 독을 품은 동물들 사냥 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1189 | 죽음의 기운 물리치기 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1190 | 황야의 무법자들과 대결 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1313 | 말동무 | mixed-families | Fail | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Fail (WriteData -> ReadData round-trip changed quest state (byte mismatch)) |
| 1421 |  | mixed-families | Skip | SKIP:Skip (orphaned context (no quest_contexts row)) |
| 182 | 황금 실타래 마을의 약탈자들 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 1897 | (구 불볕황야)사라진 가우타마(월드작업후 퀘스트 대상 배치 예정) | mixed-families | Fail | START:Pass; SUPPLY:Pass; PROGRESS:Fail (expected step Reward, got Progress; expected status Completed, got Progress); REWARD:Fail (expected completed-quest flag set, found not completed); PERSIST:Fail (WriteData -> ReadData round-trip changed quest state (byte mismatch)) |
| 1955 |  | mixed-families | Skip | SKIP:Skip (orphaned context (no quest_contexts row)) |
| 1957 |  | mixed-families | Skip | SKIP:Skip (orphaned context (no quest_contexts row)) |
| 1958 |  | mixed-families | Skip | SKIP:Skip (orphaned context (no quest_contexts row)) |
| 205 | 토리니 정원의 야생동물들 사냥 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 2140 |  | mixed-families | Skip | SKIP:Skip (orphaned context (no quest_contexts row)) |
| 3656 | 뜨거운 물이 좋아 | mixed-families | Fail | START:Pass; PROGRESS:Fail (expected step Ready, got Progress; expected status Ready, got Progress); READY:Pass; REWARD:Pass; PERSIST:Pass |
| 5489 | test_time | mixed-families | Fail | START:Pass; PROGRESS:Fail (expected step Ready, got Progress; expected status Ready, got Progress); READY:Fail (expected completed-quest flag set, found not completed); PERSIST:Pass |
| 5490 | 신기루 섬을 깨끗하게 | mixed-families | Pass | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 556 | 시차일드 부두로 찾아온 수상한 인어들 퇴치 | mixed-families | Pass | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |
| 6578 | 이이제이 | mixed-families | Fail | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Fail (KeyNotFoundException: The given key 'General' was not present in the dictionary.    at System.Collections.Generic.Dictionary`2.get_Item(TKey key)    at AAEmu.Game.Models.Game.Char.CharacterAbilities.AddActiveExp(Int32 exp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/CharacterAbilities.cs:line 55    at AAEmu.Game.Models.Game.Char.Character.AddExp(Int32 expDelta, Boolean shouldAddAbilityExp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/Character.cs:line 1455    at AAEmu.Game.Models.Game.Quests.Acts.QuestActSupplyExp.RunAct(Quest quest, QuestAct questAct, Int32 currentObjectiveCount) in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Acts/QuestActSupplyExp.cs:line 20    at AAEmu.Game.Models.Game.Quests.QuestAct.RunAct() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestAct.cs:line 50    at AAEmu.Game.Models.Game.Quests.QuestComponent.RunComponent() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestComponent.cs:line 65    at AAEmu.Game.Models.Game.Quests.QuestStep.RunComponents() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestStep.cs:line 88    at AAEmu.Game.Models.Game.Quests.Quest.RunCurrentStep() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:line 67    at AAEmu.UnitTests.Game.Quests.Scenario.QuestScenarioDriver.Run(QuestScenarioManifest manifest) in /root/aaemu-dev/AAEmu.UnitTests/Game/Quests/Scenario/QuestScenarioDriver.cs:line 628); PERSIST:Pass |
| 6600 | 보다 더 강력한 힘 | mixed-families | Fail | START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Fail (KeyNotFoundException: The given key 'General' was not present in the dictionary.    at System.Collections.Generic.Dictionary`2.get_Item(TKey key)    at AAEmu.Game.Models.Game.Char.CharacterAbilities.AddActiveExp(Int32 exp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/CharacterAbilities.cs:line 55    at AAEmu.Game.Models.Game.Char.Character.AddExp(Int32 expDelta, Boolean shouldAddAbilityExp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/Character.cs:line 1455    at AAEmu.Game.Models.Game.Quests.Acts.QuestActSupplyExp.RunAct(Quest quest, QuestAct questAct, Int32 currentObjectiveCount) in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Acts/QuestActSupplyExp.cs:line 20    at AAEmu.Game.Models.Game.Quests.QuestAct.RunAct() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestAct.cs:line 50    at AAEmu.Game.Models.Game.Quests.QuestComponent.RunComponent() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestComponent.cs:line 65    at AAEmu.Game.Models.Game.Quests.QuestStep.RunComponents() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestStep.cs:line 88    at AAEmu.Game.Models.Game.Quests.Quest.RunCurrentStep() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:line 67    at AAEmu.UnitTests.Game.Quests.Scenario.QuestScenarioDriver.Run(QuestScenarioManifest manifest) in /root/aaemu-dev/AAEmu.UnitTests/Game/Quests/Scenario/QuestScenarioDriver.cs:line 628); PERSIST:Pass |
| 6615 | 신의 방패 정식 대원이 되다! | mixed-families | Fail | START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Fail (KeyNotFoundException: The given key 'General' was not present in the dictionary.    at System.Collections.Generic.Dictionary`2.get_Item(TKey key)    at AAEmu.Game.Models.Game.Char.CharacterAbilities.AddActiveExp(Int32 exp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/CharacterAbilities.cs:line 55    at AAEmu.Game.Models.Game.Char.Character.AddExp(Int32 expDelta, Boolean shouldAddAbilityExp) in /root/aaemu-dev/AAEmu.Game/Models/Game/Char/Character.cs:line 1455    at AAEmu.Game.Models.Game.Quests.Acts.QuestActSupplyExp.RunAct(Quest quest, QuestAct questAct, Int32 currentObjectiveCount) in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/Acts/QuestActSupplyExp.cs:line 20    at AAEmu.Game.Models.Game.Quests.QuestAct.RunAct() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestAct.cs:line 50    at AAEmu.Game.Models.Game.Quests.QuestComponent.RunComponent() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestComponent.cs:line 65    at AAEmu.Game.Models.Game.Quests.QuestStep.RunComponents() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/QuestStep.cs:line 88    at AAEmu.Game.Models.Game.Quests.Quest.RunCurrentStep() in /root/aaemu-dev/AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:line 67    at AAEmu.UnitTests.Game.Quests.Scenario.QuestScenarioDriver.Run(QuestScenarioManifest manifest) in /root/aaemu-dev/AAEmu.UnitTests/Game/Quests/Scenario/QuestScenarioDriver.cs:line 628); PERSIST:Pass |
| 745 |  | mixed-families | Skip | SKIP:Skip (orphaned context (no quest_contexts row)) |
| 913 | 큰 금니 트로쉬 | mixed-families | Pass | START:Pass; READY:Pass; REWARD:Pass; PERSIST:Pass |

## FAIL rollup (by stage reason)

- **START:Pass; PROGRESS:Fail** — 3 quests: 1033, 3656, 5489
- **START:Fail** — 2 quests: 1650, 4294
- **START:Pass; SUPPLY:Pass; PROGRESS:Fail** — 2 quests: 295, 1897
- **START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Fail** — 2 quests: 6578, 6600
- **START:Pass; PROGRESS:Pass; REWARD:Fail** — 1 quests: 250
- **START:Pass; SUPPLY:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Fail** — 1 quests: 350
- **START:Pass; SUPPLY:Pass; READY:Pass; REWARD:Pass; PERSIST:Fail** — 1 quests: 4292
- **START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Pass; PERSIST:Fail** — 1 quests: 1313
- **START:Pass; PROGRESS:Pass; READY:Pass; REWARD:Fail** — 1 quests: 6615


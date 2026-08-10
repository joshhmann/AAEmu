using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Gimmicks;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.World;

public class Region(WorldInstance worldInstance, int x, int y, uint zoneKey)
{
    private readonly WorldInstance _worldInstance = worldInstance;
    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    private readonly object _objectsLock = new();
    private GameObject[] _objects;
    private int _objectsSize, _charactersSize;
    private Region[] _neighbors;
    private int _playerCount;

    private int X { get; } = x;
    private int Y { get; } = y;
    public int Id => Y + 1024 * X;
    public uint ZoneKey { get; init; } = zoneKey;

    public void AddObject(GameObject obj)
    {
        if (obj == null)
            return;
        lock (_objectsLock)
        {
            if (_objects == null)
            {
                _objects = new GameObject[50];
                _objectsSize = 0;
            }
            else if (_objectsSize >= _objects.Length)
            {
                var temp = new GameObject[_objects.Length * 2];
                Array.Copy(_objects, 0, temp, 0, _objectsSize);
                _objects = temp;
            }

            // Count-first ordering + full fence: GetList<Character> short-circuits
            // on _charactersSize via a lock-free volatile read, so a reader must
            // never see "count 0" while a character is already in the array.
            // Interlocked (full barrier) BEFORE the array write makes the count
            // store visible before the array store on ANY memory model — a reader
            // seeing count==0 cannot yet see the character. A mid-add reader that
            // sees count>0 just pays a wasted iteration (harmless).
            if (obj is Character)
                Interlocked.Increment(ref _charactersSize);
            _objects[_objectsSize] = obj;
            _objectsSize++;
        }

        if (obj.Transform != null)
        {
            obj.Transform.InstanceId = _worldInstance.Id;
            var zoneId = WorldManager.Instance.GetZoneId(_worldInstance.Template, obj.Transform.World.Position.X, obj.Transform.World.Position.Y);
            if (zoneId > 0)
                obj.Transform.ZoneId = zoneId;
        }

        if (obj is Character)
        {
            foreach (var region in GetNeighbors())
                if (region != null)
                    Interlocked.Increment(ref region._playerCount);
        }
        // Show debug info to subscribed players
        if (obj.Transform?._debugTrackers?.Count > 0)
            foreach (var chr in obj.Transform._debugTrackers)
                chr?.SendMessage($"[{DateTime.UtcNow:HH:mm:ss}] {obj.ObjId} entered region ({X} {Y})){(obj is BaseUnit bu ? " - " + bu.Name : "")}");
    }

    public void RemoveObject(GameObject obj) // TODO Нужно доделать =_+
    {
        if (obj == null)
            return;
        lock (_objectsLock)
        {
            if (_objects == null || _objectsSize == 0)
                return;

            if (_objectsSize > 1)
            {
                var index = -1;
                for (var i = 0; i < _objects.Length; i++)
                    if (_objects[i] == obj)
                    {
                        index = i;
                        break;
                    }

                if (index > -1)
                {
                    _objects[index] = _objects[_objectsSize - 1];
                    _objects[_objectsSize - 1] = null;
                    _objectsSize--;
                }
            }
            else if (_objectsSize == 1 && _objects[0] == obj)
            {
                _objects[0] = null;
                _objects = null;
                _objectsSize = 0;
            }

            if (obj is Character)
            {
                // Decrement AFTER the array removal (full fence): a reader seeing
                // count==0 is guaranteed the character is already out of the array
                // (the removal store precedes the decrement store), so the
                // lock-free short-circuit can never skip a present character.
                Interlocked.Decrement(ref _charactersSize);
                foreach (var region in GetNeighbors())
                    if (region != null)
                        Interlocked.Decrement(ref region._playerCount);
            }
        }
        // Show debug info to subscribed players
        if (obj.Transform?._debugTrackers?.Count > 0)
            foreach (var chr in obj.Transform._debugTrackers)
                chr?.SendMessage($"[{DateTime.UtcNow:HH:mm:ss}] {obj.ObjId} left the region ({X} {Y})){(obj is BaseUnit bu ? " - " + bu.Name : "")}");
    }

    public void AddToCharacters(GameObject obj)
    {
        if (_objects == null)
            return;

        // Show the player all the facilities in the region when he/she is added
        if (obj is Character objectAsCharacter)
        {
            var objectsInRegion = GetList(new List<GameObject>(), obj.ObjId);
            foreach (var go in objectsInRegion)
            {
                // Ignore doodads here, as we have a special packet for those
                if (go is Doodad)
                    continue;

                if (go is Gimmick)
                    continue;

                // turn on the motion of the visible NPC
                if (go is Npc { Ai: not null } npc)
                    npc.Ai.ShouldTick = true;

                go.AddVisibleObject(objectAsCharacter);
            }

            // Handle Doodads separately with sets of SCDoodadsCreatedPacket
            var doodads = GetList(new List<Doodad>(), obj.ObjId).ToArray();
            for (var i = 0; i < doodads.Length; i += SCDoodadsCreatedPacket.MaxCountPerPacket)
            {
                var count = doodads.Length - i;
                var temp = new Doodad[count <= SCDoodadsCreatedPacket.MaxCountPerPacket
                    ? count
                    : SCDoodadsCreatedPacket.MaxCountPerPacket];
                Array.Copy(doodads, i, temp, 0, temp.Length);
                objectAsCharacter.SendPacket(new SCDoodadsCreatedPacket(temp));
            }

            // Handle Gimmicks separately with sets of SCGimmicksCreatedPacket
            var gimmicks = GetList(new List<Gimmick>(), obj.ObjId).ToArray();
            for (var i = 0; i < gimmicks.Length; i += SCGimmicksCreatedPacket.MaxCountPerPacket)
            {
                var count = gimmicks.Length - i;
                var temp = new Gimmick[count <= SCGimmicksCreatedPacket.MaxCountPerPacket
                    ? count
                    : SCGimmicksCreatedPacket.MaxCountPerPacket];
                Array.Copy(gimmicks, i, temp, 0, temp.Length);
                objectAsCharacter.SendPacket(new SCGimmicksCreatedPacket(temp));
            }
            // Not sure why or if this is needed, but it's always sent after the creation packets with no reference to any of them
            if (gimmicks.Length > 0)
                objectAsCharacter.SendPacket(new SCGimmickJointsBrokenPacket([]));
        }

        // show the object to all players in the region
        foreach (var characterInRegion in GetList(new List<Character>(), obj.ObjId))
            obj.AddVisibleObject(characterInRegion);
    }

    public void RemoveFromCharacters(GameObject obj)
    {
        if (_objects == null)
            return;

        // Special handling for characters (players)
        if (obj is Character character1)
        {
            // Existing logic for sending SCUnitsRemovedPacket, SCDoodadsRemovedPacket, etc. remains unchanged.
            var unitIds = GetListId<Unit>([], character1.ObjId).ToArray();
            var units = GetList(new List<Unit>(), character1.ObjId);
            foreach (var t in units)
            {
                if (t is Npc { Ai: not null } npc)
                {
                    npc.Ai.ShouldTick = false;
                }
            }
            for (var offset = 0; offset < unitIds.Length; offset += SCUnitsRemovedPacket.MaxCountPerPacket)
            {
                var length = unitIds.Length - offset;
                var temp = new uint[length > SCUnitsRemovedPacket.MaxCountPerPacket
                    ? SCUnitsRemovedPacket.MaxCountPerPacket
                    : length];
                Array.Copy(unitIds, offset, temp, 0, temp.Length);
                character1.SendPacket(new SCUnitsRemovedPacket(temp));
            }

            var doodadIds = GetListId<Doodad>([], character1.ObjId).ToArray();
            for (var offset = 0; offset < doodadIds.Length; offset += SCDoodadsRemovedPacket.MaxCountPerPacket)
            {
                var length = doodadIds.Length - offset;
                var last = length <= SCDoodadsRemovedPacket.MaxCountPerPacket;
                var temp = new uint[last ? length : SCDoodadsRemovedPacket.MaxCountPerPacket];
                Array.Copy(doodadIds, offset, temp, 0, temp.Length);
                character1.SendPacket(new SCDoodadsRemovedPacket(last, temp));
            }

            var gimmickIds = GetList<Gimmick>([], character1.ObjId).Select(g => g.ObjId).ToArray();
            for (var offset = 0; offset < gimmickIds.Length; offset += SCGimmicksRemovedPacket.MaxCountPerPacket)
            {
                var length = gimmickIds.Length - offset;
                var last = length <= SCGimmicksRemovedPacket.MaxCountPerPacket;
                var temp = new uint[last ? length : SCGimmicksRemovedPacket.MaxCountPerPacket];
                Array.Copy(gimmickIds, offset, temp, 0, temp.Length);
                character1.SendPacket(new SCGimmicksRemovedPacket(temp));
            }

            if (character1.CurrentTarget != null && unitIds.Contains(character1.CurrentTarget.ObjId))
            {
                character1.CurrentTarget = null;
                character1.SendPacket(new SCTargetChangedPacket(character1.ObjId, 0));
            }

            // Also remove this character from visibility of nearby players.
            // Without this, other players keep a stale local copy ("ghost target")
            // when the character disconnects or leaves the world.
            foreach (var characterInRegion in GetList(new List<Character>(), character1.ObjId))
                obj.RemoveVisibleObject(characterInRegion);

        }
        // Special handling for non-player objects (NPCs, vehicles, doodads, etc.)
        else
        {
            // Get all characters in this region that should receive a removal packet
            var charactersInRegion = GetList(new List<Character>(), obj.ObjId);

            // --- IMPORTANT FIX ---
            // Filter the list: keep only players who are NOT in the object's new region or its neighbors.
            // This prevents sending "false" removal packets to players who should still see the object.
            var charactersToRemoveFrom = new List<Character>();
            var objRegion = obj.IsVisible ? WorldManager.Instance.GetRegion(obj) : null; // Get the current region of the object
            foreach (var character in charactersInRegion)
            {
                // Check if the player is in the object's region or one of its neighboring regions.
                // If yes, the player should still see the object, so no packet is sent.
                if (objRegion != null)
                {
                    var objNeighbors = objRegion.GetNeighbors();
                    var characterRegion = WorldManager.Instance.GetRegion(character); // Get the region of the player

                    // If the player's region matches the object's region or one of its neighbors, skip this player.
                    if (characterRegion != null && (characterRegion.Equals(objRegion) || objNeighbors.Contains(characterRegion)))
                    {
                        continue; // Skip, do not send packet to this player
                    }
                }
                // If the player is outside the object's visibility range, add them to the removal list
                charactersToRemoveFrom.Add(character);
            }
            // --- END OF FIX ---

            // Send the removal packet ONLY to the filtered list of players
            foreach (var character in charactersToRemoveFrom)
            {
                obj.RemoveVisibleObject(character);
            }
        }
    }

    public Region[] GetNeighbors()
    {
        //Will neighbor regions ever change?
        if (_neighbors == null)
        {
            _neighbors = WorldManager.Instance.GetNeighbors(_worldInstance, X, Y);
            return _neighbors;
        }
        else
        {
            return _neighbors;
        }
    }

    public bool AreNeighborsEmpty()
    {
        if (!IsEmpty())
            return false;
        foreach (var neighbor in GetNeighbors())
            if (!neighbor.IsEmpty())
                return false;
        return true;
    }

    private bool IsEmpty()
    {
        return _charactersSize <= 0;
    }

    public bool HasPlayerActivity()
    {
        return _playerCount > 0;
    }

    public List<uint> GetObjectIdsList(List<uint> result, uint exclude)
    {
        lock (_objectsLock)
        {
            if (_objects == null || _objectsSize == 0)
                return result;

            for (var i = 0; i < _objectsSize; i++)
            {
                var obj = _objects[i];
                if (obj != null && obj.ObjId != exclude)
                    result.Add(obj.ObjId);
            }
        }

        return result;
    }

    public List<GameObject> GetObjectsList(List<GameObject> result, uint exclude)
    {
        lock (_objectsLock)
        {
            if (_objects == null || _objectsSize == 0)
                return result;

            for (var i = 0; i < _objectsSize; i++)
            {
                var obj = _objects[i];
                if (obj != null && obj.ObjId != exclude)
                    result.Add(obj);
            }
        }

        return result;
    }

    private List<uint> GetListId<T>(List<uint> result, uint exclude) where T : class
    {
        lock (_objectsLock)
        {
            if (_objects == null || _objectsSize == 0)
                return result;

            for (var i = 0; i < _objectsSize; i++)
            {
                var obj = _objects[i];
                if (obj is T && obj.ObjId != exclude)
                    result.Add(obj.ObjId);
            }
        }

        return result;
    }

    public List<T> GetList<T>(List<T> result, uint exclude) where T : class
    {
        // Broadcast short-circuit: regions without a single Character can never
        // produce a Character result. Avoids the lock + iteration entirely for
        // the per-neighbor scans on the movement-broadcast path (hot at
        // ~5k broadcasts/sec with roaming bots).
        if (typeof(T) == typeof(Character) && Volatile.Read(ref _charactersSize) <= 0)
            return result;

        lock (_objectsLock)
        {
            if (_objects == null || _objectsSize == 0)
                return result;

            for (var i = 0; i < _objectsSize; i++)
            {
                var obj = _objects[i];
                if (obj is T item && obj.ObjId != exclude)
                    result.Add(item);
            }
        }

        return result;
    }

    public List<T> GetList<T>(List<T> result, uint exclude, float x, float y, float sqrad, bool useModelSize = false) where T : class
    {
        // M5 A1 (execution boundary, Kimi audit 2026-08-09 race witness): this
        // overload reads obj.Transform (a position scan) while movement writers
        // mutate it. The other overloads' snapshot pattern only protects the
        // _objects ARRAY — the Transform read here is the race. Iterate under
        // _objectsLock so the whole distance scan is consistent with region
        // mutation; combined with the marshal (bot steps now run only on the
        // game-loop thread), the witness is closed on the read side.
        if (typeof(T) == typeof(Character) && Volatile.Read(ref _charactersSize) <= 0)
            return result;

        lock (_objectsLock)
        {
            if (_objects == null || _objectsSize == 0)
                return result;

            for (var i = 0; i < _objectsSize; i++)
            {
                var obj = _objects[i];
                if (obj is not T item || obj.ObjId == exclude)
                    continue;

                var finalRad = sqrad;
                if (useModelSize)
                    finalRad += obj.ModelSize * obj.ModelSize;

                var dx = obj.Transform.World.Position.X - x;
                dx *= dx;
                if (dx > finalRad)
                    continue;
                var dy = obj.Transform.World.Position.Y - y;
                dy *= dy;
                if (dx + dy < finalRad)
                    result.Add(item);
            }
        }

        return result;
    }

    public override bool Equals(object obj)
    {
        if (obj == null)
            return false;
        if (obj.GetType() != typeof(Region))
            return false;
        var other = (Region)obj;
        return other._worldInstance == _worldInstance && other.X == X && other.Y == Y;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_worldInstance, X, Y);
    }

    public Region[] FindDifferenceBetweenRegions(Region other)
    {
        var oldNeighbors = this.GetNeighbors();
        var newNeighbors = other.GetNeighbors();

        var difference = oldNeighbors.Except(newNeighbors).ToArray();

        return difference;
    }
}

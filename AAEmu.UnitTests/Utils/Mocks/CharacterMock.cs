using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Utils.Mocks;

public class CharacterMock : Character
{
    private int? _overrideMaxHp;
    private int? _overrideMaxMp;

    public CharacterMock() : base(null)
    {
    }

    public override int MaxHp
    {
        get => _overrideMaxHp ?? base.MaxHp;
        set => _overrideMaxHp = value;
    }

    public override int MaxMp
    {
        get => _overrideMaxMp ?? base.MaxMp;
        set => _overrideMaxMp = value;
    }

    public override void BroadcastPacket(GamePacket packet, bool self)
    {
        return;
    }
}

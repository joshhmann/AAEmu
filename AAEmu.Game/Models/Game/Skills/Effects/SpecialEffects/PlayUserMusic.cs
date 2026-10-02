using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class PlayUserMusic : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.PlayUserMusic;

    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int value1,
        int value2,
        int value3,
        int value4)
    {
        // TODO ...
        if (caster is Character) { Logger.Debug("Special effects: PlayUserMusic value1 {0}, value2 {1}, value3 {2}, value4 {3}", value1, value2, value3, value4); }

        // The related tags seems to be "Play Song" (1155) and "Music Play Animation" (1202)

        if (target is Character player)
        {
            // Send Midi data
            player.BroadcastPacket(new SCSendUserMusicPacket(player.ObjId, player.Name, MusicManager.Instance.GetMidiCache(player.Id)), true);

            var instrument = player.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Musical);
            if (instrument != null)
            {
                // Data-driven first: instrument_sounds (item_id → MIDI program)
                // is the authority for which play buff an instrument uses; the
                // MIDI program family decides the buff (see GetPlayBuffForMidi).
                // Anything unlisted keeps the legacy category switch below.
                // TODO: It might be that instrument doodads require special handling (not tested)
                if (MusicManager.Instance.TryGetInstrumentMidi(instrument.TemplateId, out var midi)
                    && GetPlayBuffForMidi(midi) is uint playBuff)
                {
                    target.Buffs.AddBuff(playBuff, caster);
                }
                else switch ((ItemCategory)instrument.Template.CategoryId)
                {
                    case ItemCategory.Lute:
                        target.Buffs.AddBuff((uint)BuffConstants.LutePlay, caster);
                        break;
                    case ItemCategory.Flute:
                        target.Buffs.AddBuff((uint)BuffConstants.FlutePlay, caster);
                        break;
                    //case ItemCategory.Drum:
                    //    // NOTE: You can't actually use drum weapons as a instrument, client will throw a "requirements not met" error
                    //    target.Buffs.AddBuff((uint)BuffConstants.DrumPlay, caster);
                    //    break;
                    default:
                        Logger.Trace("SpecialEffectAction - PlayUserMusic - Equipped instrument slot is not a known instrument !");
                        break;
                }
            }
            else
            {
                Logger.Trace("SpecialEffectAction - PlayUserMusic - No instrument equipped !");
            }
        }
    }

    /// <summary>
    /// Resolves the play buff for an instrument_sounds MIDI program by its
    /// program family. The families are the exact sets observed in the 1.2
    /// reference data (archaeology MCP query_sql, canonical compact.sqlite3
    /// md5 78b3bdbf038db3b927056106efdf91af, 199 rows): programs
    /// 24/25/26/29/30/32 sit on Lute-category items, programs
    /// 66/68/69/70/71/72/73/74/105/106 on Flute-category items, with zero
    /// rows outside categories 80/81. Returns null for any unlisted program
    /// so the caller keeps the legacy item-category switch as fallback.
    /// </summary>
    internal static uint? GetPlayBuffForMidi(uint midi)
    {
        switch (midi)
        {
            case 24:
            case 25:
            case 26:
            case 29:
            case 30:
            case 32:
                return (uint)BuffConstants.LutePlay;
            case 66:
            case 68:
            case 69:
            case 70:
            case 71:
            case 72:
            case 73:
            case 74:
            case 105:
            case 106:
                return (uint)BuffConstants.FlutePlay;
            default:
                return null;
        }
    }
}

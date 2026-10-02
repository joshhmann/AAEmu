using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Music;
using AAEmu.Game.Utils.DB;
using NLog;

namespace AAEmu.Game.Core.Managers;

public class MusicManager(IMusicIdManager musicIdManager, IItemManager itemManager) : Singleton<MusicManager>, IMusicManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, SongData> _uploadQueue; // playerId, song
    private Dictionary<uint, SongData> _allSongs; // songId, song
    private Dictionary<uint, byte[]> _midiCache; // playerId, midi data
    private Dictionary<uint, uint> _instrumentSounds; // itemId → MIDI program (instrument_sounds)

    public void Load()
    {
        _uploadQueue = [];
        _allSongs = [];
        _midiCache = [];

        LoadInstrumentSounds();

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM music";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var songData = new SongData
                        {
                            Id = reader.GetUInt32("id"),
                            AuthorId = reader.GetUInt32("author"),
                            Title = reader.GetString("title"),
                            Song = reader.GetString("song")
                        };
                        _allSongs.Add(songData.Id, songData);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Loads the instrument → MIDI program mapping from the read-only
    /// reference data (compact.sqlite3 instrument_sounds(item_id, midi)).
    /// Corroborated via archaeology MCP query_sql against the canonical DB
    /// (md5 78b3bdbf038db3b927056106efdf91af): 199 rows; every mapped item
    /// sits in an instrument category and the MIDI families agree exactly
    /// with those categories (strings → Lute, winds → Flute). Called by
    /// <see cref="Load"/>; public so tests can drive the production loader
    /// path without the MySQL song table.
    /// </summary>
    public void LoadInstrumentSounds()
    {
        _instrumentSounds = [];

        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM instrument_sounds";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        _instrumentSounds[reader.GetUInt32("item_id")] = reader.GetUInt32("midi");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Looks up the MIDI program for an instrument item. Returns false when
    /// the mapping is not loaded or the item is not a listed instrument —
    /// callers keep the legacy item-category switch as fallback.
    /// </summary>
    public bool TryGetInstrumentMidi(uint itemId, out uint midi)
    {
        midi = 0;
        return _instrumentSounds != null && _instrumentSounds.TryGetValue(itemId, out midi);
    }

    /// <summary>Number of loaded instrument_sounds rows (diagnostics/tests).</summary>
    public int InstrumentSoundCount => _instrumentSounds?.Count ?? 0;

    public bool Save(SongData songData)
    {
        songData.Id = musicIdManager.GetNextId();

        using (var connection = MySQL.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "REPLACE INTO music (" +
                                      "`id`,`author`,`title`,`song` ) VALUES ( " +
                                      "@id, @author, @title, @song" +
                                      " )";
                command.Parameters.AddWithValue("@id", songData.Id);
                command.Parameters.AddWithValue("@author", songData.AuthorId);
                command.Parameters.AddWithValue("@title", songData.Title);
                command.Parameters.AddWithValue("@song", songData.Song);
                if (command.ExecuteNonQuery() != 1)
                {
                    Logger.Warn("Error saving song to DB for {0} ({1})", songData.Title, songData.Id);
                    return false;
                }
            }
        }
        _allSongs.Add(songData.Id, songData);

        return true;
    }

    public void UploadSong(uint charId, string title, string song, ulong itemId)
    {
        if (!_uploadQueue.TryGetValue(charId, out var q))
        {
            q = new SongData();
            _uploadQueue.Add(charId, q);
        }
        q.AuthorId = charId;
        q.Title = title;
        q.Song = song;
        q.SourceItemId = itemId;
    }

    public bool CreateSheetMusic(Character player, Item sourceItem)
    {
        // Check if a valid owned item
        if (sourceItem == null || sourceItem._holdingContainer.OwnerId != player.Id)
        {
            Logger.Warn("Player {0} ({1}) does not own the used source item", player.Name, player.Id);
            return false;
        }

        // Grab the related queued song (if any)
        if (!_uploadQueue.TryGetValue(player.Id, out var sud))
        {
            Logger.Warn("Player {0} ({1}) did not upload any music yet.", player.Name, player.Id);
            return false;
        }

        if (player.Inventory.Bag.FreeSlotCount < 1)
        {
            player.SendErrorMessage(ErrorMessageType.BagFull);
            Logger.Warn("Player {0} ({1}) did not have enough space to aquire new music sheet {2} ({3})",
                player.Name, player.Id, sud.Title, sud.Id);
            return false;
        }

        // Save to DB
        if (Save(sud))
        {
            var sheet = (MusicSheetItem)itemManager.Create(Item.SheetMusic, 1, 0, true);
            sheet.OwnerId = player.Id;
            sheet.MadeUnitId = player.Id;
            sheet.SongId = sud.Id;

            // Add Sheet Music to inventory
            if (!player.Inventory.Bag.AddOrMoveExistingItem(ItemTaskType.SaveMusicNotes, sheet))
            {
                Logger.Warn("Player {0} ({1}) had a unknown error when adding Sheet Music to inventory {2} ({3})",
                    player.Name, player.Id, sud.Title, sud.Id);
                return false;
            }

            // Consume Music Paper
            if (player.Inventory.Bag.ConsumeItem(ItemTaskType.SaveMusicNotes, sourceItem.TemplateId, 1, sourceItem) <= 0)
            {
                Logger.Warn("Failed to consume source item while creating music for Player {0} ({1}) item {2} ({3})",
                    player.Name, player.Id, sourceItem.Id, sourceItem.Template.Name);
            }
        }

        return true;
    }

    public SongData GetSongById(uint songId)
    {
        if (_allSongs.TryGetValue(songId, out var song))
            return song;
        return null;
    }

    public void CacheMidi(uint playerId, byte[] midiData)
    {
        _midiCache.Remove(playerId);
        _midiCache.Add(playerId, midiData);
    }

    public byte[] GetMidiCache(uint playerId)
    {
        if (_midiCache.TryGetValue(playerId, out var data))
            return data;
        return [];
    }
}

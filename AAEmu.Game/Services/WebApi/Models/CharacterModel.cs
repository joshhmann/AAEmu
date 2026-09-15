namespace AAEmu.Game.Services.WebApi.Models;

internal record CharacterModel(uint Id, string Name, uint Level, DateTime CreatedAt, bool IsOnline, float X = 0, float Y = 0, float Z = 0);

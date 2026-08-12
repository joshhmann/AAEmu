namespace AAEmu.Game.Models.Game.Housing;

public class HousingAreas
{
    public uint Id { get; set; }
    public string Name { get; set; }
    public uint GroupId { get; set; }

    /// <summary>
    /// Client level-design shape name (<c>LevelDesignShape_&lt;zoneKey&gt;_&lt;name&gt;_&lt;n&gt;</c>) —
    /// the join key between sqlite <c>housing_areas</c> and the pak <c>housing_area.xml</c>
    /// AreaShape entities (verified: 375 of 401 rows join a shape in the matching zone).
    /// </summary>
    public string Comments { get; set; }
}

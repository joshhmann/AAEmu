using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items.Procs;

namespace AAEmu.Game.Models.Game.Units;

public class UnitProcs(Unit owner)
{
    private readonly List<ItemProc> _procs = [];
    private readonly Dictionary<ProcChanceKind, List<ItemProc>> _procsByChanceKind = [];
    private readonly Func<uint, ItemProc> _procFactory;

    public Unit Owner { get; set; } = owner;

    public UnitProcs(Unit owner, Func<uint, ItemProc> procFactory) : this(owner)
    {
        _procFactory = procFactory;
    }

    public void AddProc(uint procId)
    {
        var proc = _procFactory != null ? _procFactory(procId) : new ItemProc(procId);
        _procs.Add(proc);
        if (!_procsByChanceKind.ContainsKey(proc.Template.ChanceKind))
            _procsByChanceKind.Add(proc.Template.ChanceKind, []);
        _procsByChanceKind[proc.Template.ChanceKind].Add(proc);
    }

    public void RemoveProc(uint procId)
    {
        var procTemplate = ItemManager.Instance.GetItemProcTemplate(procId);

        if (_procsByChanceKind.TryGetValue(procTemplate.ChanceKind, out var value))
            value.RemoveAll(p => p.TemplateId == procId);
    }

    public void RollProcsForKind(ProcChanceKind kind)
    {
        if (!_procsByChanceKind.TryGetValue(kind, out var procs))
            return;
        foreach (var proc in procs)
        {
            // Skip while on cooldown (mirrors ItemProc.Apply's own check)
            if (proc.LastProc.AddSeconds(proc.Template.CooldownSec) > DateTime.UtcNow)
                continue;

            proc.Apply(Owner);
            proc.LastProc = DateTime.UtcNow;
        }
    }
}

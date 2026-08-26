using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Tasks.TimedRewards;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// For timed adding credits and loyalty
/// </summary>
public class TimedRewardsManager(ITaskManager taskManager) : Singleton<TimedRewardsManager>, ITimedRewardsManager
{
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
            return; // idempotent: a double start must not double-schedule the regen tick
        _initialized = true;
        taskManager.Schedule(new TimedRewardsTask(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Effective labor cap for the given account tier — now config-driven;
    /// the retail-confirmed values (2000 free / 5000 premium) live in LaborConfig.
    /// </summary>
    public static short GetMaxLabor(bool isPremium)
    {
        return (short)Math.Clamp(AppConfiguration.Instance.Labor.GetCap(isPremium), 0, short.MaxValue);
    }

    /// <summary>
    /// Adds labor, internal use only
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="currentLabor"></param>
    /// <param name="addLabor"></param>
    private void DoAddLabor(GameConnection connection, short currentLabor, int addLabor)
    {
        addLabor = ComputeGrant(AppConfiguration.Instance.Labor, connection.Payment.PremiumState, currentLabor, addLabor);
        AccountManager.Instance.UpdateTickTimes(connection.AccountId, DateTime.UtcNow, true, false, false);
        if (addLabor > 0)
        {
            var newLabor = (short)(currentLabor + addLabor);
            AccountManager.Instance.UpdateLabor(connection.AccountId, newLabor);

            connection.ActiveChar?.SendPacket(new SCCharacterLaborPowerChangedPacket(addLabor, 0, 0, 0));

            // Update cache if character was logged in
            connection.ActiveChar?.InitializeLaborCache(newLabor, DateTime.UtcNow);
        }
    }

    public void DoTick()
    {
        var laborConfig = AppConfiguration.Instance.Labor;
        var connections = GameConnectionTable.Instance.GetConnections();
        foreach (var connection in connections)
        {
            //var character = connection.ActiveChar;
            // Grab current values for last ticks
            var accountDetails = AccountManager.Instance.GetAccountDetails(connection.AccountId);

            // Distribute Labor if needed (only for online labor)
            if (laborConfig.TickMinutes > 0 && accountDetails.LastLaborTick.AddMinutes(laborConfig.TickMinutes) <= DateTime.UtcNow)
            {
                var addLabor = laborConfig.GetOnlineTickAmount(connection.Payment.PremiumState);
                DoAddLabor(connection, accountDetails.Labor, addLabor);
            }

            // Distribute Credits if needed
            if (AppConfiguration.Instance.Credits.TickMinutes > 0 && accountDetails.LastCreditsTick.AddMinutes(AppConfiguration.Instance.Credits.TickMinutes) <= DateTime.UtcNow)
            {
                // Update Credits
                AccountManager.Instance.AddCredits(connection.AccountId, AppConfiguration.Instance.Credits.GetTickAmount(connection.Payment.PremiumState));
                AccountManager.Instance.UpdateTickTimes(connection.AccountId, DateTime.UtcNow, false, true, false);
                connection.ActiveChar?.SendPacket(new SCICSCashPointPacket(AccountManager.Instance.GetAccountDetails(connection.AccountId).Credits));
            }

            // Distribute Loyalty if needed
            if (AppConfiguration.Instance.Loyalty.TickMinutes > 0 && accountDetails.LastLoyaltyTick.AddMinutes(AppConfiguration.Instance.Loyalty.TickMinutes) <= DateTime.UtcNow)
            {
                // Update Loyalty
                AccountManager.Instance.AddLoyalty(connection.AccountId, AppConfiguration.Instance.Loyalty.GetTickAmount(connection.Payment.PremiumState));
                AccountManager.Instance.UpdateTickTimes(connection.AccountId, DateTime.UtcNow, false, false, true);
                connection.ActiveChar?.SendPacket(new SCBmPointPacket(AccountManager.Instance.GetAccountDetails(connection.AccountId).Loyalty));
            }
        }
    }

    public void DoDailyAccountLogin(uint accountId)
    {
        if (AppConfiguration.Instance.Credits.DailyLogin > 0)
            AccountManager.Instance.AddCredits(accountId, AppConfiguration.Instance.Credits.DailyLogin);

        if (AppConfiguration.Instance.Loyalty.DailyLogin > 0)
            AccountManager.Instance.AddLoyalty(accountId, AppConfiguration.Instance.Loyalty.DailyLogin);

        AccountManager.Instance.UpdateDivineClock(accountId, 0, 0);
    }

    public void AddOfflineLabor(GameConnection connection, DateTime lastLoginTime, short currentLabor)
    {
        // Offline regen shares the online Labor section's cadence; the amount is
        // mode-driven: everyone regenerates at the patron rate when Unchained,
        // patrons-only in VanillaRetail (free accounts earn nothing offline —
        // retail-confirmed, formula-corroboration-2026-08-25.md L3).
        var laborConfig = AppConfiguration.Instance.Labor;
        var delta = DateTime.UtcNow - lastLoginTime;
        var ticksToAdd = ComputeOfflineTicks(delta, laborConfig.TickMinutes);
        if (ticksToAdd <= 0)
            return;
        var addLabor = laborConfig.GetOfflineTickAmount(connection.Payment.PremiumState) * ticksToAdd;
        DoAddLabor(connection, currentLabor, addLabor);
    }

    /// <summary>Floor-to-tick offline accrual model (unchanged from the original machinery).</summary>
    internal static int ComputeOfflineTicks(TimeSpan delta, int tickMinutes) =>
        (int)Math.Floor(delta.TotalMinutes / tickMinutes);

    /// <summary>
    /// Clamps a labor addition against the cap — additions only, never reduces
    /// a balance below its current value.
    /// </summary>
    internal static int ClampLaborGrant(short currentLabor, int addLabor, int cap)
    {
        var maxLaborToAdd = cap - currentLabor;
        if (maxLaborToAdd < 0)
            maxLaborToAdd = 0;
        return Math.Min(addLabor, maxLaborToAdd);
    }

    /// <summary>
    /// Final regen grant for a tick: clamped at the tier cap, or unbounded
    /// when UnlimitedCap is set.
    /// </summary>
    internal static int ComputeGrant(LaborConfig laborConfig, bool isPremium, short currentLabor, int addLabor) =>
        laborConfig.UnlimitedCap
            ? addLabor
            : ClampLaborGrant(currentLabor, addLabor, laborConfig.GetCap(isPremium));

}

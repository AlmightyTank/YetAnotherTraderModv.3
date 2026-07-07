using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace YetAnotherTraderMod.src;

/// <summary>
/// Kept as a no-op compatibility loader so older references do not break.
/// Real quest import now happens inside YATMTraderRuntimeService after AddTraderToDb().
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 60)]
public sealed class YATMQuestLoader : IOnLoad
{
    public Task OnLoad()
    {
        YATMLogger.LogDebug("[QuestLoader] No-op. Quests are imported by YATMTraderRuntimeService after Tony is added to DB.");
        return Task.CompletedTask;
    }
}

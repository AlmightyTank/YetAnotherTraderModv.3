using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using YetAnotherTraderMod.src.Services.ItemHelpers;

namespace YetAnotherTraderMod.src;

/// <summary>
/// Compatibility/finalization loader. Real quest import happens inside
/// YATMTraderRuntimeService after AddTraderToDb(); this pass only retries any
/// deferred custom-item quest extensions that targeted a trader loaded later.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 5)]
public sealed class YATMQuestLoader(YATMDeferredQuestItemService deferredQuestItemService) : IOnLoad
{
    public Task OnLoad()
    {
        deferredQuestItemService.ApplyDeferredQuestData("Post-load retry", finalPass: true);
        return Task.CompletedTask;
    }
}

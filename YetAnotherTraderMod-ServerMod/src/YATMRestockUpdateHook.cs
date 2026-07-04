using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using System.Threading.Tasks;

namespace YetAnotherTraderMod.src;

[Injectable(TypePriority = OnUpdateOrder.InsuranceCallbacks)]
public sealed class YATMRestockUpdateHook(YATMTraderRuntimeService runtimeService) : IOnUpdate
{
    public Task<bool> OnUpdate(long timeSinceLastRun)
    {
        return runtimeService.OnRestockUpdate(timeSinceLastRun);
    }
}

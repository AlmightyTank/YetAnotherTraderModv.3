using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Logger;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;

namespace YetAnotherTraderMod.src;

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 10)]
public class YATMInsurancePatch(
    ISptLogger<YATMInsurancePatch> logger,
    DatabaseService databaseService,
    ConfigServer configServer
) : IOnLoad
{
    private static readonly MongoId TonyId = new("66a0f6b2c4d8e90123456789");

    public Task OnLoad()
    {
        PatchTraderDialogue();
        PatchLocales();
        PatchInsuranceReturnChance();

        return Task.CompletedTask;
    }

    private void PatchInsuranceReturnChance()
    {
        InsuranceConfig insuranceConfig = configServer.GetConfig<InsuranceConfig>();

        insuranceConfig.ReturnChancePercent ??= [];

        // Tony insurance return chance.
        // 95 = 95% chance item returns, 5% chance it gets deleted.
        // 100 = always returns unless map rules like Labs block insurance.
        insuranceConfig.ReturnChancePercent[TonyId] = 95;

        if (YATMLogger.IsDebugEnabled)
        {
            YATMLogger.LogDebug("Patched insurance return chance.");
        }
    }

    private void PatchTraderDialogue()
    {
        var trader = databaseService.GetTrader(TonyId);

        if (trader is null)
        {
            YATMLogger.Log("Could not patch insurance dialogue. Trader {TonyId} was not found.");
            return;
        }

        if (trader.Dialogue == null)
        {
            YATMLogger.Log("Could not patch insurance dialogue. Trader {TonyId} Dialogue property is null and cannot be set (init-only).");
            return;
        }

        trader.Dialogue["insuranceStart"] =
        [
            $"{TonyId} insuranceStart 0",
            $"{TonyId} insuranceStart 1",
            $"{TonyId} insuranceStart 2",
            $"{TonyId} insuranceStart 3",
        ];

        trader.Dialogue["insuranceFound"] =
        [
            $"{TonyId} insuranceFound 0",
            $"{TonyId} insuranceFound 1",
            $"{TonyId} insuranceFound 2",
            $"{TonyId} insuranceFound 3"
        ];

        trader.Dialogue["insuranceExpired"] =
        [
            $"{TonyId} insuranceExpired 0",
            $"{TonyId} insuranceExpired 1",
            $"{TonyId} insuranceExpired 2",
            $"{TonyId} insuranceExpired 3",
        ];

        trader.Dialogue["insuranceComplete"] =
        [
            $"{TonyId} insuranceComplete 0",
            $"{TonyId} insuranceComplete 1",
            $"{TonyId} insuranceComplete 2",
            $"{TonyId} insuranceComplete 3",
        ];

        trader.Dialogue["insuranceFailed"] =
        [
            $"{TonyId} insuranceFailed 0",
            $"{TonyId} insuranceFailed 1",
            $"{TonyId} insuranceFailed 2", 
            $"{TonyId} insuranceFailed 3",
        ];

        trader.Dialogue["insuranceFailedLabs"] =
        [
            $"{TonyId} insuranceFailedLabs 0",
            $"{TonyId} insuranceFailedLabs 1",
            $"{TonyId} insuranceFailedLabs 2",
            $"{TonyId} insuranceFailedLabs 3",
        ];

        trader.Dialogue["insuranceFailedLabyrinth"] =
        [
            $"{TonyId} insuranceFailedLabyrinth 0",
            $"{TonyId} insuranceFailedLabyrinth 1",
            $"{TonyId} insuranceFailedLabyrinth 2",
            $"{TonyId} insuranceFailedLabyrinth 3",
        ];

        if (YATMLogger.IsDebugEnabled)
        {
            YATMLogger.LogDebug("Patched insurance dialogue keys.");
        }
    }

    private void PatchLocales()
    {
        var locales = databaseService.GetLocales();

        if (!locales.Global.TryGetValue("en", out var englishLocale) || englishLocale is null)
        {
            YATMLogger.Log("Could not patch insurance locale text. English locale was not found.");
            return;
        }

        englishLocale.AddTransformer(locale =>
        {
            locale[$"{TonyId} insuranceStart 0"] =
                "You paid for speed. My people are already moving.";

            locale[$"{TonyId} insuranceStart 1"] =
                "Volkov prices buy Volkov speed. Watch your messages.";

            locale[$"{TonyId} insuranceStart 2"] =
                "The payment cleared. Your gear is being handled.";

            locale[$"{TonyId} insuranceStart 3"] =
                "I have people closer than you think. They are moving now.";


            locale[$"{TonyId} insuranceFound 0"] =
                "Your gear is back. Fast work is not cheap, but you already knew that.";

            locale[$"{TonyId} insuranceFound 1"] =
                "Recovered and stored. Take it before I start charging for the space.";

            locale[$"{TonyId} insuranceFound 2"] =
                "Your things came back. Expensive, quick, and clean.";

            locale[$"{TonyId} insuranceFound 3"] =
                "My people found your gear. Do not ask what it cost me to get it back.";


            locale[$"{TonyId} insuranceExpired 0"] =
                "I held your gear long enough. Storage is not charity.";

            locale[$"{TonyId} insuranceExpired 1"] =
                "Your time ran out. I do not keep dead men’s property forever.";

            locale[$"{TonyId} insuranceExpired 2"] =
                "You left your gear sitting too long. It is gone now.";

            locale[$"{TonyId} insuranceExpired 3"] =
                "I gave you more time than the others would have. You wasted it.";


            locale[$"{TonyId} insuranceComplete 0"] =
                "Business finished. Your gear is no longer my problem.";

            locale[$"{TonyId} insuranceComplete 1"] =
                "You collected it. Good. I prefer clean accounts.";

            locale[$"{TonyId} insuranceComplete 2"] =
                "That closes the job. Next time, try not to lose it.";

            locale[$"{TonyId} insuranceComplete 3"] =
                "Recovered, stored, collected. Clean work.";


            locale[$"{TonyId} insuranceFailed 0"] =
                "Nothing came back. Someone got to it first, or there was nothing worth saving.";

            locale[$"{TonyId} insuranceFailed 1"] =
                "No recovery. Whoever found it wanted it more than you did.";

            locale[$"{TonyId} insuranceFailed 2"] =
                "My people found the place, not the gear. That should tell you enough.";

            locale[$"{TonyId} insuranceFailed 3"] =
                "The trail went cold. Your gear is gone.";


            locale[$"{TonyId} insuranceFailedLabs 0"] =
                "Labs is not a place for recovery work. You knew the risk.";

            locale[$"{TonyId} insuranceFailedLabs 1"] =
                "Nothing comes back from Labs unless someone very powerful allows it.";

            locale[$"{TonyId} insuranceFailedLabs 2"] =
                "Labs swallowed your gear. I am not sending men into that grinder.";

            locale[$"{TonyId} insuranceFailedLabs 3"] =
                "No recovery from Labs. Some doors stay closed.";


            locale[$"{TonyId} insuranceFailedLabyrinth 0"] =
                "The Labyrinth took your gear. I am not paying men to vanish with it.";

            locale[$"{TonyId} insuranceFailedLabyrinth 1"] =
                "No recovery from the Labyrinth. Even my people know when to stop.";

            locale[$"{TonyId} insuranceFailedLabyrinth 2"] =
                "Your gear is gone. The Labyrinth does not give things back.";

            locale[$"{TonyId} insuranceFailedLabyrinth 3"] =
                "I do business in dangerous places. The Labyrinth is something else.";

            return locale;
        });

        if (YATMLogger.IsDebugEnabled)
        {
            YATMLogger.LogDebug("Patched insurance locale text.");
        }
    }
}
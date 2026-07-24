using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using System.Linq;
using System.Threading.Tasks;

namespace YetAnotherTraderMod.src;

/// <summary>
/// One-time profile patch for players who accepted "No Names Spoken" before
/// tonys_calling_card.json had its QuestComplete requirement. Without that
/// requirement, SPT's RewardHelper.FindAndAddHideoutProductionIdToProfile
/// couldn't match the quest's ProductionScheme reward to a hideout craft, so
/// it never added the recipe to those profiles. New quest acceptances are
/// unaffected; this only backfills profiles that already have the quest.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class YATMNoNamesSpokenMigration : IOnLoad
{
    private static readonly MongoId NoNamesSpokenQuestId = new("6a300025d2999bc7b9bba926");
    private static readonly MongoId TonysCallingCardRecipeId = new("6a6361c699377840f8e2b849");

    private static readonly QuestStatusEnum[] AcceptedStatuses =
    [
        QuestStatusEnum.Started,
        QuestStatusEnum.AvailableForFinish,
        QuestStatusEnum.Success
    ];

    private readonly SaveServer _saveServer;

    public YATMNoNamesSpokenMigration(SaveServer saveServer)
    {
        _saveServer = saveServer;
    }

    public Task OnLoad()
    {
        var patched = 0;

        foreach (var (sessionId, profile) in _saveServer.GetProfiles())
        {
            var pmcData = profile.CharacterData?.PmcData;
            if (pmcData?.Quests == null)
            {
                continue;
            }

            var hasAcceptedQuest = pmcData.Quests.Any(quest =>
                quest.QId == NoNamesSpokenQuestId && AcceptedStatuses.Contains(quest.Status));

            if (!hasAcceptedQuest)
            {
                continue;
            }

            pmcData.UnlockedInfo ??= new UnlockedInfo();
            pmcData.UnlockedInfo.UnlockedProductionRecipe ??= [];

            if (pmcData.UnlockedInfo.UnlockedProductionRecipe.Add(TonysCallingCardRecipeId))
            {
                patched++;
                YATMLogger.Log($"[NoNamesSpokenMigration] Unlocked Tony's calling card recipe for session {sessionId}.");
            }
        }

        if (patched > 0)
        {
            YATMLogger.Log($"[NoNamesSpokenMigration] Patched {patched} existing profile(s) missing the calling card craft unlock.");
        }

        return Task.CompletedTask;
    }
}

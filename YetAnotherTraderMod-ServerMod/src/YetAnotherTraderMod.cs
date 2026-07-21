using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using YetAnotherTraderMod.config;
using Path = System.IO.Path;

namespace YetAnotherTraderMod.src;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.amightytank.yatm";
    public override string Name { get; init; } = "YetAnotherTraderMod";
    public override string Author { get; init; } = "AMightyTank | Based on PrisciluOrigins by Reis/Anigx";
    public override List<string>? Contributors { get; init; } = ["Reis", "Anigx"];
    public override SemanticVersioning.Version Version { get; init; } = new("0.2.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.13");
    public override List<string>? Incompatibilities { get; init; } = [];
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("^2.0.20") }
    };
    public override string? Url { get; init; } = null;
    public override bool? IsBundleMod { get; init; } = true;
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 4)]
public sealed class YetAnotherTraderMod(YATMTraderRuntimeService runtimeService) : IOnLoad
{
    public async Task OnLoad()
    {
        await runtimeService.OnLoad();
    }
}

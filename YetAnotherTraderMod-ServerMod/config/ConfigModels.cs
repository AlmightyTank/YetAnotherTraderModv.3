using System.Collections.Generic;

namespace YetAnotherTraderMod.config;

public class SettingsConfig
{
    public int MinLevel { get; set; } = 1;
    public bool UnlockedByDefault { get; set; } = false;

    public int TraderRefreshMin { get; set; } = 1800;
    public int TraderRefreshMax { get; set; } = 3600;

    public bool RerollAssortOnRestock { get; set; } = true;

    public bool AddTraderToFleaMarket { get; set; } = true;
    public bool EnableCustomQuests { get; set; } = true;
    public int InsurancePriceCoef { get; set; } = 25;
    public double RepairQuality { get; set; } = 0.8;

    public bool RandomizeStockAvailable { get; set; } = true;
    public int OutOfStockChance { get; set; } = 15;

    public bool PreventBarterOffersOutOfStock { get; set; } = true;
    public bool UnlimitedStock { get; set; } = false;
    public double PriceMultiplier { get; set; } = 1.0;

    public bool CashOffersOnly { get; set; } = false;
    public bool RandomizeCashBarterOffers { get; set; } = true;
    public int CashOfferPercent { get; set; } = 85;

    public bool DebugLogging { get; set; } = false;
    public bool RealDebugLogging { get; set; } = false;
}

public class PriceConfigItem
{
    public string? OfferId { get; set; }
    public string? PackOfferId { get; set; }
    public string TplId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public double Price { get; set; }
    public string Currency { get; set; } = "RUB";
    public bool CashOnly { get; set; } = true;
    public bool AlwaysBarter { get; set; } = false;
    public bool AlwaysInStock { get; set; } = false;
    public List<List<PaymentConfigItem>>? BarterScheme { get; set; }
    public string? AmmoBarterPackTplId { get; set; }
    public string? AmmoBarterPackItemName { get; set; }
    public int AmmoBarterPackSize { get; set; } = 0;
    public string BarterSchemeValueBasis { get; set; } = "Unit";
}

public class PaymentConfigItem
{
    public string TplId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public double Count { get; set; } = 1;
}

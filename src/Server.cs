using System.Collections.Concurrent;
using System.Text.Json;
using Path = System.IO.Path;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Helpers.Ragfair;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Eft.Repair;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Services.Ragfair;

namespace ComponentWear;

public record Metadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.component.wear";
    public string Name { get; init; } = "ComponentWear";
    public string Author { get; init; } = "Local";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("4.1.6");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

public sealed record ServerSettings
{
    public bool UsedFleaParts { get; init; } = true;
    public double UsedOfferChance { get; init; } = 0.25;
    public bool BotPartCondition { get; init; } = true;
}

[Injectable(TypePriority = OnLoadOrder.RagfairCallbacks - 100)]
public sealed class ServerLoad(TemplateTable templates, GlobalTable globals, SaveServer save, EventOutputHolder eventOutput,
    ISptLogger<ServerLoad> logger) : IOnLoad
{
    internal static readonly Dictionary<MongoId, PartKind> Parts = new();
    internal static ISptLogger<ServerLoad> Log = null!;
    internal static SaveServer Saves = null!;
    internal static EventOutputHolder EventOutput = null!;
    internal static ServerSettings Settings = new();
    internal static string Journal = string.Empty;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Log = logger;
        Saves = save;
        EventOutput = eventOutput;
        string directory = Path.GetDirectoryName(typeof(ServerLoad).Assembly.Location)!;
        string settings = Path.Combine(directory, "config.json");
        if (File.Exists(settings))
            Settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(settings),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Settings;
        Journal = Path.GetFullPath(Path.Combine(directory, "..", "..", "..", "..",
            "BepInEx", "config", "local.component.wear.raid"));

        foreach (TemplateItem item in templates.Items.Values)
        {
            if (item.Type != "Item" || item.Properties == null)
                continue;

            PartKind kind = Classify(item);
            if (kind == PartKind.None)
                continue;

            Parts[item.Id] = kind;
            item.Properties.Durability = 100;
            item.Properties.MaxDurability = 100;
            item.Properties.RepairSpeed = 23; // Also advertises component support to the matching client.
            item.Properties.RepairCost = Math.Max(item.Properties.RepairCost ?? 0, kind switch
            {
                PartKind.Barrel => 26,
                PartKind.Upper => 24,
                PartKind.Suppressor => 20,
                _ => 12
            });
            item.Properties.MinRepairDegradation ??= 0;
            item.Properties.MaxRepairDegradation ??= 0.03;
            item.Properties.MinRepairKitDegradation ??= 0;
            item.Properties.MaxRepairKitDegradation ??= 0.025;
        }

        // Extend only kits and strategy that already accept firearms.
        foreach (TemplateItem template in templates.Items.Values)
        {
            var filter = template.Properties?.TargetItemFilter;
            if (filter?.Contains(BaseClasses.WEAPON) == true)
                template.Properties!.TargetItemFilter = filter.Concat(Parts.Keys).Distinct().ToArray();
        }
        var firearms = globals.Configuration.RepairSettings.RepairStrategies.Firearms;
        firearms.Filter = firearms.Filter.Concat(Parts.Keys.Select(id => id.ToString())).Distinct().ToArray();

        var harmony = new Harmony("local.component.wear.server");
        harmony.PatchAll(typeof(ServerLoad).Assembly);
        logger.Info($"[Component Wear] Ready: {Parts.Count} repairable part templates; flea={Settings.UsedFleaParts}, bot wear={Settings.BotPartCondition}.");
        return Task.CompletedTask;
    }

    private PartKind Classify(TemplateItem item)
    {
        MongoId parent = item.Parent;
        for (int depth = 0; depth < 24; depth++)
        {
            if (parent == BaseClasses.BARREL)
                return PartKind.Barrel;
            if (parent == BaseClasses.RECEIVER)
                return item.Properties?.Slots?.Any(slot => slot.Name == "mod_barrel") == true
                    ? PartKind.Upper : PartKind.None;
            if (parent == BaseClasses.SILENCER)
                return PartKind.Suppressor;
            if (parent == BaseClasses.MUZZLE)
                return PartKind.Muzzle;
            if (!templates.Items.TryGetValue(parent, out TemplateItem? category) || category.Parent == parent)
                break;
            parent = category.Parent;
        }
        return PartKind.None;
    }

    internal static void SetCondition(Item item, double current, double max)
    {
        item.Upd ??= new Upd();
        item.Upd.Repairable = new UpdRepairable { Durability = Math.Min(current, max), MaxDurability = max };
    }
}

internal enum PartKind { None, Barrel, Upper, Suppressor, Muzzle }

[HarmonyPatch]
internal static class SkipFullPartRepairs
{
    [HarmonyPatch(typeof(RepairController), nameof(RepairController.RepairWithKit))]
    [HarmonyPrefix]
    private static bool Kit(MongoId __0, RepairActionDataRequest __1, PmcData __2,
        ref ItemEventRouterResponse __result)
    {
        if (__1.Target is not MongoId target || FindFullPart(__2, target) is not Item part)
            return true;

        __result = ServerLoad.EventOutput.GetOutput(__0);
        AddCurrentState(__result, __0, part, "kit");
        return false;
    }

    [HarmonyPatch(typeof(RepairController), nameof(RepairController.TraderRepair))]
    [HarmonyPrefix]
    private static bool Trader(MongoId __0, ref TraderRepairActionDataRequest __1, PmcData __2,
        ref ItemEventRouterResponse __result)
    {
        if (__1.RepairItems is not { Count: > 0 } requested)
            return true;

        var remaining = new List<RepairItem>(requested.Count);
        ItemEventRouterResponse? output = null;
        foreach (RepairItem entry in requested)
        {
            if (FindFullPart(__2, entry.Id) is Item part)
            {
                output ??= ServerLoad.EventOutput.GetOutput(__0);
                AddCurrentState(output, __0, part, "trader");
            }
            else
                remaining.Add(entry);
        }

        if (output == null)
            return true;
        if (remaining.Count > 0)
        {
            __1 = __1 with { RepairItems = remaining };
            return true;
        }

        __result = output;
        return false;
    }

    private static Item? FindFullPart(PmcData pmc, MongoId id)
    {
        Item? item = pmc.Inventory?.Items?.Find(candidate => candidate.Id == id);
        return item != null && ServerLoad.Parts.ContainsKey(item.Template) &&
            item.Upd?.Repairable is { Durability: double current, MaxDurability: double maximum } &&
            current >= maximum ? item : null;
    }

    private static void AddCurrentState(ItemEventRouterResponse output, MongoId sessionId, Item part, string source)
    {
        // The item-event router initializes both collections before calling the controller.
        var changedItems = output.ProfileChanges[sessionId].Items!.ChangedItems!;
        if (!changedItems.Any(item => item.Id == part.Id))
            changedItems.Add(part);

        var state = part.Upd!.Repairable!;
        ServerLoad.Log.Info($"[Component Wear] Repair skipped ({source}) {part.Id}: already at maximum " +
            $"{state.Durability:0.###}/{state.MaxDurability:0.###}; returned current state.");
    }
}

[HarmonyPatch(typeof(RepairHelper), nameof(RepairHelper.UpdateItemDurability))]
internal static class PartRepairWear
{
    internal const double MinimumLoss = 0.10;

    private static void Prefix(Item itemToRepair, TemplateItem itemToRepairDetails,
        double amountToRepair, bool useRepairKit, bool applyMaxDurabilityDegradation,
        out (double Current, double Maximum)? __state)
    {
        __state = null;
        if (ServerLoad.Parts.ContainsKey(itemToRepairDetails.Id) &&
            itemToRepair.Upd?.Repairable is { Durability: double current, MaxDurability: double maximum })
        {
            __state = (current, maximum);
            ServerLoad.Log.Info($"[Component Wear] Repair requested ({(useRepairKit ? "kit" : "trader")}) " +
                $"{itemToRepair.Id}: condition {current:0.###}/{maximum:0.###}, requested +{amountToRepair:0.###}, " +
                $"native degradation enabled={applyMaxDurabilityDegradation}.");
        }
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Item itemToRepair, bool useRepairKit,
        (double Current, double Maximum)? __state)
    {
        if (__state is not { } before ||
            itemToRepair.Upd?.Repairable is not { Durability: double current, MaxDurability: double maximum } state)
            return;

        if (current <= before.Current)
        {
            ServerLoad.Log.Info($"[Component Wear] Repair result ({(useRepairKit ? "kit" : "trader")}) " +
                $"{itemToRepair.Id}: no condition restored; {current:0.###}/{maximum:0.###}.");
            return;
        }

        // Preserve greater native degradation. Limit the minimum loss to the repair
        // performed, so topping up a small amount cannot lower the current condition.
        double minimumLoss = Math.Min(MinimumLoss, current - before.Current);
        double ceiling = Math.Min(maximum, Math.Max(1, before.Maximum - minimumLoss));
        state.MaxDurability = ceiling;
        state.Durability = Math.Min(current, ceiling);
        ServerLoad.Log.Info($"[Component Wear] Repair ({(useRepairKit ? "kit" : "trader")}) {itemToRepair.Id}: " +
            $"{before.Current:0.###}/{before.Maximum:0.###} -> {state.Durability:0.###}/{ceiling:0.###}; " +
            $"native result {current:0.###}/{maximum:0.###}; permanent loss {before.Maximum - ceiling:0.###}.");
    }
}

[HarmonyPatch(typeof(RepairService), "GetKitDivisor")]
internal static class PartKitPrice
{
    internal const double KitCostFactor = 0.15;

    private static void Postfix(TemplateItem itemToRepairDetails, ref double __result)
    {
        if (ServerLoad.Parts.ContainsKey(itemToRepairDetails.Id))
            __result *= KitCostFactor;
    }
}

[HarmonyPatch(typeof(RepairService), "AddBuffToItem")]
internal static class NoPartRepairEnhancements
{
    private static bool Prefix(RepairDetails repairDetails) =>
        repairDetails.RepairedItem == null ||
        !ServerLoad.Parts.ContainsKey(repairDetails.RepairedItem.Template);
}

[HarmonyPatch(typeof(RagfairOfferService), nameof(RagfairOfferService.AddOffer))]
internal static class UsedFleaParts
{
    private static int _loggedOffers;
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(RagfairOffer offer)
    {
        if (!ServerLoad.Settings.UsedFleaParts || offer.User == null ||
            offer.User.MemberType == MemberCategory.Trader ||
            ServerLoad.Saves.GetProfiles().ContainsKey(offer.User.Id) ||
            offer.Items == null || offer.Items.Count == 0)
            return;

        Item root = offer.Items[0];
        foreach (Item part in offer.Items)
        {
            if (!ServerLoad.Parts.ContainsKey(part.Template) || part.Upd?.Repairable != null)
                continue;

            double chance = Math.Clamp(ServerLoad.Settings.UsedOfferChance, 0, 1);
            if (part != root && root.Upd?.Repairable?.Durability is double rootWear)
                chance = Math.Max(chance, (100 - rootWear) / 100);
            if (Random.Shared.NextDouble() >= chance)
                continue;

            double max = Random.Shared.Next(82, 101);
            double current = Math.Min(max, Random.Shared.Next(55, 96));
            ServerLoad.SetCondition(part, current, max);
            if (Interlocked.Increment(ref _loggedOffers) == 1)
                ServerLoad.Log.Info($"[Component Wear] Used Flea part generated: {part.Template}, {current:0.##}/{max:0.##}.");
        }
    }
}

[HarmonyPatch(typeof(BotInventoryGenerator), nameof(BotInventoryGenerator.GenerateInventory))]
internal static class SpawnPartCondition
{
    private static int _loggedBots;
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(BotBaseInventory __result)
    {
        if (!ServerLoad.Settings.BotPartCondition || __result?.Items == null)
            return;

        var byId = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (Item item in __result.Items)
            byId.TryAdd(item.Id.ToString(), item);
        foreach (Item part in __result.Items)
        {
            if (!ServerLoad.Parts.ContainsKey(part.Template) || part.Upd?.Repairable != null)
                continue;

            Item root = part;
            for (int depth = 0; depth < 24 && root.ParentId is string parent &&
                byId.TryGetValue(parent, out Item? ancestor); depth++)
            {
                root = ancestor;
                if (root.Upd?.Repairable?.Durability is not double baseCurrent ||
                    ServerLoad.Parts.ContainsKey(root.Template))
                    continue;

                double baseMax = root.Upd.Repairable.MaxDurability ?? 100;
                double max = Math.Clamp(baseMax + Random.Shared.Next(-5, 6), 1, 100);
                double current = Math.Clamp(baseCurrent + Random.Shared.Next(-8, 9), 1, max);
                ServerLoad.SetCondition(part, current, max);
                if (Interlocked.Increment(ref _loggedBots) == 1)
                    ServerLoad.Log.Info($"[Component Wear] Bot part generated: {part.Template}, {current:0.##}/{max:0.##}; base weapon {baseCurrent:0.##}/{baseMax:0.##}.");
                break;
            }
        }
    }
}

internal static class RaidConditionSync
{
    private static readonly ConcurrentDictionary<MongoId, long> RaidStarts = new();

    [HarmonyPatch(typeof(MatchController), nameof(MatchController.StartLocalRaidAsync))]
    internal static class RaidStart
    {
        private static void Prefix(MongoId __0) => RaidStarts[__0] = DateTime.UtcNow.Ticks;
    }

    [HarmonyPatch(typeof(MatchController), nameof(MatchController.EndLocalRaidAsync))]
    internal static class RaidEnd
    {
        private static void Prefix(MongoId __0, EndLocalRaidRequestData __1) => End(__0, __1);
    }

    private static void End(MongoId __0, EndLocalRaidRequestData __1)
    {
        if (!RaidStarts.TryRemove(__0, out long started) || !File.Exists(ServerLoad.Journal))
            return;

        try
        {
            var journal = new ConditionStore(ServerLoad.Journal);
            int applied = Apply(__1.Results?.Profile?.Inventory?.Items, journal, started);
            applied += Apply(__1.LostInsuredItems, journal, started);
            if (__1.TransferItems != null)
                foreach (var transfer in __1.TransferItems.Values)
                    applied += Apply(transfer, journal, started);
            if (applied > 0)
                ServerLoad.Log.Info($"[Component Wear] End raid: merged {applied} worn parts into native item data.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ServerLoad.Log.Warning($"[Component Wear] Raid state could not be read: {exception.Message}");
        }
    }

    private static int Apply(IEnumerable<Item>? items, ConditionStore journal, long since)
    {
        int applied = 0;
        foreach (Item item in items ?? [])
        {
            if (!ServerLoad.Parts.ContainsKey(item.Template) ||
                !journal.TryGetState(item.Id.ToString(), out PartCondition state) || state.UpdatedAt < since)
                continue;

            // Both paths describe the same item. If the vanilla payload contains the wear, keep the lower value.
            double maximum = Math.Min(state.Maximum, item.Upd?.Repairable?.MaxDurability ?? 100);
            double current = Math.Min(state.Current, item.Upd?.Repairable?.Durability ?? 100);
            ServerLoad.SetCondition(item, current, maximum);
            applied++;
        }
        return applied;
    }
}



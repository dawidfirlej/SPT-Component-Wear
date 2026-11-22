using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace ComponentWear;

[BepInPlugin("local.component.wear", "Component Wear", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; }

    internal static ConditionStore Conditions { get; private set; }
    internal static ConditionStore RaidConditions { get; private set; }
    private float _nextFlush;
    private static bool _loggedSave;
    private Harmony _pendingRepairHarmony;

    private void Awake()
    {
        Log = Logger;
        if (!Config.Bind("Prototype", "Enabled", false,
                "Enable the prototype for the explicitly listed test items.").Value)
            return;

        string entries = Config.Bind("Prototype", "TestItems", string.Empty,
            "Comma-separated test item IDs and starting condition, as id:condition.").Value;
        Wear.LoadTestItems(entries);

        var harmony = new Harmony("local.component.wear");
        try
        {
            Conditions = new ConditionStore(Path.Combine(BepInEx.Paths.ConfigPath, "local.component.wear.condition"));
            RaidConditions = new ConditionStore(Path.Combine(BepInEx.Paths.ConfigPath, "local.component.wear.raid"));
            Logger.LogInfo($"[Save] Loaded {Conditions.Count} legacy and {RaidConditions.Count} raid conditions.");
            harmony.CreateClassProcessor(typeof(AddRepairableComponent)).Patch();
            harmony.CreateClassProcessor(typeof(ReadPartCondition)).Patch();
            harmony.CreateClassProcessor(typeof(WritePartCondition)).Patch();
            harmony.CreateClassProcessor(typeof(WearOnShot)).Patch();
            harmony.CreateClassProcessor(typeof(ApplySpread)).Patch();
            harmony.CreateClassProcessor(typeof(MuzzleAccuracy)).Patch();
            harmony.CreateClassProcessor(typeof(MuzzleRecoil)).Patch();
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            Logger.LogError(exception);
            return;
        }

        TryPatch(() => harmony.CreateClassProcessor(typeof(ShowConditionInInspect)).Patch(), "Inspect bar");
        TryPatch(() => ApplySuppressorVolume.Install(harmony), "shot audio");
        TryPatch(() => harmony.CreateClassProcessor(typeof(PartMalfunctions)).Patch(), "part malfunctions");
        _pendingRepairHarmony = harmony;
        Logger.LogInfo($"Ready for server-registered part templates; {Wear.TestItemCount} legacy test IDs.");
    }

    internal static void TryPatch(Action patch, string feature)
    {
        try
        {
            patch();
        }
        catch (Exception exception)
        {
            Log.LogError($"[{feature}] {exception}");
        }
    }

    private void Update()
    {
        InstallRepairPatchesWhenReady();
        if ((Conditions?.IsDirty != true && RaidConditions?.IsDirty != true) || UnityEngine.Time.unscaledTime < _nextFlush)
            return;

        _nextFlush = UnityEngine.Time.unscaledTime + 1f;
        FlushConditions();
    }

    private void InstallRepairPatchesWhenReady()
    {
        Harmony harmony = _pendingRepairHarmony;
        if (harmony == null)
            return;

        // Harmony can run the target's static constructor while patching it.
        // RepairController reads these settings in that constructor.
        GlobalConfiguration configuration = Comfort.Common.Singleton<GlobalConfiguration>.Instance;
        var skills = configuration?.SkillsSettings;
        if (configuration?.RepairSettings == null || skills?.Intellect == null ||
            skills.WeaponTreatment == null || skills.LightVests == null || skills.HeavyVests == null)
            return;

        _pendingRepairHarmony = null;
        Logger.LogInfo("[Repair] Game configuration ready; installing part repair hooks.");
        TryPatch(() => harmony.CreateClassProcessor(typeof(PartKitPrice)).Patch(), "part kit price");
        TryPatch(() => harmony.CreateClassProcessor(typeof(UseFirearmRepairParameters)).Patch(), "part repair parameters");
        TryPatch(() => harmony.CreateClassProcessor(typeof(NoPartRepairBuffs)).Patch(), "part repair buffs");
    }

    private void OnApplicationQuit() => FlushConditions();

    private static void FlushConditions()
    {
        if (Conditions?.IsDirty != true && RaidConditions?.IsDirty != true)
            return;

        try
        {
            Conditions.Flush();
            RaidConditions.Flush();
            if (!_loggedSave)
            {
                _loggedSave = true;
                Log.LogInfo($"[Save] Committed {Conditions.Count} legacy and {RaidConditions.Count} raid conditions.");
            }
        }
        catch (IOException exception)
        {
            Log.LogWarning($"[Save] Write failed and will be retried: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.LogWarning($"[Save] Write failed and will be retried: {exception.Message}");
        }
    }
}

internal enum Part
{
    None, Barrel, Upper, Suppressor, FlashHider, MuzzleDevice
}

internal static class Wear
{
    private static readonly Dictionary<string, int> TestItems = new(StringComparer.Ordinal);

    internal static int TestItemCount => TestItems.Count;

    internal static void LoadTestItems(string entries)
    {
        foreach (string entry in entries.Split(','))
        {
            string[] pair = entry.Trim().Split(':');
            if (pair.Length == 2 && pair[0].Length == 24 && pair[0].All(Uri.IsHexDigit)
                && int.TryParse(pair[1], out int condition) && condition >= 0 && condition <= 100)
                TestItems[pair[0]] = condition;
        }

    }

    internal static bool TryGetTestCondition(Mod mod, out int condition) =>
        TestItems.TryGetValue(mod.Id.ToString(), out condition);

    internal const int RepairMarker = 23;

    internal static bool IsNativePart(Mod mod) => mod.RepairSpeed == RepairMarker && Classify(mod) != Part.None;

    internal static bool Tracks(Mod mod) => IsNativePart(mod) || TryGetTestCondition(mod, out _);

    internal static Part Classify(Mod mod) => mod switch
    {
        Barrel => Part.Barrel,
        Silencer => Part.Suppressor,
        FlashHider => Part.FlashHider,
        MuzzleMod => Part.MuzzleDevice,
        Receiver receiver when receiver.AllSlots.Any(slot =>
            string.Equals(slot.ID, "mod_barrel", StringComparison.OrdinalIgnoreCase)) => Part.Upper,
        _ => Part.None
    };

    internal static float LossFraction(RepairableComponent component) =>
        1f - Math.Clamp(component.Durability / Math.Max(1f, component.TemplateDurability), 0f, 1f);

    internal static float WorstLoss(Weapon weapon, Part part)
    {
        float loss = 0f;
        foreach (Mod mod in weapon.Mods)
        {
            if (Tracks(mod) && Classify(mod) == part &&
                mod.TryGetItemComponent(out RepairableComponent component))
                loss = Math.Max(loss, LossFraction(component));
        }
        return loss;
    }

    internal sealed class Template : IRepairableComponentTemplate
    {
        internal static readonly Template Instance = new();
        public int Durability => 100;
        public int MaxDurability => 100;
        public float MinRepairDegradation => 0f;
        public float MaxRepairDegradation => 0.03f;
        public float MinRepairKitDegradation => 0f;
        public float MaxRepairKitDegradation => 0.025f;
    }
}

[HarmonyPatch(typeof(Mod), MethodType.Constructor, typeof(string), typeof(ModTemplate))]
internal static class AddRepairableComponent
{
    private static void Postfix(Mod __instance) => Ensure(__instance, "constructor");

    internal static RepairableComponent Ensure(Mod mod, string source)
    {
        bool native = Wear.IsNativePart(mod);
        bool test = Wear.TryGetTestCondition(mod, out int condition);
        if (!native && !test)
            return null;

        if (mod.TryGetItemComponent(out RepairableComponent existing))
            return existing;

        Part part = Wear.Classify(mod);
        if (part == Part.None)
        {
            Plugin.Log.LogWarning($"[{source}] Unrecognized test part {mod.Id}, template={mod.StringTemplateId}, type={mod.GetType().Name}.");
            return null;
        }

        var component = new RepairableComponent(mod, Wear.Template.Instance);
        bool restored = false;
        if (!native)
        {
            restored = Plugin.Conditions.TryGetState(mod.Id, out PartCondition persisted);
            component.Durability = restored ? persisted.Current : condition;
            component.MaxDurability = restored ? persisted.Maximum : 100f;
        }
        mod.Components.Add(component);
        if (test)
            Plugin.Log.LogInfo($"[{source}] Added {part} {mod.Id} at {component.Durability:0.###}/{component.MaxDurability:0.###} (native={native}, legacy restored={restored}).");
        return component;
    }
}

[HarmonyPatch(typeof(ItemDeserializer), "CreateItem")]
internal static class ReadPartCondition
{
    private static readonly Dictionary<string, (float Current, float Maximum)> Logged = new(StringComparer.Ordinal);

    private static void Prefix(Item item, ref UnparsedData properties)
    {
        if (item is not Mod mod || !Wear.IsNativePart(mod) ||
            properties?.JToken is not JObject data || data["Repairable"] is not JObject condition)
            return;

        RepairableComponent component = AddRepairableComponent.Ensure(mod, "native upd");
        if (component == null)
            return;

        // Vanilla discovers native components from declared fields on the item class.
        // Our repairable component is attached to Components, so apply it before handing off the other fields.
        float maximum = ReadNumber(condition["MaxDurability"], component.MaxDurability);
        float current = ReadNumber(condition["Durability"], component.Durability);
        component.MaxDurability = Math.Clamp(maximum, 1f, 100f);
        component.Durability = Math.Clamp(current, 0f, component.MaxDurability);

        var remaining = (JObject)data.DeepClone();
        remaining.Remove("Repairable");
        properties = new UnparsedData { JToken = remaining };

        if (Plugin.RaidConditions.TryGetState(mod.Id, out _))
        {
            var loaded = (component.Durability, component.MaxDurability);
            if (!Logged.TryGetValue(mod.Id, out var previous) || previous != loaded)
            {
                Logged[mod.Id] = loaded;
                Plugin.Log.LogInfo($"[Save] Restored native condition {mod.Id}: {loaded.Durability:0.###}/{loaded.MaxDurability:0.###}.");
            }
        }
    }

    private static float ReadNumber(JToken token, float fallback)
    {
        if (token?.Type != JTokenType.Float && token?.Type != JTokenType.Integer)
            return fallback;

        float value = token.Value<float>();
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }
}

[HarmonyPatch(typeof(ItemDeserializer), "GetItemProperties")]
internal static class WritePartCondition
{
    private static void Postfix(Item item, Dictionary<string, object> __result)
    {
        if (item is not Mod mod || !Wear.IsNativePart(mod) ||
            !mod.TryGetItemComponent(out RepairableComponent component))
            return;

        __result["Repairable"] = new { component.Durability, component.MaxDurability };
    }
}

[HarmonyPatch(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.method_1))]
internal static class ShowConditionInInspect
{
    private static readonly AccessTools.FieldRef<UIElement, UIParent> Ui =
        AccessTools.FieldRefAccess<UIElement, UIParent>("UI");

    private static void Postfix(ItemSpecificationPanel __instance, Item ____item,
        CharacteristicPanel ____charDurability, SimpleTooltip ____secondaryTooltip)
    {
        if (____item is not Mod mod || !Wear.Tracks(mod) ||
            !__instance.Examined || ____charDurability == null)
            return;

        RepairableComponent component = AddRepairableComponent.Ensure(mod, "inspect fallback");
        if (component == null)
            return;

        ItemAttribute durability = mod.Attributes.FirstOrDefault(attribute =>
            attribute is RangedItemAttribute && Equals(attribute.Id, EItemAttributeId.Durability));
        if (durability == null)
        {
            Plugin.Log.LogWarning($"[Inspect] No native durability attribute for {mod.Id}.");
            return;
        }

        Ui(__instance).AddDisposable(____charDurability);
        ____charDurability.Show(durability, ____secondaryTooltip, true, component.TemplateDurability);
        ____charDurability.gameObject.SetActive(true);
        mod.UpdateAttributes();
        Plugin.Log.LogInfo($"[Inspect] Native bar {Wear.Classify(mod)} {mod.Id}: {component.Durability:0.##}/{component.MaxDurability:0.##}.");
    }
}

[HarmonyPatch(typeof(Weapon), nameof(Weapon.OnShot))]
internal static class WearOnShot
{
    private static readonly Dictionary<string, int> ShotCounts = new(StringComparer.Ordinal);

    private static readonly Random HeatRandom = new();

    private static void Prefix(Weapon __instance, out float __state) =>
        __state = __instance.Repairable.MaxDurability;

    private static void Postfix(Weapon __instance, float ammoBurnRatio,
        float skillWeaponTreatmentFactor, GlobalConfiguration.OverheatSettings overheatSettings, float __state)
    {

        if (!__instance.Mods.Any(mod => Wear.Tracks(mod)))
            return;

        float heat = __instance.MalfState.LastShotOverheat;
        float heatProgress = Math.Clamp(heat, overheatSettings.OverheatProblemsStart, overheatSettings.MaxOverheat) / 100f - 1f;
        float heatMultiplier = UnityEngine.Mathf.Lerp(overheatSettings.DurReduceMinMult,
            overheatSettings.DurReduceMaxMult, heatProgress);
        float shotWear = __instance.GetDurabilityLossOnShot(ammoBurnRatio,
            heatMultiplier, skillWeaponTreatmentFactor, out _);
        if (!(shotWear > 0f) || float.IsInfinity(shotWear))
            return;

        float maxLoss = 0f;
        if (__instance.AllowOverheat && heat >= overheatSettings.OverheatProblemsStart)
        {
            // Reuse the game's actual roll when the weapon also lost maximum durability.
            maxLoss = Math.Max(0f, __state - __instance.Repairable.MaxDurability);
            if (maxLoss == 0f)
            {
                maxLoss = heat >= overheatSettings.MaxOverheat
                    ? UnityEngine.Mathf.Lerp(overheatSettings.MinWearOnMaxOverheat,
                        overheatSettings.MaxWearOnMaxOverheat, (float)HeatRandom.NextDouble())
                    : UnityEngine.Mathf.Lerp(overheatSettings.MinWearOnOverheat,
                        overheatSettings.MaxWearOnOverheat, heatProgress);
            }
        }
        float wearFloor = Math.Clamp(overheatSettings.OverheatWearLimit * 100f, 1f, 100f);

        ShotCounts.TryGetValue(__instance.Id, out int shots);
        ShotCounts[__instance.Id] = ++shots;
        bool logShot = shots == 1 || shots % 30 == 0;
        foreach (Mod mod in __instance.Mods)
        {
            if (!Wear.Tracks(mod))
                continue;

            float rate = Wear.Classify(mod) switch
            {
                Part.Barrel => 0.65f,
                Part.Upper => 0.35f,
                Part.Suppressor => 0.55f,
                Part.FlashHider => 0.25f,
                Part.MuzzleDevice => 0.25f,
                _ => 0f
            };
            if (rate > 0f && mod.TryGetItemComponent(out RepairableComponent component))
            {
                float before = component.Durability;
                float beforeMax = component.MaxDurability;
                component.Durability = Math.Max(0f, before - shotWear * rate);
                float loss = maxLoss * rate;
                if (loss > 0f && beforeMax > wearFloor && component.Durability < beforeMax - loss)
                    component.MaxDurability = Math.Max(wearFloor, beforeMax - loss);
                component.Durability = Math.Min(component.Durability, component.MaxDurability);

                bool native = Wear.IsNativePart(mod);
                (native ? Plugin.RaidConditions : Plugin.Conditions)
                    .Record(mod.Id, component.Durability, component.MaxDurability);
                if (logShot && Wear.TryGetTestCondition(mod, out _))
                    Plugin.Log.LogInfo($"[Shot {shots}] {Wear.Classify(mod)} {mod.Id}: {before:0.000}/{beforeMax:0.000} -> {component.Durability:0.000}/{component.MaxDurability:0.000}, heat {heat:0.0}.");
            }
        }
    }
}

[HarmonyPatch(typeof(Weapon), nameof(Weapon.GetBarrelDeviation), new Type[0])]
internal static class ApplySpread
{
    private static void Postfix(Weapon __instance, ref float __result)
    {
        float barrel = Wear.WorstLoss(__instance, Part.Barrel);
        __result *= 1f + 0.30f * barrel;
    }
}

internal static class PartBalance
{
    internal const float UnattenuatedThreshold = 0.15f;
    internal const float MaxPartMalfChance = 0.0025f;

    internal static float SuppressorGain(float loss) =>
        (float)Math.Pow(10.0, loss * loss); // 20 dB of gain at total wear, before playback limits.

    internal static float ExtraMalfChance(float durability, float upperLoss, float barrelLoss, float protectedThreshold)
    {
        float points = upperLoss * 14f + barrelLoss * 6f;
        float headroom = Math.Max(0f, durability - protectedThreshold);
        return MaxPartMalfChance * Math.Clamp((points - headroom) / 13f, 0f, 1f);
    }

    internal static float LostMuzzleBonus(Weapon weapon, bool recoil)
    {
        float lost = 0f;
        foreach (Mod mod in weapon.Mods)
        {
            if (mod is not MuzzleMod || !Wear.Tracks(mod) ||
                !mod.TryGetItemComponent(out RepairableComponent component))
                continue;

            float bonus = recoil ? -mod.Template.Recoil : mod.Accuracy;
            lost += Math.Max(0f, bonus) * Wear.LossFraction(component) * 0.25f;
        }
        return Math.Min(lost / 100f, recoil ? 0.025f : 0.020f);
    }
}

[HarmonyPatch(typeof(Weapon), "get_CenterOfImpactDelta")]
internal static class MuzzleAccuracy
{
    private static void Postfix(Weapon __instance, ref float __result) =>
        __result += PartBalance.LostMuzzleBonus(__instance, recoil: false);
}

[HarmonyPatch(typeof(Weapon), "get_RecoilDelta")]
internal static class MuzzleRecoil
{
    private static void Postfix(Weapon __instance, ref float __result) =>
        __result += PartBalance.LostMuzzleBonus(__instance, recoil: true);
}

[HarmonyPatch(typeof(Player.FirearmController), nameof(Player.FirearmController.GetTotalMalfunctionChance))]
internal static class PartMalfunctions
{
    private static readonly HashSet<string> LoggedWeapons = new(StringComparer.Ordinal);

    private static void Postfix(Player.FirearmController __instance, ref double durabilityMalfChance,
        ref float weaponDurability, ref float __result)
    {
        Weapon weapon = __instance.Item;
        if (!weapon.AllowMalfunction || !weapon.Mods.Any(mod => Wear.Tracks(mod)))
            return;

        float upper = Wear.WorstLoss(weapon, Part.Upper);
        float barrel = Wear.WorstLoss(weapon, Part.Barrel);
        float threshold = Comfort.Common.Singleton<GlobalConfiguration>.Instance.Malfunction.DurRangeToIgnoreMalfs.x;
        float extra = PartBalance.ExtraMalfChance(weaponDurability, upper, barrel, threshold);
        extra *= (float)Math.Clamp(weapon.Buff.MalfunctionProtections, 0.0, 1.0);
        float previous = __result;
        __result = Math.Min(1f, previous + extra);
        durabilityMalfChance += __result - previous;

        if (weapon.Mods.Any(mod => Wear.TryGetTestCondition(mod, out _)) &&
            LoggedWeapons.Add(weapon.Id + (extra > 0f ? ":extra" : ":baseline")))
            Plugin.Log.LogInfo($"[Malfunction] {weapon.Id}: base condition {weaponDurability:0.##}, upper wear {upper:P1}, barrel wear {barrel:P1}, probability {previous:P3} -> {__result:P3}.");
    }
}

internal static class ApplySuppressorVolume
{
    private static readonly AccessTools.FieldRef<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>
        Bridge = AccessTools.FieldRefAccess<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>("playersBridge");
    private static readonly HashSet<string> LoggedAudioWeapons = new(StringComparer.Ordinal);

    internal static void Install(Harmony harmony)
    {
        Patch(harmony, nameof(VanillaTranspiler),
            AccessTools.Method(typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.FireBullet)), "vanilla");
        const string prefix = "SoundEngineBackport.";
        Type live = AccessTools.TypeByName(prefix + "LiveEngine.LiveWeaponSoundEngine");
        Type legacy = AccessTools.TypeByName(prefix + "WeaponSoundPlayerPatches");
        if (live != null)
            Patch(harmony, nameof(LiveTranspiler), AccessTools.Method(live, "FireBullet"), "SEB Live");
        if (legacy != null)
        {
            foreach (string name in new[] { "FireBulletV1", "EnqueueOneShot", "StopLoopV1", "ScheduleLoopTail" })
                Patch(harmony, nameof(LegacyTranspiler), AccessTools.Method(legacy, name), "SEB " + name);
        }
    }

    private static void Patch(Harmony harmony, string transpiler, MethodInfo target, string path)
    {
        Plugin.TryPatch(() =>
        {
            if (target == null)
                throw new MissingMethodException(path);
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(ApplySuppressorVolume), transpiler));
            Plugin.Log.LogInfo($"[Audio] Hooked {path}.");
        }, "Audio " + path);
    }

    private static IEnumerable<CodeInstruction> LiveTranspiler(IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var owner = AccessTools.Field(__originalMethod.DeclaringType, "_owner");
        int settings = code.FindIndex(instruction => instruction.operand is MethodInfo method &&
            method.DeclaringType?.FullName == "SoundEngineBackport.LiveEngine.LiveWeaponAudioSettings" &&
            method.Name == "Get");
        if (owner?.FieldType != typeof(WeaponSoundPlayer) || settings < 2 ||
            (code[settings - 1].opcode != OpCodes.Ldloca && code[settings - 1].opcode != OpCodes.Ldloca_S) ||
            code[settings].operand is not MethodInfo get ||
            get.GetParameters().Last().Name != "volume" || __originalMethod.GetParameters()[4].Name != "multiShot")
            throw new InvalidOperationException("Unexpected SEB Live volume layout.");

        var silenced = AccessTools.PropertyGetter(typeof(BaseSoundPlayer), nameof(BaseSoundPlayer.IsSilenced));
        int selector = code.FindIndex(instruction => instruction.Calls(silenced));
        if (selector < 0 || code.Count(instruction => instruction.Calls(silenced)) != 1)
            throw new InvalidOperationException("Unexpected SEB Live bank selector.");

        // Scale the resolved base volume before it is copied into either playback context.
        code.InsertRange(settings + 1, new[]
        {
            new CodeInstruction(code[settings - 1].opcode, code[settings - 1].operand),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, owner),
            new CodeInstruction(OpCodes.Ldarg_S, (byte)5),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApplySuppressorVolume), nameof(ScaleLiveVolume)))
        });
        // Retain labels on the first instruction of the replacement.
        code[selector].opcode = OpCodes.Ldarg_S;
        code[selector].operand = (byte)5;
        code.Insert(selector + 1, new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(ApplySuppressorVolume), nameof(SelectSuppressedBank))));
        return code;
    }

    private static IEnumerable<CodeInstruction> VanillaTranspiler(IEnumerable<CodeInstruction> instructions) =>
        ScaleSoundBankReads(instructions, nameof(ScaleVanillaVolume));

    private static IEnumerable<CodeInstruction> LegacyTranspiler(IEnumerable<CodeInstruction> instructions) =>
        ScaleSoundBankReads(instructions, nameof(ScaleLegacyVolume));

    private static IEnumerable<CodeInstruction> ScaleSoundBankReads(IEnumerable<CodeInstruction> instructions,
        string helper)
    {
        var code = instructions.ToList();
        var volume = AccessTools.Field(typeof(SoundBank), nameof(SoundBank.BaseVolume));
        var apply = AccessTools.Method(typeof(ApplySuppressorVolume), helper);
        if (!code.Any(instruction => instruction.LoadsField(volume)))
            throw new InvalidOperationException("Missing sound bank volume read.");

        foreach (var instruction in code)
        {
            yield return instruction;
            if (instruction.LoadsField(volume))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, apply);
            }
        }
    }

    private static bool SelectSuppressedBank(WeaponSoundPlayer player, bool multiShot)
    {
        if (!player.IsSilenced)
            return false;
        if (!TryGetSuppressor(player, out Mod mod, out float loss) ||
            !CanPlayUnattenuated(player, multiShot, loss))
            return true;

        if (Wear.TryGetTestCondition(mod, out _) && LoggedAudioWeapons.Add("SEB Live dry:" + mod.Id))
            Plugin.Log.LogInfo($"[Audio] SEB Live {mod.Id}: suppressor wear {loss:P1}; selected unsuppressed shot bank.");
        return false;
    }

    private static bool CanPlayUnattenuated(WeaponSoundPlayer player, bool multiShot, float loss) =>
        loss >= 1f - PartBalance.UnattenuatedThreshold && (multiShot ? player.Doublet : player.Body) != null;

    private static bool TryGetSuppressor(WeaponSoundPlayer player, out Mod suppressor, out float loss)
    {
        suppressor = null;
        loss = 0f;
        if (player == null || !player.IsSilenced || Bridge(player)?.Weapon?.Item is not Weapon weapon)
            return false;

        foreach (Mod mod in weapon.Mods)
        {
            if (mod is not Silencer || !Wear.Tracks(mod) ||
                !mod.TryGetItemComponent(out RepairableComponent component))
                continue;

            float candidate = Wear.LossFraction(component);
            if (suppressor == null || candidate > loss)
            {
                suppressor = mod;
                loss = candidate;
            }
        }
        return suppressor != null;
    }

    private static void ScaleLiveVolume(ref float volume, WeaponSoundPlayer player, bool multiShot)
    {
        if (TryGetSuppressor(player, out Mod mod, out float loss) &&
            !CanPlayUnattenuated(player, multiShot, loss))
            volume = ScaleVolume(volume, mod, loss, "SEB Live");
    }

    private static float ScaleVanillaVolume(float volume, WeaponSoundPlayer player) =>
        TryGetSuppressor(player, out Mod mod, out float loss) ? ScaleVolume(volume, mod, loss, "vanilla") : volume;

    private static float ScaleLegacyVolume(float volume, WeaponSoundPlayer player) =>
        TryGetSuppressor(player, out Mod mod, out float loss) ? ScaleVolume(volume, mod, loss, "SEB legacy") : volume;

    private static float ScaleVolume(float volume, Mod mod, float loss, string path)
    {
        float multiplier = PartBalance.SuppressorGain(loss);
        float adjusted = volume * multiplier;
        if (Wear.TryGetTestCondition(mod, out _) && LoggedAudioWeapons.Add(path + ":" + mod.Id))
            Plugin.Log.LogInfo($"[Audio] {path} {mod.Id}: wear {loss:P1}; volume {volume:0.000} -> {adjusted:0.000} (x{multiplier:0.000}).");
        return adjusted;
    }
}


[HarmonyPatch(typeof(EFT.Repairing.RepairController), nameof(EFT.Repairing.RepairController.GetRepairPrice))]
internal static class PartKitPrice
{
    private static void Postfix(Item __2, ref double __result)
    {
        if (__2 is Mod mod && Wear.IsNativePart(mod))
            __result *= 0.15;
    }
}

[HarmonyPatch(typeof(EFT.Repairing.RepairController), "HasRepairBonus")]
internal static class UseFirearmRepairParameters
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var check = AccessTools.Method(typeof(ItemTemplate), "IsChildOf", new[] { typeof(string) });
        int index = code.FindIndex(instruction => instruction.Calls(check));
        if (index < 1 || !Equals(code[index - 1].operand, "5422acb9af1c889c16000029"))
            throw new InvalidOperationException("Unexpected firearm repair category check.");

        code[index].opcode = OpCodes.Call;
        code[index].operand = AccessTools.Method(typeof(UseFirearmRepairParameters), nameof(IsFirearmOrPart));
        return code;
    }

    private static bool IsFirearmOrPart(ItemTemplate template, string category) =>
        (template is ModTemplate && template.RepairSpeed == Wear.RepairMarker) || template.IsChildOf(category);
}

[HarmonyPatch(typeof(EFT.Repairing.RepairController), nameof(EFT.Repairing.RepairController.TryGetEnhancementChance))]
internal static class NoPartRepairBuffs
{
    private static bool Prefix(Item __0, ref double __2, ref bool __result)
    {
        if (__0 is not Mod mod || !Wear.IsNativePart(mod))
            return true;
        __2 = 0;
        __result = false;
        return false;
    }
}

using System;
using System.IO;
using BepInEx.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ComponentWear;

internal sealed class ClientSettings
{
    private const string Categories = "ExtraCategories";
    private const string Restart = "Restart the SPT server and game to apply changes.";
    private readonly string _path;
    private readonly ConfigFile _menu;
    private readonly JObject _initial;

    internal static void Bind(ConfigFile menu, string gameRoot)
    {
        string path = Path.Combine(gameRoot, "SPT_Runtime", "user", "mods", "ComponentWear", "config.json");
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            Plugin.Log.LogInfo("[Settings] Open the server's Component Wear config to change categories.");
            return;
        }

        new ClientSettings(path, menu).BindEntries();
    }

    private ClientSettings(string path, ConfigFile menu)
    {
        _path = path;
        _menu = menu;
        _initial = Read(path);
    }

    private void BindEntries()
    {
        const string partSection = "Extra part categories";
        const string generationSection = "Extra parts on the Flea Market and bots";
        foreach (ExtraPart part in Enum.GetValues(typeof(ExtraPart)))
        {
            if (part != ExtraPart.None)
                BindToggle(partSection, part.ToString(), false,
                    "Enable condition and repairs for this type of part.", Categories);
        }

        BindToggle(generationSection, "ExtraUsedFleaParts", true, "Allow used offers for enabled extra categories.");
        BindChance(generationSection, "ExtraUsedOfferChance", 25f, "Chance of a used extra part in a generated offer.");
        BindToggle(generationSection, "ExtraBotPartCondition", true, "Randomize condition of enabled extra parts on bots.");
        BindChance(generationSection, "ExtraBotWearChance", 65f, "Chance that an eligible extra part on a bot spawns worn.");
    }

    private void BindToggle(string section, string name, bool fallback, string description, string group = null)
    {
        JObject values = group == null ? _initial : Find(_initial, group) as JObject;
        bool value = Find(values, name) is JToken token && token.Type == JTokenType.Boolean
            ? token.Value<bool>() : fallback;
        BindEntry(section, name, fallback, value, new ConfigDescription($"{description} {Restart}"),
            enabled => new JValue(enabled), group);
    }

    private void BindChance(string section, string name, float fallback, string description)
    {
        JToken token = Find(_initial, name);
        float percent = token?.Type is JTokenType.Integer or JTokenType.Float
            ? Math.Clamp(token.Value<float>() * 100f, 0f, 100f) : fallback;
        BindEntry(section, name, fallback, percent,
            new ConfigDescription($"{description} Percent (0–100). {Restart}", new AcceptableValueRange<float>(0f, 100f)),
            value => new JValue((double)value / 100d));
    }

    private void BindEntry<T>(string section, string name, T fallback, T serverValue, ConfigDescription description,
        Func<T, JValue> serialize, string group = null)
    {
        ConfigEntry<T> entry = _menu.Bind(section, name, fallback, description);
        entry.Value = serverValue;
        bool restoring = false;
        entry.SettingChanged += (_, _) =>
        {
            if (restoring)
                return;
            try
            {
                Write(_path, group, name, serialize(entry.Value));
                serverValue = entry.Value;
                Plugin.Log.LogInfo($"[Settings] Saved {name} = {entry.Value}. {Restart}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                restoring = true;
                try { entry.Value = serverValue; }
                finally { restoring = false; }
                Plugin.Log.LogError($"[Settings] Could not save {name}: {exception.Message}");
            }
        };
    }

    private static JObject Read(string path) => File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();

    private static JToken Find(JObject values, string name) =>
        values?.Property(name, StringComparison.OrdinalIgnoreCase)?.Value;

    private static void Write(string path, string group, string name, JValue value)
    {
        JObject root = Read(path);
        JObject target = root;
        if (group != null)
        {
            JProperty section = root.Property(group, StringComparison.OrdinalIgnoreCase);
            target = section?.Value as JObject ?? new JObject();
            if (section == null)
                root.Add(group, target);
            else if (!ReferenceEquals(section.Value, target))
                section.Value = target;
        }

        JProperty setting = target.Property(name, StringComparison.OrdinalIgnoreCase);
        if (setting == null)
            target.Add(name, value);
        else
            setting.Value = value;

        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, root.ToString(Formatting.Indented) + Environment.NewLine);
            if (File.Exists(path))
                File.Replace(pending, path, null);
            else
                File.Move(pending, path);
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}

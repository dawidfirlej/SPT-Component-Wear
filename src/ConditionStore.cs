using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ComponentWear;

internal readonly struct PartCondition
{
    internal PartCondition(float current, float maximum, long updatedAt)
    {
        Current = current;
        Maximum = maximum;
        UpdatedAt = updatedAt;
    }

    internal float Current { get; }
    internal float Maximum { get; }
    internal long UpdatedAt { get; }
}

internal sealed class ConditionStore
{
    private readonly string _path;
    private readonly Dictionary<string, PartCondition> _values = new(StringComparer.Ordinal);

    internal ConditionStore(string path)
    {
        _path = path;
        if (!File.Exists(path))
            return;

        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0 || (lines[0] != "ComponentWear 1" && lines[0] != "ComponentWear 2"))
            throw new InvalidDataException("Unrecognized condition store format.");

        bool legacy = lines[0] == "ComponentWear 1";
        foreach (string line in lines.Skip(1))
        {
            string[] entry = line.Split('=');
            float max = 100;
            long time = 0;
            if (entry.Length != (legacy ? 2 : 4) || entry[0].Length != 24 || !entry[0].All(Uri.IsHexDigit) ||
                !TryParse(entry[1], out float current) ||
                (!legacy && (!TryParse(entry[2], out max) ||
                    !long.TryParse(entry[3], NumberStyles.None, CultureInfo.InvariantCulture, out time))) ||
                !Valid(current, max) || !_values.TryAdd(entry[0], new PartCondition(current, max, time)))
                throw new InvalidDataException("Invalid entry in condition store.");
        }
    }

    internal int Count => _values.Count;
    internal bool IsDirty { get; private set; }

    internal bool TryGetState(string id, out PartCondition value) => _values.TryGetValue(id, out value);

    internal void Record(string id, float current, float max)
    {
        if (!Valid(current, max) || (_values.TryGetValue(id, out PartCondition old) &&
                old.Current == current && old.Maximum == max))
            return;

        _values[id] = new PartCondition(current, max, DateTime.UtcNow.Ticks);
        IsDirty = true;
    }

    internal void Flush()
    {
        if (!IsDirty)
            return;

        string pending = _path + ".pending";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream);
            writer.WriteLine("ComponentWear 2");
            foreach (var item in _values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                PartCondition value = item.Value;
                writer.WriteLine(string.Join("=", item.Key, value.Current.ToString("R", CultureInfo.InvariantCulture),
                    value.Maximum.ToString("R", CultureInfo.InvariantCulture), value.UpdatedAt.ToString(CultureInfo.InvariantCulture)));
            }
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
            File.Replace(pending, _path, null);
        else
            File.Move(pending, _path);
        IsDirty = false;
    }

    private static bool TryParse(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool Valid(float current, float max) => current >= 0f && current <= max && max > 0f && max <= 100f;
}

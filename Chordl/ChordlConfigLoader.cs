using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chordl;

public static class ChordlConfigLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly StringComparer ModifierComparer = StringComparer.OrdinalIgnoreCase;

    public static ChordlConfiguration LoadFromDefaultLocation()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "hotkeys.json");
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "hotkeys.json");
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("hotkeys.json not found.", configPath);
        }

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<ChordlConfigDto>(json, JsonOpts)
            ?? throw new InvalidOperationException("Failed to parse hotkeys.json.");

        ValidateConfig(config);

        var actions = BuildActionMap(config);
        return new ChordlConfiguration(
            actions,
            actions.Keys.Select(chord => chord.KeyCode).ToHashSet(),
            TimeSpan.FromMilliseconds(config.RepeatSuppressionDelayMs),
            TimeSpan.FromMilliseconds(config.HoldDelayMs));
    }

    private static void ValidateConfig(ChordlConfigDto config)
    {
        var errors = new List<string>();

        if (config.RepeatSuppressionDelayMs < 0)
        {
            errors.Add("repeatSuppressionDelayMs must be zero or greater.");
        }

        if (config.HoldDelayMs <= 0)
        {
            errors.Add("holdDelayMs must be greater than zero.");
        }

        var definitions = config.Hotkeys;
        if (definitions is null)
        {
            errors.Add("hotkeys must be an array.");
            throw CreateInvalidConfigException(errors);
        }

        if (definitions.Count == 0)
        {
            errors.Add("hotkeys must contain at least one definition.");
        }

        var seenChords = new HashSet<ChordlChord>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var dto = definitions[i];
            var label = string.IsNullOrWhiteSpace(dto.Name)
                ? $"hotkeys[{i}]"
                : $"hotkey '{dto.Name}'";
            var modifiers = dto.Modifiers ?? [];
            var replayModifiers = dto.ReplayModifiers ?? [];

            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                errors.Add($"{label} must define a name.");
            }

            if (!ChordlKeys.TryKeyToVirtualKey(dto.Key, out var keyCode))
            {
                errors.Add($"{label} has unsupported key '{dto.Key}'. Use a single letter key.");
            }

            if (!modifiers.Contains("Ctrl", ModifierComparer))
            {
                errors.Add($"{label} must include Ctrl in modifiers.");
            }

            foreach (var modifier in modifiers.Where(modifier => !IsKnownModifier(modifier)))
            {
                errors.Add($"{label} has unsupported modifier '{modifier}'. Supported modifiers: Ctrl, Shift.");
            }

            foreach (var modifier in replayModifiers.Where(modifier => !IsKnownModifier(modifier)))
            {
                errors.Add($"{label} has unsupported replay modifier '{modifier}'. Supported replay modifiers: Ctrl, Shift.");
            }

            if (!Enum.TryParse<ChordlDispatchMode>(dto.Dispatch, ignoreCase: true, out _))
            {
                errors.Add($"{label} has unsupported dispatch '{dto.Dispatch}'. Supported dispatch values: None, Immediate, TapOnly.");
            }

            if (keyCode != 0)
            {
                var chord = new ChordlChord(
                    keyCode,
                    Ctrl: modifiers.Contains("Ctrl", ModifierComparer),
                    Shift: modifiers.Contains("Shift", ModifierComparer));
                if (!seenChords.Add(chord))
                {
                    errors.Add($"{label} duplicates {ChordlKeys.FormatComboName(keyCode, chord.Shift)}.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw CreateInvalidConfigException(errors);
        }
    }

    private static InvalidOperationException CreateInvalidConfigException(List<string> errors)
    {
        return new InvalidOperationException(
            "Invalid hotkeys.json:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(error => "- " + error)));
    }

    private static Dictionary<ChordlChord, ChordlAction> BuildActionMap(ChordlConfigDto source)
    {
        var map = new Dictionary<ChordlChord, ChordlAction>();
        foreach (var dto in source.Hotkeys)
        {
            var keyCode = ChordlKeys.KeyToVirtualKey(dto.Key);
            var ctrl = dto.Modifiers.Contains("Ctrl", ModifierComparer);
            var shift = dto.Modifiers.Contains("Shift", ModifierComparer);
            var dispatch = Enum.Parse<ChordlDispatchMode>(dto.Dispatch, ignoreCase: true);
            var replayShift = dto.ReplayModifiers.Contains("Shift", ModifierComparer);

            map[new ChordlChord(keyCode, ctrl, shift)] = new ChordlAction(
                dto.Name,
                dispatch,
                replayShift);
        }

        return map;
    }

    private static bool IsKnownModifier(string modifier)
    {
        return ModifierComparer.Equals(modifier, "Ctrl")
            || ModifierComparer.Equals(modifier, "Shift");
    }

    private sealed class ChordlConfigDto
    {
        public int RepeatSuppressionDelayMs { get; set; }
        public int HoldDelayMs { get; set; }
        public List<ChordlDefinitionDto> Hotkeys { get; set; } = new();
    }

    private sealed class ChordlDefinitionDto
    {
        public string Name { get; set; } = "";
        public string Key { get; set; } = "";
        public List<string> Modifiers { get; set; } = new();
        public string Dispatch { get; set; } = "None";
        public List<string> ReplayModifiers { get; set; } = ["Ctrl"];
    }
}


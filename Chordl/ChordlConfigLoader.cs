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
        if (!TryFindConfigFile(out var configPath))
        {
            throw new FileNotFoundException("hotkeys.json not found.", configPath);
        }

        return LoadFromFile(configPath);
    }

    /// <summary>
    /// Looks for an external hotkeys.json next to the executable, then in the
    /// source tree (so <c>dotnet run</c> works). Returns false when neither
    /// exists; <paramref name="configPath"/> is then the preferred location for
    /// error reporting.
    /// </summary>
    public static bool TryFindConfigFile(out string configPath)
    {
        configPath = Path.Combine(AppContext.BaseDirectory, "hotkeys.json");
        if (File.Exists(configPath))
        {
            return true;
        }

        var sourcePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "hotkeys.json");
        if (File.Exists(sourcePath))
        {
            configPath = sourcePath;
            return true;
        }

        return false;
    }

    public static ChordlConfiguration LoadFromFile(string configPath)
    {
        return LoadFromJson(File.ReadAllText(configPath));
    }

    public static ChordlConfiguration LoadFromJson(string json)
    {
        var config = JsonSerializer.Deserialize<ChordlConfigDto>(json, JsonOpts)
            ?? throw new InvalidOperationException("Failed to parse hotkeys.json.");

        Normalize(config);
        ValidateConfig(config);

        var actions = BuildActionMap(config);
        return new ChordlConfiguration(
            actions,
            actions.Keys.Select(chord => chord.KeyCode).ToHashSet(),
            TimeSpan.FromMilliseconds(config.RepeatSuppressionDelayMs),
            TimeSpan.FromMilliseconds(config.HoldDelayMs));
    }

    // Replace any null fields left by explicit JSON nulls (e.g.
    // "replayModifiers": null) with the same safe defaults the DTO uses when a
    // field is absent. Validation and BuildActionMap then work only against
    // normalized, non-null values; previously a literal null passed validation
    // (which coalesced locally) and then threw an NRE in BuildActionMap, which
    // read the raw DTO.
    private static void Normalize(ChordlConfigDto config)
    {
        config.Hotkeys ??= new();
        foreach (var dto in config.Hotkeys)
        {
            if (dto is null)
            {
                continue;
            }

            dto.Name ??= "";
            dto.Key ??= "";
            dto.Dispatch ??= "None";
            // Coalesce a null array, but keep any null *entries* so validation can
            // report them by index rather than silently dropping them.
            dto.Modifiers ??= new();
            dto.ReplayModifiers ??= new() { "Ctrl" };
        }
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
            if (dto is null)
            {
                errors.Add($"hotkeys[{i}] must be an object, not null.");
                continue;
            }

            var label = string.IsNullOrWhiteSpace(dto.Name)
                ? $"hotkeys[{i}]"
                : $"hotkey '{dto.Name}'";
            // Normalize has already replaced any null collections, so these are
            // safe to read directly.
            var modifiers = dto.Modifiers;
            var replayModifiers = dto.ReplayModifiers;

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

            for (var m = 0; m < modifiers.Count; m++)
            {
                if (!IsKnownModifier(modifiers[m]))
                {
                    errors.Add($"{label} modifiers[{m}] must be Ctrl or Shift, not '{modifiers[m] ?? "null"}'.");
                }
            }

            for (var m = 0; m < replayModifiers.Count; m++)
            {
                if (!IsKnownModifier(replayModifiers[m]))
                {
                    errors.Add($"{label} replayModifiers[{m}] must be Ctrl or Shift, not '{replayModifiers[m] ?? "null"}'.");
                }
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

    private static bool IsKnownModifier(string? modifier)
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


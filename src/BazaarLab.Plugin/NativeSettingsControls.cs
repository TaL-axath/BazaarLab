using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private ConfigEntry<bool>? _showLineupWindowConfig;
    private ConfigEntry<bool>? _showPlacementWindowConfig;
    private ConfigEntry<bool>? _showBaselineWindowConfig;
    private ConfigEntry<bool>? _enableMonsterSimulationConfig;

    private bool ShowLineupWindow => _showLineupWindowConfig?.Value ?? true;
    private bool ShowPlacementWindow => _showPlacementWindowConfig?.Value ?? true;
    private bool ShowBaselineWindow => _showBaselineWindowConfig?.Value ?? true;
    private bool MonsterSimulationEnabled => _enableMonsterSimulationConfig?.Value ?? true;

    private void InitializeNativeSettingsControls()
    {
        _showLineupWindowConfig = Config.Bind("界面", "显示阵容码对战浮窗", true,
            "在游戏界面显示 BazaarLab 的本地阵容码对战浮窗。");
        _showPlacementWindowConfig = Config.Bind("界面", "显示摆位规划浮窗", true,
            "在游戏界面显示 BazaarLab 的本地摆位规划浮窗。");
        _showBaselineWindowConfig = Config.Bind("界面", "显示白板曲线浮窗", true,
            "在游戏界面显示 BazaarLab 的伤害、护盾、治疗曲线浮窗。");
        _enableMonsterSimulationConfig = Config.Bind("模拟", "启用野怪模拟", true,
            "自动计算野怪遭遇和已选择野怪的固定 50 场胜率。");

        try
        {
            RegisterBppToggle("BazaarLabLineupWindow", "阵容码对战浮窗",
                () => ShowLineupWindow,
                value => _showLineupWindowConfig.Value = value);
            RegisterBppToggle("BazaarLabPlacementWindow", "摆位规划浮窗",
                () => ShowPlacementWindow,
                value => _showPlacementWindowConfig.Value = value);
            RegisterBppToggle("BazaarLabBaselineWindow", "白板曲线浮窗",
                () => ShowBaselineWindow,
                value => _showBaselineWindowConfig.Value = value);
            RegisterBppToggle("BazaarLabMonsterSimulation", "野怪战斗模拟",
                () => MonsterSimulationEnabled, SetMonsterSimulationEnabled);
            RefreshBppNativeSettings();
            Logger.LogInfo("registered BazaarLab controls in the native BPP settings page");
        }
        catch (Exception exception)
        {
            Logger.LogWarning("native BazaarLab settings unavailable; BepInEx config remains usable: " +
                exception.GetType().Name + ": " + exception.Message);
        }
    }

    private void RegisterBppToggle(string key, string chineseLabel,
        Func<bool> read, Action<bool> write)
    {
        Assembly bpp = typeof(BazaarPlusPlus.Game.PvpBattles.PvpBattleSnapshots).Assembly;
        Type definitionType = bpp.GetType(
            "BazaarPlusPlus.Game.Settings.BppSettingsDockDefinition", throwOnError: true)!;
        Type catalogType = bpp.GetType(
            "BazaarPlusPlus.Game.Settings.BppSettingsDockCatalog", throwOnError: true)!;
        FieldInfo definitionsField = catalogType.GetField("_definitions",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(catalogType.FullName, "_definitions");
        IList definitions = definitionsField.GetValue(null) as IList
            ?? throw new InvalidOperationException("BPP settings catalog is unavailable");
        PropertyInfo keyProperty = definitionType.GetProperty("Key",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(definitionType.FullName, "Key");
        if (definitions.Cast<object>().Any(value => string.Equals(
                keyProperty.GetValue(value) as string, key, StringComparison.Ordinal)))
        {
            return;
        }
        MethodInfo toggleFactory = definitionType.GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Toggle" && method.GetParameters().Length == 5);
        Func<string, string> label = languageCode =>
            IsChineseLanguage(languageCode)
                ? "BL·" + chineseLabel
                : "BL·" + EnglishSettingsLabel(key);
        object definition = toggleFactory.Invoke(null, new object[]
        {
            key,
            label,
            read,
            write,
            new Func<bool>(() => true),
        }) ?? throw new InvalidOperationException("BPP toggle factory returned null");
        definitions.Add(definition);
    }

    private static bool IsChineseLanguage(string? languageCode) =>
        languageCode is not null &&
        languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string EnglishSettingsLabel(string key) => key switch
    {
        "BazaarLabLineupWindow" => "Duel",
        "BazaarLabPlacementWindow" => "Plan",
        "BazaarLabBaselineWindow" => "DPS",
        "BazaarLabMonsterSimulation" => "PvE",
        _ => key,
    };

    private static void RefreshBppNativeSettings()
    {
        Assembly bpp = typeof(BazaarPlusPlus.Game.PvpBattles.PvpBattleSnapshots).Assembly;
        Type? controller = bpp.GetType(
            "BazaarPlusPlus.Game.Settings.BppNativeSettingsSectionController");
        controller?.GetMethod("RefreshAll", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, null);
    }

    private void SetMonsterSimulationEnabled(bool enabled)
    {
        if (_enableMonsterSimulationConfig is not null)
            _enableMonsterSimulationConfig.Value = enabled;
        if (enabled)
        {
            _monsterCandidatePayload = null;
            _monsterCompletedPayload = null;
            _encounterPreviewCandidateFingerprint = null;
            _encounterPreviewAppliedFingerprint = null;
            return;
        }
        if (IsMonsterCalculating) CancelMonsterCalculationForCombat();
        if (IsEncounterPreviewCalculating) CancelEncounterPreviewsForCombat();
        _monsterResult = null;
        _pendingMonsterPrediction = null;
        _encounterPreviews.Clear();
        _encounterPreviewQueue.Clear();
        _predictionWorker?.Invalidate();
    }
}

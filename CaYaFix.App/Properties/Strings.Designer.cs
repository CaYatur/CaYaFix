// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Globalization;
using System.Resources;

namespace CaYaFix.App.Properties;

public static class Strings
{
    private static readonly ResourceManager Manager =
        new("CaYaFix.App.Properties.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string AppName => Get(nameof(AppName));
    public static string AppTagline => Get(nameof(AppTagline));
    public static string App_ReleaseChannel => Get(nameof(App_ReleaseChannel));
    public static string App_VersionFormat => Get(nameof(App_VersionFormat));
    public static string Nav_Home => Get(nameof(Nav_Home));
    public static string Nav_Findings => Get(nameof(Nav_Findings));
    public static string Nav_LiveTests => Get(nameof(Nav_LiveTests));
    public static string Nav_Recovery => Get(nameof(Nav_Recovery));
    public static string Nav_Settings => Get(nameof(Nav_Settings));
    public static string AdminMode => Get(nameof(AdminMode));
    public static string Hero_Eyebrow => Get(nameof(Hero_Eyebrow));
    public static string Hero_Title => Get(nameof(Hero_Title));
    public static string Hero_Description => Get(nameof(Hero_Description));
    public static string Action_AutomaticScan => Get(nameof(Action_AutomaticScan));
    public static string Action_Cancel => Get(nameof(Action_Cancel));
    public static string Action_SkipRestorePoint => Get(nameof(Action_SkipRestorePoint));
    public static string Action_RepairSelected => Get(nameof(Action_RepairSelected));
    public static string Action_TryModerate => Get(nameof(Action_TryModerate));
    public static string Action_ForceRepair => Get(nameof(Action_ForceRepair));
    public static string Action_CreateSupportPackage => Get(nameof(Action_CreateSupportPackage));
    public static string Action_OpenReport => Get(nameof(Action_OpenReport));
    public static string Action_OpenFolder => Get(nameof(Action_OpenFolder));
    public static string Action_UndoSession => Get(nameof(Action_UndoSession));
    public static string Action_UndoAction => Get(nameof(Action_UndoAction));
    public static string Action_RunTest => Get(nameof(Action_RunTest));
    public static string Action_RunExpertFix => Get(nameof(Action_RunExpertFix));
    public static string Action_DismissRecovery => Get(nameof(Action_DismissRecovery));
    public static string Action_ClosePanel => Get(nameof(Action_ClosePanel));
    public static string Action_ViewFindings => Get(nameof(Action_ViewFindings));
    public static string Action_ForceRepairReal => Get(nameof(Action_ForceRepairReal));
    public static string Action_ForceRepairAll => Get(nameof(Action_ForceRepairAll));
    public static string Action_ScanModule => Get(nameof(Action_ScanModule));
    public static string ModulePanel_Details => Get(nameof(ModulePanel_Details));
    public static string ModulePanel_Symptoms => Get(nameof(ModulePanel_Symptoms));
    public static string Operation_StatPassed => Get(nameof(Operation_StatPassed));
    public static string Operation_StatFindings => Get(nameof(Operation_StatFindings));
    public static string Operation_StatCommon => Get(nameof(Operation_StatCommon));
    public static string Operation_StatActionable => Get(nameof(Operation_StatActionable));
    public static string Action_Minimize => Get(nameof(Action_Minimize));
    public static string Action_Maximize => Get(nameof(Action_Maximize));
    public static string Action_Close => Get(nameof(Action_Close));
    public static string Dashboard_Modules => Get(nameof(Dashboard_Modules));
    public static string Dashboard_ModulesHint => Get(nameof(Dashboard_ModulesHint));
    public static string Dashboard_ModuleCount => Get(nameof(Dashboard_ModuleCount));
    public static string Dashboard_SafetyTitle => Get(nameof(Dashboard_SafetyTitle));
    public static string Dashboard_SafetyText => Get(nameof(Dashboard_SafetyText));
    public static string Findings_Title => Get(nameof(Findings_Title));
    public static string Findings_Description => Get(nameof(Findings_Description));
    public static string Findings_None => Get(nameof(Findings_None));
    public static string Finding_TechnicalDetail => Get(nameof(Finding_TechnicalDetail));
    public static string Finding_Recommended => Get(nameof(Finding_Recommended));
    public static string Escalation_Title => Get(nameof(Escalation_Title));
    public static string Escalation_ModerateText => Get(nameof(Escalation_ModerateText));
    public static string Escalation_ForceText => Get(nameof(Escalation_ForceText));
    public static string Force_Accept => Get(nameof(Force_Accept));
    public static string LiveTests_Title => Get(nameof(LiveTests_Title));
    public static string LiveTests_Description => Get(nameof(LiveTests_Description));
    public static string LiveTests_Output => Get(nameof(LiveTests_Output));
    public static string LiveMetrics_Title => Get(nameof(LiveMetrics_Title));
    public static string LiveMetrics_Test => Get(nameof(LiveMetrics_Test));
    public static string LiveMetrics_Result => Get(nameof(LiveMetrics_Result));
    public static string LiveMetrics_Measurements => Get(nameof(LiveMetrics_Measurements));
    public static string LiveTest_Running => Get(nameof(LiveTest_Running));
    public static string Recovery_Title => Get(nameof(Recovery_Title));
    public static string Recovery_Description => Get(nameof(Recovery_Description));
    public static string Recovery_None => Get(nameof(Recovery_None));
    public static string Settings_Title => Get(nameof(Settings_Title));
    public static string Settings_DryRun => Get(nameof(Settings_DryRun));
    public static string Settings_DryRunDescription => Get(nameof(Settings_DryRunDescription));
    public static string Settings_Expert => Get(nameof(Settings_Expert));
    public static string Settings_ExpertDescription => Get(nameof(Settings_ExpertDescription));
    public static string Settings_ExpertCatalog => Get(nameof(Settings_ExpertCatalog));
    public static string Settings_ExpertCatalogDescription => Get(nameof(Settings_ExpertCatalogDescription));
    public static string Expert_TierSelector => Get(nameof(Expert_TierSelector));
    public static string Settings_Offline => Get(nameof(Settings_Offline));
    public static string Settings_OfflineDescription => Get(nameof(Settings_OfflineDescription));
    public static string Settings_Language => Get(nameof(Settings_Language));
    public static string Settings_LanguageDescription => Get(nameof(Settings_LanguageDescription));
    public static string Language_English => Get(nameof(Language_English));
    public static string Language_Turkish => Get(nameof(Language_Turkish));
    public static string Language_System => Get(nameof(Language_System));
    public static string Console_Title => Get(nameof(Console_Title));
    public static string Console_Hint => Get(nameof(Console_Hint));
    public static string Tier_Safe => Get(nameof(Tier_Safe));
    public static string Tier_Moderate => Get(nameof(Tier_Moderate));
    public static string Tier_Aggressive => Get(nameof(Tier_Aggressive));
}

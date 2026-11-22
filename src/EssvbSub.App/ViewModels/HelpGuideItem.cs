// Copyright (c) 2026 ESSVB. All Rights Reserved.

namespace EssvbSub.App.ViewModels;

/// <summary>
/// Metadata for an illustrated step-by-step user guide shown in the Yardım &amp; Hakkında tab.
/// </summary>
public sealed record HelpGuideItem(
    int Index,
    string TabTitle,
    string FullTitle,
    string Summary,
    string FileName)
{
    public string ResourceUri => $"avares://ESSVB-sub/Assets/Help/{FileName}";
    public string StepLabel => $"Adım {Index}/5";
}

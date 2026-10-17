using Lumen.Core.Models;
using Lumen.App.Helpers;

namespace Lumen.App.ViewModels;

/// <summary>模型下拉项：包一层 <see cref="ModelDescriptor"/>，只暴露界面需要的只读展示值。</summary>
public sealed class ModelOption
{
    public ModelOption(ModelDescriptor model)
    {
        Model = model;
    }

    public ModelDescriptor Model { get; }

    public string Id => Model.Id;

    public string DisplayName => $"{Model.DisplayName} · {Model.Scale}×";

    public string Description => Model.Description;

    /// <summary>倍率角标，例如 “4×”。</summary>
    public string ScaleLabel => $"{Model.Scale}×";

    public string TierLabel => Model.QualityTier switch
    {
        ModelQualityTier.Fast => "极速",
        ModelQualityTier.Balanced => "均衡",
        _ => "质量",
    };

    public string SizeLabel => Ui.FileSize(Model.SizeBytes);

    public override string ToString() => DisplayName;
}

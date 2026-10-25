using Microsoft.Extensions.Options;

namespace Maldex.Web.Templates;

/// <summary>Bound from <c>Maldex:Templates</c>: what the admin template pages show.</summary>
public sealed class TemplateAdminOptions
{
    public const string SectionName = "Maldex:Templates";

    /// <summary>A template is due for a refresh when its current version was sealed more than this many days ago.</summary>
    public int RefreshDueDays { get; set; } = 30;
}

public sealed class TemplateAdminOptionsValidation : IValidateOptions<TemplateAdminOptions>
{
    public ValidateOptionsResult Validate(string? name, TemplateAdminOptions o) =>
        o.RefreshDueDays >= 1
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{TemplateAdminOptions.SectionName}:RefreshDueDays is {o.RefreshDueDays}; it must be at least 1");
}

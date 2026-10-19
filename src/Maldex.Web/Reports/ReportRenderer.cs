using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Maldex.Web.Reports;

/// <summary>Renders <see cref="ReportDocument"/> to an HTML string. Razor encodes every model value, so guest text can never become markup.</summary>
public sealed class ReportRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
{
    public async Task<string> RenderHtmlAsync(ReportModel model)
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ReportDocument>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ReportDocument.Model)] = model }));
            return output.ToHtmlString();
        });
    }
}

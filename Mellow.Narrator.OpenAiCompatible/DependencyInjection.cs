using Mellow.Narrator.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Mellow.Narrator.OpenAiCompatible;

public static class OpenAiCompatibleServiceCollectionExtensions
{
    public static IServiceCollection AddMellowNarratorOpenAiCompatible(this IServiceCollection services)
    {
        services.AddHttpClient<ILanguageModelProvider, OpenAiCompatibleProvider>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MellowNarrator/1.0");
        });
        return services;
    }
}

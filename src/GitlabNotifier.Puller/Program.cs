using System.Net.Http.Headers;
using System.Text.Json;
using GitlabNotifier.Puller.Api;
using GitlabNotifier.Puller.Configuration;
using GitlabNotifier.Puller.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Refit;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);

builder.Services.Configure<GitlabOptions>(builder.Configuration.GetRequiredSection("Gitlab"));

builder.Services
    .AddRefitClient<IGitlabApiClient>()
    .ConfigureHttpClient((serviceProvider, httpClient) =>
    {
        var gitlabOptions = serviceProvider.GetRequiredService<IOptions<GitlabOptions>>().Value;

        if (string.IsNullOrWhiteSpace(gitlabOptions.Url))
        {
            throw new InvalidOperationException("GitLab URL must be configured.");
        }

        if (string.IsNullOrWhiteSpace(gitlabOptions.ApiKey))
        {
            throw new InvalidOperationException("GitLab API key must be configured.");
        }

        httpClient.BaseAddress = EnsureTrailingSlash(new Uri(gitlabOptions.Url, UriKind.Absolute));
        httpClient.DefaultRequestHeaders.Remove("PRIVATE-TOKEN");
        httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", gitlabOptions.ApiKey);
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    });

builder.Services.AddTransient<GitlabDataPuller>();

using var host = builder.Build();

var gitlabDataPuller = host.Services.GetRequiredService<GitlabDataPuller>();
var mergeRequests = await gitlabDataPuller.GetMergeRequestsForCurrentUserAsync();

Console.WriteLine(JsonSerializer.Serialize(
    mergeRequests,
    new JsonSerializerOptions { WriteIndented = true }));

static Uri EnsureTrailingSlash(Uri uri)
{
    return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
        ? uri
        : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
}

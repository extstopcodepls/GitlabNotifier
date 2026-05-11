using System.Net.Http.Headers;
using GitlabNotifier.Data;
using GitlabNotifier.Runner.Api;
using GitlabNotifier.Runner.Configuration;
using GitlabNotifier.Runner.Services;
using GitlabNotifier.Runner.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);

builder.Services.Configure<GitlabOptions>(builder.Configuration.GetRequiredSection("Gitlab"));
builder.Services.Configure<RunnerOptions>(builder.Configuration.GetRequiredSection("Runner"));

builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetRequiredSection("Logging"));
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.AddDbContext<GitlabNotifierDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("GitlabNotifier")
        ?? "Data Source=gitlab-notifier.db"));

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
builder.Services.AddTransient<GitlabNotifierDatabaseInitializer>();
builder.Services.AddTransient<GitlabMergeRequestCacheUpdater>();
builder.Services.AddSingleton<GitlabMergeRequestNotificationService>();
builder.Services.AddSingleton<IGitlabMergeRequestNotificationService>(
    serviceProvider => serviceProvider.GetRequiredService<GitlabMergeRequestNotificationService>());
builder.Services.AddHostedService(
    serviceProvider => serviceProvider.GetRequiredService<GitlabMergeRequestNotificationService>());
builder.Services.AddHostedService<GitlabPullerWorker>();

using var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var databaseInitializer = scope.ServiceProvider.GetRequiredService<GitlabNotifierDatabaseInitializer>();
    await databaseInitializer.EnsureInitializedAsync();
}

await host.RunAsync();

static Uri EnsureTrailingSlash(Uri uri)
{
    return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
        ? uri
        : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
}

﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Azure.Identity;
using EntityFrameworkCore.Triggers;
using Maestro.Common;
using Maestro.Common.AzureDevOpsTokens;
using Maestro.Data;
using Maestro.Data.Models;
using Maestro.DataProviders;
using Maestro.MergePolicies;
using Microsoft.AspNetCore.ApiPagination;
using Microsoft.AspNetCore.ApiVersioning;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.DotNet.DarcLib;
using Microsoft.DotNet.GitHub.Authentication;
using Microsoft.DotNet.Internal.Logging;
using Microsoft.DotNet.Services.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using ProductConstructionService.Api.Api;
using ProductConstructionService.Api.Configuration;
using ProductConstructionService.Api.Pages.DependencyFlow;
using ProductConstructionService.Api.Telemetry;
using ProductConstructionService.Api.VirtualMonoRepo;
using ProductConstructionService.Common;
using ProductConstructionService.DependencyFlow.WorkItems;
using ProductConstructionService.WorkItems;
using ProductConstructionService.DependencyFlow;
using ProductConstructionService.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octokit.Webhooks.AspNetCore;
using Octokit.Webhooks;
using ProductConstructionService.Api.Controllers;

namespace ProductConstructionService.Api;

internal static class PcsStartup
{
    private const string SqlConnectionStringUserIdPlaceholder = "USER_ID_PLACEHOLDER";
    private const string GitHubWebHooksPath = "/api/webhooks/incoming/github";

    private static class ConfigurationKeys
    {
        // All secrets loaded from KeyVault will have this prefix
        public const string KeyVaultSecretPrefix = "KeyVaultSecrets:";

        // Secrets coming from the KeyVault
        public const string GitHubClientId = $"{KeyVaultSecretPrefix}github-app-id";
        public const string GitHubClientSecret = $"{KeyVaultSecretPrefix}github-app-private-key";
        public const string GitHubAppWebhook = $"{KeyVaultSecretPrefix}github-app-webhook-secret";

        // Configuration from appsettings.json
        public const string AzureDevOpsConfiguration = "AzureDevOps";
        public const string DatabaseConnectionString = "BuildAssetRegistrySqlConnectionString";
        public const string DependencyFlowSLAs = "DependencyFlowSLAs";
        public const string EntraAuthenticationKey = "EntraAuthentication";
        public const string KeyVaultName = "KeyVaultName";
        public const string ManagedIdentityId = "ManagedIdentityClientId";
    }

    /// <summary>
    /// Path to the compiled static files for the Angular app.
    /// This is required when running PCS locally when Angular is not published.
    /// </summary>
    internal static string LocalCompiledStaticFilesPath => Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "artifacts", "bin", "ProductConstructionService.BarViz", "Release", "net8.0", "browser-wasm", "publish", "wwwroot");

    static PcsStartup()
    {
        var metadata = typeof(Program).Assembly
            .GetCustomAttributes()
            .OfType<AssemblyMetadataAttribute>()
            .ToDictionary(m => m.Key, m => m.Value);

        Triggers<BuildChannel>.Inserted += entry =>
        {
            var context = (BuildAssetRegistryContext)entry.Context;
            ILogger<BuildAssetRegistryContext> logger = context.GetService<ILogger<BuildAssetRegistryContext>>();
            var workItemProducerFactory = context.GetService<IWorkItemProducerFactory>();
            BuildChannel entity = entry.Entity;

            Build? build = context.Builds
                .Include(b => b.Assets)
                .ThenInclude(a => a.Locations)
                .FirstOrDefault(b => b.Id == entity.BuildId);

            if (build == null)
            {
                logger.LogError("Could not find build with id {buildId} in BAR. Skipping dependency update.", entity.BuildId);
            }
            else
            {
                bool hasAssetsWithPublishedLocations = build.Assets
                    .Any(a => a.Locations.Any(al => al.Type != LocationType.None && !al.Location.EndsWith("/artifacts")));

                if (!hasAssetsWithPublishedLocations)
                {
                    logger.LogInformation("Skipping Dependency update for Build {buildId} because it contains no assets in valid locations", entity.BuildId);
                    return;
                }

                List<Subscription> subscriptionsToUpdate = context.Subscriptions
                    .Where(sub =>
                        sub.Enabled &&
                        sub.ChannelId == entity.ChannelId &&
                        (sub.SourceRepository == entity.Build.GitHubRepository || sub.SourceDirectory == entity.Build.AzureDevOpsRepository) &&
                        JsonExtensions.JsonValue(sub.PolicyString, "lax $.UpdateFrequency") == ((int)UpdateFrequency.EveryBuild).ToString())
                    .ToList();

                foreach (Subscription subscription in subscriptionsToUpdate)
                {
                    var workItemProducer = workItemProducerFactory.CreateProducer<SubscriptionTriggerWorkItem>(subscription.SourceEnabled);
                    workItemProducer.ProduceWorkItemAsync(new()
                    {
                        BuildId = entity.BuildId,
                        SubscriptionId = subscription.Id
                    }).GetAwaiter().GetResult();
                }
            }
        };
    }

    /// <summary>
    /// Registers all necessary services for the Product Construction Service
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="addKeyVault">Use KeyVault for secrets?</param>
    /// <param name="authRedis">Use authenticated connection for Redis?</param>
    /// <param name="addSwagger">Add Swagger UI?</param>
    internal static async Task ConfigurePcs(
        this WebApplicationBuilder builder,
        bool addKeyVault,
        bool authRedis,
        bool addSwagger)
    {
        bool isDevelopment = builder.Environment.IsDevelopment();

        // Read configuration
        string? managedIdentityId = builder.Configuration[ConfigurationKeys.ManagedIdentityId];
        string databaseConnectionString = builder.Configuration.GetRequiredValue(ConfigurationKeys.DatabaseConnectionString)
            .Replace(SqlConnectionStringUserIdPlaceholder, managedIdentityId);
        builder.Services.Configure<AzureDevOpsTokenProviderOptions>(ConfigurationKeys.AzureDevOpsConfiguration, (o, s) => s.Bind(o));

        DefaultAzureCredential azureCredential = new(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityId,
        });

        builder.AddDataProtection(azureCredential);
        builder.AddTelemetry();

        if (addKeyVault)
        {
            Uri keyVaultUri = new($"https://{builder.Configuration.GetRequiredValue(ConfigurationKeys.KeyVaultName)}.vault.azure.net/");
            builder.Configuration.AddAzureKeyVault(
                keyVaultUri,
                azureCredential,
                new KeyVaultSecretsWithPrefix(ConfigurationKeys.KeyVaultSecretPrefix));
        }

        // This needs to precede the AddVmrRegistrations call as we want a GitHub provider using the app installations
        // Otherwise, AddVmrRegistrations would add one based on PATs (like we give it in darc)
        builder.Services.TryAddSingleton<IRemoteTokenProvider>(sp =>
        {
            var azdoTokenProvider = sp.GetRequiredService<IAzureDevOpsTokenProvider>();
            var gitHubTokenProvider = sp.GetRequiredService<IGitHubTokenProvider>();
            return new RemoteTokenProvider(azdoTokenProvider, new Microsoft.DotNet.DarcLib.GitHubTokenProvider(gitHubTokenProvider));
        });

        await builder.AddRedisCache(authRedis);
        builder.AddBuildAssetRegistry();
        builder.AddMetricRecorder();
        builder.AddWorkItemQueues(azureCredential, waitForInitialization: true);
        builder.AddDependencyFlowProcessors();
        builder.AddVmrRegistrations();
        builder.AddGitHubClientFactory(
            builder.Configuration[ConfigurationKeys.GitHubClientId],
            builder.Configuration[ConfigurationKeys.GitHubClientSecret]);
        builder.Services.AddGitHubTokenProvider();
        builder.Services.AddScoped<IRemoteFactory, RemoteFactory>();
        builder.Services.AddSingleton<Microsoft.Extensions.Internal.ISystemClock, Microsoft.Extensions.Internal.SystemClock>();
        builder.Services.AddSingleton<ExponentialRetry>();
        builder.Services.Configure<ExponentialRetryOptions>(_ => { });
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(builder.Configuration);

        // We do not use AddMemoryCache here. We use our own cache because we wish to
        // use a sized cache and some components, such as EFCore, do not implement their caching
        // in such a way that will work with sizing.
        builder.Services.AddSingleton<DarcRemoteMemoryCache>();
        builder.Services.EnableLazy();
        builder.Services.AddMergePolicies();
        builder.Services.Configure<SlaOptions>(builder.Configuration.GetSection(ConfigurationKeys.DependencyFlowSLAs));

        builder.InitializeVmrFromRemote();
        builder.AddServiceDefaults();

        // Configure API
        builder.Services.Configure<CookiePolicyOptions>(
            options =>
            {
                options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
                options.Secure = builder.Environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
            });
        builder.Services.ConfigureAuthServices(builder.Configuration.GetSection(ConfigurationKeys.EntraAuthenticationKey));
        builder.ConfigureApiRedirection();
        builder.Services.AddApiVersioning(options => options.VersionByQuery("api-version"));
        builder.Services.AddOperationTracking(_ => { });
        builder.Services.AddHttpLogging(
            options =>
            {
                options.LoggingFields =
                    HttpLoggingFields.RequestPath
                    | HttpLoggingFields.RequestQuery
                    | HttpLoggingFields.ResponseStatusCode;
                options.CombineLogs = true;
            });

        builder.Services
            .AddControllers()
            .AddNewtonsoftJson(
                options =>
                {
                    options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                    options.SerializerSettings.Converters.Add(new StringEnumConverter
                    {
                        NamingStrategy = new CamelCaseNamingStrategy()
                    });
                    options.SerializerSettings.Converters.Add(
                        new IsoDateTimeConverter
                        {
                            DateTimeFormat = "yyyy-MM-ddTHH:mm:ssZ",
                            DateTimeStyles = DateTimeStyles.AdjustToUniversal
                        });
                });

        builder.Services.AddRazorPages(
            options =>
            {
                options.Conventions.AuthorizeFolder("/", AuthenticationConfiguration.MsftAuthorizationPolicyName);
                options.Conventions.AllowAnonymousToPage("/Error");
            })
            .AddGitHubWebHooks()
            .AddApiPagination()
            .AddCookieTempDataProvider(
                options =>
                {
                    // Cookie Policy will not send this cookie unless we mark it as Essential
                    // The application will not function without this cookie.
                    options.Cookie.IsEssential = true;
                });

        builder.Services.AddTransient<WebhookEventProcessor, GitHubWebhookEventProcessor>();

        if (addSwagger)
        {
            builder.ConfigureSwagger();
        }

        if (isDevelopment)
        {
            builder.Services.AddCors(policy =>
            {
                policy.AddDefaultPolicy(p =>
                    // These come from BarViz project's launchsettings.json
                    p.WithOrigins("https://localhost:7287", "http://localhost:5015")
                      .AllowAnyHeader()
                      .AllowAnyMethod());
            });
        }
    }

    public static void ConfigureApi(this IApplicationBuilder app, bool isDevelopment)
    {
        app.UseApiRedirection(requireAuth: !isDevelopment);
        app.UseExceptionHandler(a =>
            a.Run(async ctx =>
            {
                var result = new ApiError("An error occured.");
                MvcNewtonsoftJsonOptions jsonOptions =
                    ctx.RequestServices.GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>().Value;
                string output = JsonConvert.SerializeObject(result, jsonOptions.SerializerSettings);
                ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await ctx.Response.WriteAsync(output, Encoding.UTF8);
            }));
        app.UseEndpoints(e =>
        {
            var controllers = e.MapControllers();
            if (isDevelopment)
            {
                controllers.AllowAnonymous();
            }
            
            e.MapGitHubWebhooks(
                path: GitHubWebHooksPath,
                secret: app.ApplicationServices.GetRequiredService<IConfiguration>()[ConfigurationKeys.GitHubAppWebhook]);
        });
    }

    public static void ConfigureSecurityHeaders(this WebApplication app)
    {
        app.Use((ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers.TryAdd("X-XSS-Protection", "1");
                ctx.Response.Headers.TryAdd("X-Frame-Options", "DENY");
                ctx.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
                ctx.Response.Headers.TryAdd("Referrer-Policy", "no-referrer-when-downgrade");
                // Allow DependencyFlow pages to be rendered on Azure DevOps dashboard
                ctx.Response.Headers.Append("Content-Security-Policy", "frame-ancestors https://dev.azure.com");
                return Task.CompletedTask;
            });

            return next();
        });
    }

    public static bool IsGet(this HttpContext context)
    {
        return string.Equals(context.Request.Method, "get", StringComparison.OrdinalIgnoreCase);
    }
}

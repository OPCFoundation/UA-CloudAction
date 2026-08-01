
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using System.Threading.RateLimiting;
using UACloudAction;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Swagger / OpenAPI for the OPC UA Web API, with an HTTP Basic security scheme so callers can
// authenticate directly from the Swagger UI using the ADMIN_USERNAME/ADMIN_PASSWORD credentials.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "UA-CloudAction OPC UA Web API",
        Version = "v1",
        Description = "OPC UA Web API (Read / HistoryRead) backed by the ADX and InfluxDB data sources."
    });

    const string basicScheme = UACloudAction.Services.BasicAuthenticationHandler.SchemeName;
    options.AddSecurityDefinition(basicScheme, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "basic",
        Description = "HTTP Basic authentication using the ADMIN_USERNAME / ADMIN_PASSWORD credentials."
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference(basicScheme),
            new List<string>()
        }
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The Azure Container Apps ingress proxy is not in the default known networks/proxies list.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth";
        options.AccessDeniedPath = "/Shared/Error";
        options.ExpireTimeSpan = TimeSpan.FromHours(1);
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, UACloudAction.Services.BasicAuthenticationHandler>(
        UACloudAction.Services.BasicAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorization();

builder.Services.AddSingleton<ActionProcessor>();

builder.Services.AddSingleton<UACloudAction.Services.AdxDataSource>();
builder.Services.AddSingleton<UACloudAction.Services.InfluxDataSource>();
builder.Services.AddSingleton<UACloudAction.Services.OpcUaDataSourceResolver>();

// Rate limiter protecting the OPC UA Web API endpoints. Each client IP is allowed a fixed
// number of requests per time window; excess requests receive HTTP 429 (Too Many Requests).
// The limit and window are configurable via the RATE_LIMIT_PERMIT and RATE_LIMIT_WINDOW_SECONDS
// environment variables (defaulting to 60 requests per 60 seconds).
int ratePermitLimit = int.TryParse(Environment.GetEnvironmentVariable("RATE_LIMIT_PERMIT"), out int parsedPermit) && parsedPermit > 0
    ? parsedPermit
    : 60;
int rateWindowSeconds = int.TryParse(Environment.GetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS"), out int parsedWindow) && parsedWindow > 0
    ? parsedWindow
    : 60;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(UACloudAction.Services.RateLimiterPolicies.OpcUaWebApi, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ratePermitLimit,
                Window = TimeSpan.FromSeconds(rateWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHsts();

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    // Relative to the Swagger UI route prefix, so the document is still resolved correctly when
    // the app is served behind a reverse proxy that adds a path prefix (e.g. Container Apps or an
    // ingress controller). An absolute "/swagger/v1/swagger.json" would bypass that prefix and
    // return the proxy's HTML page, which Swagger UI rejects with "does not specify a valid
    // version field".
    options.SwaggerEndpoint("v1/swagger.json", "UA-CloudAction OPC UA Web API v1");
});

_ = Task.Run(() => app.Services.GetService<ActionProcessor>()?.Run());

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

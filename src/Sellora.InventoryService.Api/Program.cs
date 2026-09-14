using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sellora.InventoryService.Api.Authorization;
using Sellora.InventoryService.Api.Identity;
using Sellora.InventoryService.Api.Tenancy;
using Sellora.InventoryService.Application.Events;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.HierarchyEvents;
using Sellora.InventoryService.Infrastructure.Persistence;
using Sellora.InventoryService.Infrastructure.Stock;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var jwt = builder.Configuration.GetSection("Jwt");
var audiences = jwt.GetSection("Audience").Get<string[]>()
    ?? new[] { jwt["Audience"]! };

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwt["Authority"];
        options.MetadataAddress = jwt["MetadataAddress"]!;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = "roles"
        };

        // Local developer machines may not have the shared WSO2 CA installed.
        // Staging and production must validate the WSO2 certificate chain.
        if (builder.Environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler
                        .DangerousAcceptAnyServerCertificateValidator
            };
        }
    });

builder.Services.AddAuthorization(options =>
    options.AddSelloraInventoryPolicies());

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpTenantContext>();
builder.Services.AddScoped<ITenantContext>(serviceProvider =>
    serviceProvider.GetRequiredService<HttpTenantContext>());
builder.Services.AddScoped<ISystemTenantContext>(serviceProvider =>
    serviceProvider.GetRequiredService<HttpTenantContext>());
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();

var connectionString =
    builder.Configuration.GetConnectionString("Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "The inventory database connection string is not configured.");
}

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<
    IStockAdjustmentService,
    StockAdjustmentService>();

builder.Services.AddScoped<
    IStockReadService,
    StockReadService>();

builder.Services.Configure<StockReservationOptions>(
    builder.Configuration.GetSection(
        StockReservationOptions.SectionName));

builder.Services.AddScoped<
    IStockReservationService,
    StockReservationService>();

builder.Services.AddScoped<
    IFulfilmentOwnerLookup,
    FulfilmentOwnerLookup>();

builder.Services.Configure<HierarchyConsumerOptions>(
    builder.Configuration.GetSection(
        HierarchyConsumerOptions.SectionName));

builder.Services.AddScoped<
    IHierarchyEventHandler,
    InventoryOwnerHierarchyEventHandler>();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<HierarchyEventConsumerService>();
}

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ReservationExpirySweeper>();
}

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddControllers();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;

    // Azure terminates TLS before forwarding requests to this container.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseForwardedHeaders();

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();

    var db = scope.ServiceProvider
        .GetRequiredService<InventoryDbContext>();

    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Containers receive internal HTTP traffic; HTTPS is terminated by APIM
// or a reverse proxy, allowing the direct health probe to return HTTP 200.
if (!app.Environment.IsEnvironment("Container"))
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.MapGet("/whoami", (HttpContext context) =>
    Results.Ok(context.User.Claims.Select(claim => new
    {
        claim.Type,
        claim.Value
    })))
    .RequireAuthorization();

app.Run();

public partial class Program;

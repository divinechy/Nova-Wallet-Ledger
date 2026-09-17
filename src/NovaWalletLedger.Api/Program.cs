using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NovaWalletLedger.Api.Data;
using NovaWalletLedger.Api.Endpoints;
using NovaWalletLedger.Api.Middleware;
using NovaWalletLedger.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Database ----
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// ---- Auth (mock issuer — see Services/DevTokenService.cs) ----
var jwtKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// ---- Rate limiting on the transfer endpoint (stretch goal) ----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("transfer", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                // Generous enough not to interfere with legitimate concurrent-transfer load
                // (including this service's own concurrency test suite); tune down for a
                // real deployment based on observed per-customer traffic.
                PermitLimit = 200,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            }));
});

// ---- App services ----
builder.Services.AddScoped<WalletService>();
builder.Services.AddSingleton<DevTokenService>();

// ---- Swagger / OpenAPI ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NovaWallet Ledger Service",
        Version = "v1",
        Description = "Simplified wallet ledger for FirstBank NovaPay's NovaWallet module."
    });

    var bearerScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste a token minted from POST /auth/dev-token."
    };
    options.AddSecurityDefinition("Bearer", bearerScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

var app = builder.Build();

// ---- Ensure the database schema exists, with a short retry loop for compose startup races ----
await EnsureDatabaseReadyAsync(app);

app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "NovaWallet Ledger v1"));

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapWalletEndpoints();
app.MapAuthEndpoints();
app.MapHealthEndpoints();

app.Run();

static async Task EnsureDatabaseReadyAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    const int maxAttempts = 10;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            logger.LogInformation("Database schema ready.");
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(ex, "Database not ready yet (attempt {Attempt}/{MaxAttempts}), retrying...", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

// Exposed for WebApplicationFactory<Program> in the test project.
public partial class Program { }

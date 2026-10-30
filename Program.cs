using System.Security.Claims;
using System.Text;
using LimsApi.Data;
using LimsApi.Models;
using LimsApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Default is missing in appsettings.json.");

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters long.");

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddControllers();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AuditService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep short claim names: "name", "module"
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = "name",
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // On every request, read the user's CURRENT module access from the database.
        // So a removed user is locked out at once, and access changes apply without signing in again.
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                if (!int.TryParse(ctx.Principal?.FindFirst("sub")?.Value, out var userId))
                {
                    ctx.Fail("Invalid token.");
                    return;
                }
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var user = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId && !u.IsDeleted)
                    .Select(u => new { Assigned = u.ModuleAccess.Select(m => m.Module).ToList() })
                    .FirstOrDefaultAsync();
                if (user is null)
                {
                    ctx.Fail("This user no longer exists.");
                    return;
                }
                var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
                foreach (var m in Modules.Normalize(user.Assigned))
                    identity.AddClaim(new Claim("module", m));
            }
        };
    });

// One policy per module, named after the module.
builder.Services.AddAuthorization(o =>
{
    foreach (var m in Modules.All)
        o.AddPolicy(m, p => p.RequireClaim("module", m));

    // Reading the lab setup is needed to register samples and to manage test setup.
    o.AddPolicy(Policies.ConfigRead, p => p.RequireAssertion(c =>
        c.User.HasClaim("module", Modules.RegisterSamples) || c.User.HasClaim("module", Modules.TestSetup)));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173","https://precut-afford-reapprove.ngrok-free.dev").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.GetMigrations().Any())
    {
        db.Database.Migrate();
    }
    else
    {
        app.Logger.LogWarning("No EF Core migrations found, so the schema is created directly. " +
            "Run 'dotnet ef migrations add InitialCreate' to add migrations (see README).");
        db.Database.EnsureCreated();
    }
    SeedData.Run(db);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

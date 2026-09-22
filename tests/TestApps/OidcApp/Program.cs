using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = builder.Configuration["Auth:Issuer"];
    o.MetadataAddress = builder.Configuration["Auth:Metadata"]!;
    o.Audience = builder.Configuration["Auth:Audience"];
    o.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters { ClockSkew = TimeSpan.Zero, ValidAlgorithms = [SecurityAlgorithms.RsaSha256] };
});
builder.Services.AddAuthorization(o => o.AddPolicy("write", policy => policy.RequireClaim("permission", "write")));
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/me", (HttpContext context) => Results.Ok(new { subject = context.User.FindFirst("sub")?.Value })).RequireAuthorization();
app.MapPost("/write", () => Results.NoContent()).RequireAuthorization("write");
app.Run();
public partial class Program;

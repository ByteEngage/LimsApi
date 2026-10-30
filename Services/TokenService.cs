using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LimsApi.Models;
using Microsoft.IdentityModel.Tokens;

namespace LimsApi.Services;

public class TokenService(IConfiguration config)
{
    public string Create(AppUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("name", user.Username)
        };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

using LimsApi.Data;
using LimsApi.Models;
using LimsApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

[ApiController, Route("api/auth")]
public class AuthController(AppDbContext db, TokenService tokens, AuditService audit) : ControllerBase
{
    public record LoginRequest(string Username, string Password);

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req)
    {
        var name = req.Username?.Trim() ?? "";
        var user = await db.Users.Include(u => u.ModuleAccess)
            .FirstOrDefaultAsync(u => u.Username == name && !u.IsDeleted);
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password ?? "", user.PasswordHash))
            return Unauthorized(new { message = "Wrong username or password." });

        await audit.Log(user.Username, "Login", "User", user.Id.ToString(), null);
        return Ok(new
        {
            token = tokens.Create(user),
            username = user.Username,
            modules = Modules.Normalize(user.ModuleAccess.Select(m => m.Module))
        });
    }

    // The app calls this on load so changes made by an administrator show up straight away.
    [HttpGet("me"), Authorize]
    public IActionResult Me() => Ok(new
    {
        username = User.Identity?.Name,
        modules = User.FindAll("module").Select(c => c.Value).ToList()
    });
}

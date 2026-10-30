using System.Text.RegularExpressions;
using LimsApi.Data;
using LimsApi.Models;
using LimsApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

// Access is granted by module only (no roles). Only users who hold the "Users and access" module get here.
// Nobody can change or remove their own account, so the person doing the change always keeps this module,
// and there is always at least one user who can manage access.
[ApiController, Route("api/users"), Authorize(Policy = Modules.UserManagement)]
public class UsersController(AppDbContext db, AuditService audit) : ControllerBase
{
    public record CreateUserReq(string Username, string Password, List<string>? ModuleNames);
    public record AccessReq(List<string>? ModuleNames);
    public record PasswordReq(string Password);

    string Me => User.Identity?.Name ?? "unknown";
    int MyId => int.TryParse(User.FindFirst("sub")?.Value, out var id) ? id : -1;

    static string? PasswordProblem(string? p)
    {
        if (string.IsNullOrEmpty(p) || p.Length < 8) return "Use at least 8 characters for the password.";
        if (p.Length > 72) return "The password can be at most 72 characters.";
        if (!p.Any(char.IsLetter) || !p.Any(char.IsDigit)) return "The password needs at least one letter and one number.";
        return null;
    }

    // Returns the cleaned module list, or an error message.
    static (List<string>? modules, string? error) CleanModules(List<string>? input)
    {
        var wanted = (input ?? new List<string>()).Distinct().ToList();
        if (wanted.Any(m => !Modules.All.Contains(m))) return (null, "Unknown module in the list.");
        var modules = Modules.Normalize(wanted);
        if (modules.Count == 0) return (null, "Tick at least one module.");
        return (modules, null);
    }

    [HttpGet("meta")]
    public IActionResult Meta() => Ok(new { modules = Modules.All });

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var users = await db.Users.Where(u => !u.IsDeleted).OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, Assigned = u.ModuleAccess.Select(m => m.Module).ToList() })
            .ToListAsync();
        return Ok(users.Select(u => new { u.Id, u.Username, modules = Modules.Normalize(u.Assigned) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserReq req)
    {
        var name = req.Username?.Trim() ?? "";
        if (!Regex.IsMatch(name, "^[A-Za-z0-9._-]{3,50}$"))
            return BadRequest(new { message = "Username must be 3 to 50 characters: letters, numbers, dot, dash or underscore." });
        if (PasswordProblem(req.Password) is { } pw) return BadRequest(new { message = pw });
        var (modules, error) = CleanModules(req.ModuleNames);
        if (error is not null) return BadRequest(new { message = error });
        if (await db.Users.AnyAsync(u => u.Username == name))
            return Conflict(new { message = "That username is already taken (it may belong to a removed user)." });

        var user = new AppUser { Username = name, PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password) };
        foreach (var m in modules!) user.ModuleAccess.Add(new UserModule { Module = m });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.Log(Me, "AddUser", "User", user.Id.ToString(), $"{name}; modules: {string.Join(", ", modules)}");
        return Ok(new { user.Id });
    }

    [HttpPut("{id:int}/access")]
    public async Task<IActionResult> SetAccess(int id, AccessReq req)
    {
        var user = await db.Users.Include(u => u.ModuleAccess).FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        if (user is null) return NotFound();
        if (user.Id == MyId)
            return BadRequest(new { message = "You cannot change your own access. Ask another user who has Users and access." });
        var (modules, error) = CleanModules(req.ModuleNames);
        if (error is not null) return BadRequest(new { message = error });

        var before = string.Join(", ", user.ModuleAccess.Select(m => m.Module).OrderBy(m => m));
        // Only remove and add what changed (a delete and re-insert of the same module in one save could clash with the unique index).
        var current = user.ModuleAccess.ToList();
        foreach (var m in current.Where(m => !modules!.Contains(m.Module))) db.UserModules.Remove(m);
        foreach (var name in modules!.Where(n => current.All(c => c.Module != n)))
            user.ModuleAccess.Add(new UserModule { Module = name });
        await db.SaveChangesAsync();
        await audit.Log(Me, "ChangeAccess", "User", user.Id.ToString(),
            $"{user.Username}: from [{before}] to [{string.Join(", ", modules.OrderBy(m => m))}]");
        return Ok(new { user.Id });
    }

    [HttpPost("{id:int}/password")]
    public async Task<IActionResult> ResetPassword(int id, PasswordReq req)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        if (user is null) return NotFound();
        if (PasswordProblem(req.Password) is { } pw) return BadRequest(new { message = pw });
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password);
        await db.SaveChangesAsync();
        await audit.Log(Me, "ResetPassword", "User", user.Id.ToString(), user.Username);
        return Ok(new { user.Id });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        if (user is null) return NotFound();
        if (user.Id == MyId) return BadRequest(new { message = "You cannot remove your own account." });

        user.IsDeleted = true;   // soft delete: history keeps pointing at a real name
        await db.SaveChangesAsync();
        await audit.Log(Me, "RemoveUser", "User", user.Id.ToString(), user.Username);
        return Ok(new { user.Id });
    }
}

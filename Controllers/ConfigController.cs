using LimsApi.Data;
using LimsApi.Models;
using LimsApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

// Domain knowledge (tests, parameters, units, limits) is data entered here,
// so a new laboratory type needs no code change.
[ApiController, Route("api/config"), Authorize]
public class ConfigController(AppDbContext db, AuditService audit) : ControllerBase
{
    public record DomainReq(string Name);
    public record TestTypeReq(int DomainId, string Name);
    public record ParameterReq(int TestTypeId, string Name, string? Unit, string? ResultType, string? Method);
    public record LimitReq(int ParameterId, string? StandardName, decimal? Lower, decimal? Upper);

    string Me => User.Identity?.Name ?? "unknown";

    // Matches the column sizes in AppDbContext.
    static string? TooLong(string? value, int max, string label) =>
        value is not null && value.Trim().Length > max ? $"{label} can be at most {max} characters." : null;

    [HttpGet("domains"), Authorize(Policy = Policies.ConfigRead)]
    public async Task<IActionResult> Domains()
    {
        var data = await db.Domains.OrderBy(d => d.Name).Select(d => new
        {
            d.Id, d.Name,
            TestTypes = d.TestTypes.OrderBy(t => t.Name).Select(t => new
            {
                t.Id, t.Name,
                Parameters = t.Parameters.OrderBy(p => p.Name).Select(p => new
                {
                    p.Id, p.Name, p.Unit, p.ResultType, p.Method,
                    StandardName = p.Specification != null ? p.Specification.StandardName : null,
                    Lower = p.Specification != null ? (decimal?)p.Specification.MinValue : null,
                    Upper = p.Specification != null ? (decimal?)p.Specification.MaxValue : null
                })
            })
        }).ToListAsync();
        return Ok(data);
    }

    [HttpPost("domains"), Authorize(Policy = Modules.TestSetup)]
    public async Task<IActionResult> AddDomain(DomainReq req)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { message = "Enter a domain name." });
        if (TooLong(name, 100, "Domain name") is { } err) return BadRequest(new { message = err });
        if (await db.Domains.AnyAsync(d => d.Name == name))
            return Conflict(new { message = "That domain already exists." });

        var d = new LabDomain { Name = name };
        db.Domains.Add(d);
        await db.SaveChangesAsync();
        await audit.Log(Me, "AddDomain", "Domain", d.Id.ToString(), d.Name);
        return Ok(new { d.Id });
    }

    [HttpPost("test-types"), Authorize(Policy = Modules.TestSetup)]
    public async Task<IActionResult> AddTestType(TestTypeReq req)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { message = "Enter a test name." });
        if (TooLong(name, 100, "Test name") is { } err) return BadRequest(new { message = err });
        if (!await db.Domains.AnyAsync(d => d.Id == req.DomainId))
            return BadRequest(new { message = "Choose a domain." });
        if (await db.TestTypes.AnyAsync(t => t.DomainId == req.DomainId && t.Name == name))
            return Conflict(new { message = "This domain already has a test with that name." });

        var t = new TestType { DomainId = req.DomainId, Name = name };
        db.TestTypes.Add(t);
        await db.SaveChangesAsync();
        await audit.Log(Me, "AddTestType", "TestType", t.Id.ToString(), t.Name);
        return Ok(new { t.Id });
    }

    [HttpPost("parameters"), Authorize(Policy = Modules.TestSetup)]
    public async Task<IActionResult> AddParameter(ParameterReq req)
    {
        var name = req.Name?.Trim() ?? "";
        var type = string.IsNullOrWhiteSpace(req.ResultType) ? ResultTypes.Numeric : req.ResultType.Trim();
        var unit = type == ResultTypes.Numeric ? req.Unit?.Trim() ?? "" : "";
        var method = string.IsNullOrWhiteSpace(req.Method) ? null : req.Method.Trim();
        if (!ResultTypes.All.Contains(type)) return BadRequest(new { message = "Choose a result type." });
        if (TooLong(method, 150, "Method") is { } e3) return BadRequest(new { message = e3 });
        if (name.Length == 0) return BadRequest(new { message = "Enter a parameter name." });
        if (TooLong(name, 100, "Parameter name") is { } e1) return BadRequest(new { message = e1 });
        if (TooLong(unit, 30, "Unit") is { } e2) return BadRequest(new { message = e2 });
        if (!await db.TestTypes.AnyAsync(t => t.Id == req.TestTypeId))
            return BadRequest(new { message = "Choose a test." });
        if (await db.Parameters.AnyAsync(p => p.TestTypeId == req.TestTypeId && p.Name == name))
            return Conflict(new { message = "This test already has a parameter with that name." });

        var p = new Parameter { TestTypeId = req.TestTypeId, Name = name, Unit = unit, ResultType = type, Method = method };
        db.Parameters.Add(p);
        await db.SaveChangesAsync();
        await audit.Log(Me, "AddParameter", "Parameter", p.Id.ToString(), $"{p.Name} ({type}{(unit.Length > 0 ? ", " + unit : "")}){(method is null ? "" : "; method " + method)}");
        return Ok(new { p.Id });
    }

    // Sets the limits of a parameter. Both a lower and an upper limit are always required.
    // If the parameter already has limits they are replaced; results already entered keep the limits they were checked against.
    [HttpPost("limits"), Authorize(Policy = Modules.TestSetup)]
    public async Task<IActionResult> SetLimits(LimitReq req)
    {
        if (req.Lower is null || req.Upper is null)
            return BadRequest(new { message = "Enter both a lower limit and an upper limit." });
        if (req.Lower >= req.Upper)
            return BadRequest(new { message = "The lower limit must be below the upper limit." });
        if (Math.Abs(req.Lower.Value) >= 100_000_000_000_000m || Math.Abs(req.Upper.Value) >= 100_000_000_000_000m)
            return BadRequest(new { message = "A limit is too large." });
        var std = string.IsNullOrWhiteSpace(req.StandardName) ? "Unnamed standard" : req.StandardName.Trim();
        if (TooLong(std, 150, "Standard name") is { } err) return BadRequest(new { message = err });

        var p = await db.Parameters.Include(x => x.Specification).FirstOrDefaultAsync(x => x.Id == req.ParameterId);
        if (p is null) return BadRequest(new { message = "Choose a parameter." });
        if (p.ResultType != ResultTypes.Numeric)
            return BadRequest(new { message = "Only numeric parameters have a lower and an upper limit." });

        string action, details;
        if (p.Specification is null)
        {
            p.Specification = new Specification { StandardName = std, MinValue = req.Lower.Value, MaxValue = req.Upper.Value };
            action = "SetLimits";
            details = $"{p.Name}: {req.Lower} to {req.Upper} ({std})";
        }
        else
        {
            details = $"{p.Name}: {p.Specification.MinValue} to {p.Specification.MaxValue} changed to {req.Lower} to {req.Upper} ({std})";
            p.Specification.StandardName = std;
            p.Specification.MinValue = req.Lower.Value;
            p.Specification.MaxValue = req.Upper.Value;
            action = "ChangeLimits";
        }
        await db.SaveChangesAsync();
        await audit.Log(Me, action, "Parameter", p.Id.ToString(), details);
        return Ok(new { p.Id });
    }
}

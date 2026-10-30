using LimsApi.Data;
using LimsApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

[ApiController, Route("api/audit"), Authorize(Policy = Modules.AuditTrail)]
public class AuditController(AppDbContext db) : ControllerBase
{
    // Newest first. Optional filters (all combine): sample ID, report ID, user, action.
    [HttpGet]
    public async Task<IActionResult> Latest(string? sample, string? report, string? user, string? act, int take = 200)
    {
        take = Math.Clamp(take, 1, 1000);
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(sample)) { var v = sample.Trim(); q = q.Where(a => a.SampleCode != null && a.SampleCode.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(report)) { var v = report.Trim(); q = q.Where(a => a.ReportNumber != null && a.ReportNumber.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(user)) { var v = user.Trim(); q = q.Where(a => a.Username.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(act)) { var v = act.Trim(); q = q.Where(a => a.Action.Contains(v)); }

        var items = await q.OrderByDescending(a => a.Id).Take(take)
            .Select(a => new { a.Id, a.Timestamp, a.Username, a.Action, a.Entity, a.EntityId, a.SampleCode, a.ReportNumber, a.Details })
            .ToListAsync();
        return Ok(items);
    }
}

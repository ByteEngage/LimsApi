using LimsApi.Data;
using LimsApi.Models;
using LimsApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

// A report exists once a sample is Finalized. It has a report ID (R-YYYY-0001) and the sample ID (S-YYYY-0001).
// The Reports module is self-contained: it does not need View samples.
[ApiController, Route("api/reports"), Authorize(Policy = Modules.Reports)]
public class ReportsController(AppDbContext db, AuditService audit) : ControllerBase
{
    string Me => User.Identity?.Name ?? "unknown";

    // Choices for the filter boxes.
    [HttpGet("filters")]
    public async Task<IActionResult> Filters()
    {
        var domains = await db.Domains.OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync();
        var tests = await db.TestTypes.Select(t => t.Name).Distinct().OrderBy(n => n).ToListAsync();
        var rows = await db.Samples.Where(s => s.Status == SampleStatus.Finalized)
            .Select(s => new { s.RegisteredBy, s.ReviewedBy, s.QcBy, s.ApprovedBy, s.FinalizedBy })
            .Distinct().ToListAsync();
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            foreach (var name in new[] { r.RegisteredBy, r.ReviewedBy, r.QcBy, r.ApprovedBy, r.FinalizedBy })
                if (!string.IsNullOrEmpty(name)) people.Add(name);
        return Ok(new { domains, tests, people = people.OrderBy(p => p).ToList() });
    }

    // All finalized reports, newest first. Every filter is optional and they combine (AND).
    // Dates are instants: the app sends the start of the chosen day in the user's own time zone.
    // "To" values are exclusive (the start of the day after the chosen day).
    [HttpGet]
    public async Task<IActionResult> List(
        string? search, int? domainId, string? test, string? client,
        string? addedBy, string? reviewedBy, string? qcBy, string? approvedBy, string? finalizedBy,
        string? result,
        DateTimeOffset? regFrom, DateTimeOffset? regTo, DateTimeOffset? repFrom, DateTimeOffset? repTo,
        int take = 200)
    {
        take = Math.Clamp(take, 1, 500);
        var q = db.Samples.AsNoTracking().Where(s => s.Status == SampleStatus.Finalized);

        if (domainId is not null) q = q.Where(s => s.DomainId == domainId);
        if (!string.IsNullOrWhiteSpace(test)) { var t = test.Trim(); q = q.Where(s => s.Tests.Any(x => x.TestType!.Name == t)); }
        if (!string.IsNullOrWhiteSpace(client)) { var c = client.Trim(); q = q.Where(s => s.ClientName.Contains(c)); }
        if (!string.IsNullOrWhiteSpace(addedBy)) { var v = addedBy.Trim(); q = q.Where(s => s.RegisteredBy == v); }
        if (!string.IsNullOrWhiteSpace(reviewedBy)) { var v = reviewedBy.Trim(); q = q.Where(s => s.ReviewedBy == v); }
        if (!string.IsNullOrWhiteSpace(qcBy)) { var v = qcBy.Trim(); q = q.Where(s => s.QcBy == v); }
        if (!string.IsNullOrWhiteSpace(approvedBy)) { var v = approvedBy.Trim(); q = q.Where(s => s.ApprovedBy == v); }
        if (!string.IsNullOrWhiteSpace(finalizedBy)) { var v = finalizedBy.Trim(); q = q.Where(s => s.FinalizedBy == v); }

        if (result == "ok") q = q.Where(s => !s.Tests.Any(t => t.Results.Any(r => !r.Conforming)));
        else if (result == "flagged") q = q.Where(s => s.Tests.Any(t => t.Results.Any(r => !r.Conforming)));

        if (regFrom is not null) { var d = regFrom.Value.UtcDateTime; q = q.Where(s => s.RegisteredAt >= d); }
        if (regTo is not null) { var d = regTo.Value.UtcDateTime; q = q.Where(s => s.RegisteredAt < d); }
        if (repFrom is not null) { var d = repFrom.Value.UtcDateTime; q = q.Where(s => s.FinalizedAt >= d); }
        if (repTo is not null) { var d = repTo.Value.UtcDateTime; q = q.Where(s => s.FinalizedAt < d); }

        // One box that searches every field.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var k = search.Trim();
            q = q.Where(s =>
                s.SampleCode.Contains(k) || (s.ReportNumber ?? "").Contains(k) ||
                s.ClientName.Contains(k) || s.Description.Contains(k) || s.Domain!.Name.Contains(k) ||
                s.RegisteredBy.Contains(k) || (s.ReviewedBy ?? "").Contains(k) || (s.QcBy ?? "").Contains(k) ||
                (s.ApprovedBy ?? "").Contains(k) || (s.FinalizedBy ?? "").Contains(k) ||
                s.Tests.Any(t => t.TestType!.Name.Contains(k) ||
                                 t.TestType.Parameters.Any(p => p.Name.Contains(k) || (p.Method ?? "").Contains(k))));
        }

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(s => s.FinalizedAt).ThenByDescending(s => s.Id).Take(take)
            .Select(s => new
            {
                s.Id, s.ReportNumber, s.SampleCode,
                Domain = s.Domain!.Name,
                s.ClientName, s.Description,
                s.RegisteredBy, s.RegisteredAt,
                s.ReviewedBy, s.QcBy, s.ApprovedBy, s.FinalizedBy, s.FinalizedAt,
                Tests = s.Tests.Select(t => t.TestType!.Name).ToList(),
                ResultCount = s.Tests.SelectMany(t => t.Results).Count(),
                Flagged = s.Tests.SelectMany(t => t.Results).Count(r => !r.Conforming)
            }).ToListAsync();

        return Ok(new { total, shown = items.Count, items });
    }

    // One report (the same data as the sample page). Opening a report is written to the audit trail.
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var s = await db.Samples.WithDetail().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.Status == SampleStatus.Finalized);
        if (s is null) return NotFound(new { message = "Report not found." });
        await audit.Log(Me, "ViewReport", "Report", id.ToString(), $"{s.ReportNumber} for {s.SampleCode}", s.SampleCode, s.ReportNumber);
        return Ok(SampleShape.From(s));
    }
}

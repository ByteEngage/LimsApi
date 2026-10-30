using LimsApi.Data;
using LimsApi.Models;

namespace LimsApi.Services;

public class AuditService(AppDbContext db)
{
    // Append-only: the API never updates or deletes audit rows.
    // sampleCode and reportNumber are the IDs people see (S-2026-0001, R-2026-0001), so the trail can be searched by them.
    public async Task Log(string user, string action, string entity, string entityId, string? details,
                          string? sampleCode = null, string? reportNumber = null)
    {
        if (details is { Length: > 1000 }) details = details[..1000];
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            Username = user,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            SampleCode = sampleCode,
            ReportNumber = reportNumber,
            Details = details
        });
        await db.SaveChangesAsync();
    }
}

using LimsApi.Data;
using LimsApi.Models;
using LimsApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

// Workflow: Registered -> Tested -> Reviewed -> QCChecked -> Approved -> Finalized
// A sample can be sent back to Registered (retest) by the reviewer or the QC checker.
// Each step needs its own module and nothing else: whoever holds the module can do the step,
// no matter who registered the sample, entered the results or did an earlier step.
[ApiController, Route("api/samples"), Authorize]
public class SamplesController(AppDbContext db, AuditService audit) : ControllerBase
{
    public record RegisterRequest(int DomainId, string Description, string ClientName, List<int> TestTypeIds);
    public record ResultInput(int ParameterId, decimal? Value, string? Choice);
    public record EnterResultsRequest(int SampleTestId, List<ResultInput> Results);
    public record DecisionRequest(bool Accept, string? Reason);

    const string ConflictMessage = "Someone else just changed this sample. Reload the page and try again.";

    string Me => User.Identity?.Name ?? "unknown";

    IQueryable<Sample> Full() => db.Samples.WithDetail();

    // Acceptance rule: Lp <= r <= Up. Both limits always exist.
    static bool Within(decimal value, Specification s) => value >= s.MinValue && value <= s.MaxValue;

    // SQL Server rowversion on Sample makes a stale save fail instead of overwriting.
    async Task<bool> TrySave()
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    void SendBack(Sample s, string stage, string reason)
    {
        s.Status = SampleStatus.Registered;
        foreach (var t in s.Tests) t.Done = false;
        s.ReturnNote = $"Sent back by {Me} ({stage}): {reason}";
        s.ReviewedBy = null;
        s.QcBy = null;
        s.ReviewNote = null;
        s.QcNote = null;
    }

    [HttpGet, Authorize(Policy = Modules.ViewSamples)]
    public async Task<IActionResult> List() =>
        Ok(await db.Samples.OrderByDescending(s => s.Id).Select(s => new
        {
            s.Id, s.SampleCode, s.ReportNumber, s.Status, s.ClientName, s.Description,
            Domain = s.Domain!.Name, s.RegisteredAt, s.RegisteredBy
        }).ToListAsync());

    [HttpGet("{id:int}"), Authorize(Policy = Modules.ViewSamples)]
    public async Task<IActionResult> Get(int id)
    {
        var s = await Full().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        return Ok(SampleShape.From(s));
    }

    [HttpPost, Authorize(Policy = Modules.RegisterSamples)]
    public async Task<IActionResult> Register(RegisterRequest req)
    {
        var client = req.ClientName?.Trim() ?? "";
        var desc = req.Description?.Trim() ?? "";
        if (client.Length == 0 || desc.Length == 0)
            return BadRequest(new { message = "Enter a client name and a sample description." });
        if (client.Length > 150) return BadRequest(new { message = "Client name can be at most 150 characters." });
        if (desc.Length > 500) return BadRequest(new { message = "Description can be at most 500 characters." });

        var wanted = req.TestTypeIds?.Distinct().ToList() ?? new List<int>();
        if (wanted.Count == 0) return BadRequest(new { message = "Select at least one test." });

        var types = await db.TestTypes.Include(t => t.Parameters).ThenInclude(p => p.Specification)
            .Where(t => wanted.Contains(t.Id) && t.DomainId == req.DomainId).ToListAsync();
        if (types.Count != wanted.Count)
            return BadRequest(new { message = "Some tests do not belong to the selected domain." });

        // Every test needs parameters, and every parameter needs BOTH limits.
        foreach (var t in types)
        {
            if (t.Parameters.Count == 0)
                return BadRequest(new { message = $"Test \"{t.Name}\" has no parameters yet. Ask an administrator to set it up." });
            var missing = t.Parameters.FirstOrDefault(p => p.ResultType == ResultTypes.Numeric && p.Specification is null);
            if (missing is not null)
                return BadRequest(new { message = $"Parameter \"{missing.Name}\" of test \"{t.Name}\" has no lower and upper limit. Ask an administrator to set them in Test setup." });
        }
        var typeIds = types.Select(t => t.Id).ToList();
        var typeNames = string.Join(", ", types.Select(t => t.Name));

        // Two people registering at the same moment can pick the same number.
        // The unique index rejects the second one, so we pick again.
        var prefix = $"S-{DateTime.UtcNow.Year}-";
        Sample sample = null!;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var next = await db.Samples.CountAsync(s => s.SampleCode.StartsWith(prefix)) + 1;
            sample = new Sample
            {
                SampleCode = $"{prefix}{next:D4}",
                DomainId = req.DomainId,
                Description = desc,
                ClientName = client,
                Status = SampleStatus.Registered,
                RegisteredAt = DateTime.UtcNow,
                RegisteredBy = Me
            };
            foreach (var tid in typeIds) sample.Tests.Add(new SampleTest { TestTypeId = tid });
            db.Samples.Add(sample);
            try { await db.SaveChangesAsync(); break; }
            catch (DbUpdateException) when (attempt < 3) { db.ChangeTracker.Clear(); }
        }

        await audit.Log(Me, "Register", "Sample", sample.Id.ToString(), $"{sample.SampleCode}; tests: {typeNames}", sample.SampleCode);
        return Ok(new { sample.Id, sample.SampleCode });
    }

    [HttpPost("{id:int}/results"), Authorize(Policy = Modules.ResultEntry)]
    public async Task<IActionResult> EnterResults(int id, EnterResultsRequest req)
    {
        var s = await Full().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (s.Status != SampleStatus.Registered)
            return Conflict(new { message = "Results can only be entered while the sample is Registered." });

        var test = s.Tests.FirstOrDefault(t => t.Id == req.SampleTestId);
        if (test is null) return BadRequest(new { message = "That test is not part of this sample." });

        var parameters = test.TestType!.Parameters;
        var inputs = req.Results ?? new List<ResultInput>();
        var distinct = inputs.Select(r => r.ParameterId).Distinct().Count() == inputs.Count;
        if (!distinct || inputs.Count != parameters.Count || inputs.Any(r => parameters.All(p => p.Id != r.ParameterId)))
            return BadRequest(new { message = "Enter exactly one result for every parameter of this test." });

        // Check every entry first, then replace the old results in one go.
        var built = new List<Result>();
        foreach (var r in inputs)
        {
            var p = parameters.First(x => x.Id == r.ParameterId);
            var result = new Result
            {
                ParameterId = p.Id,
                Method = p.Method,
                EnteredBy = Me,
                EnteredAt = DateTime.UtcNow
            };
            if (p.ResultType == ResultTypes.Numeric)
            {
                if (r.Value is null)
                    return BadRequest(new { message = $"Enter a value for \"{p.Name}\"." });
                if (Math.Abs(r.Value.Value) >= 100_000_000_000_000m)
                    return BadRequest(new { message = "One of the values is too large." });
                if (p.Specification is null)
                    return BadRequest(new { message = $"Parameter \"{p.Name}\" has no lower and upper limit. Ask an administrator to set them in Test setup." });
                result.Value = r.Value.Value;
                result.LowerLimit = p.Specification.MinValue;
                result.UpperLimit = p.Specification.MaxValue;
                result.Conforming = Within(r.Value.Value, p.Specification);
            }
            else
            {
                if (r.Choice is null || !ResultTypes.Choices(p.ResultType).Contains(r.Choice))
                    return BadRequest(new { message = $"Choose a result for \"{p.Name}\"." });
                result.TextValue = r.Choice;
                result.Conforming = r.Choice == ResultTypes.Expected(p.ResultType);
            }
            built.Add(result);
        }

        test.Results.Clear(); // re-entry after a send-back replaces the old values
        foreach (var result in built) test.Results.Add(result);
        test.Done = true;
        if (!await TrySave()) return Conflict(new { message = ConflictMessage });

        // Re-check from the database, so two analysts finishing the last two
        // tests at the same time still move the sample on.
        var allDone = await db.SampleTests.AsNoTracking().Where(t => t.SampleId == id).AllAsync(t => t.Done);
        if (allDone && s.Status == SampleStatus.Registered)
        {
            s.Status = SampleStatus.Tested;
            await TrySave(); // if this fails, another request already moved the sample on
        }

        var outside = test.Results.Count(r => !r.Conforming);
        await audit.Log(Me, "EnterResults", "Sample", id.ToString(),
            $"{test.TestType.Name}: {inputs.Count} results, {outside} flagged (outside limits or not the expected result)", s.SampleCode);
        return Ok(new { status = s.Status, outsideLimits = outside });
    }

    [HttpPost("{id:int}/review"), Authorize(Policy = Modules.Review)]
    public async Task<IActionResult> Review(int id, DecisionRequest req)
    {
        var s = await Full().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (s.Status != SampleStatus.Tested)
            return Conflict(new { message = "Only a Tested sample can be reviewed." });

        var reason = req.Reason?.Trim();
        if (reason is { Length: > 500 }) return BadRequest(new { message = "The note can be at most 500 characters." });

        if (!req.Accept)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return BadRequest(new { message = "Give a reason for sending the sample back." });
            SendBack(s, "review", reason);
            if (!await TrySave()) return Conflict(new { message = ConflictMessage });
            await audit.Log(Me, "ReviewReject", "Sample", id.ToString(), reason, s.SampleCode);
            return Ok(new { status = s.Status });
        }

        var anyOutside = s.Tests.SelectMany(t => t.Results).Any(r => !r.Conforming);
        if (anyOutside && string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { message = "Some results are outside their limits. Add a note to accept them." });

        s.Status = SampleStatus.Reviewed;
        s.ReviewedBy = Me;
        s.ReviewNote = reason;
        s.ReturnNote = null;
        if (!await TrySave()) return Conflict(new { message = ConflictMessage });
        await audit.Log(Me, "ReviewAccept", "Sample", id.ToString(), reason, s.SampleCode);
        return Ok(new { status = s.Status });
    }

    [HttpPost("{id:int}/qc"), Authorize(Policy = Modules.QC)]
    public async Task<IActionResult> QcCheck(int id, DecisionRequest req)
    {
        var s = await Full().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (s.Status != SampleStatus.Reviewed)
            return Conflict(new { message = "Only a Reviewed sample can go through the QC check." });

        var reason = req.Reason?.Trim();
        if (reason is { Length: > 500 }) return BadRequest(new { message = "The note can be at most 500 characters." });

        if (!req.Accept)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return BadRequest(new { message = "Give a reason for sending the sample back." });
            SendBack(s, "QC", reason);
            if (!await TrySave()) return Conflict(new { message = ConflictMessage });
            await audit.Log(Me, "QcReject", "Sample", id.ToString(), reason, s.SampleCode);
            return Ok(new { status = s.Status });
        }

        s.Status = SampleStatus.QCChecked;
        s.QcBy = Me;
        s.QcNote = reason;
        if (!await TrySave()) return Conflict(new { message = ConflictMessage });
        await audit.Log(Me, "QcAccept", "Sample", id.ToString(), reason, s.SampleCode);
        return Ok(new { status = s.Status });
    }

    [HttpPost("{id:int}/approve"), Authorize(Policy = Modules.Approval)]
    public async Task<IActionResult> Approve(int id)
    {
        var s = await Full().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (s.Status != SampleStatus.QCChecked)
            return Conflict(new { message = "Only a QC-checked sample can be approved." });

        s.Status = SampleStatus.Approved;
        s.ApprovedBy = Me;
        if (!await TrySave()) return Conflict(new { message = ConflictMessage });
        await audit.Log(Me, "Approve", "Sample", id.ToString(), s.SampleCode, s.SampleCode);
        return Ok(new { status = s.Status });
    }

    [HttpPost("{id:int}/finalize"), Authorize(Policy = Modules.ReportFinalizer)]
    public async Task<IActionResult> Finalize(int id)
    {
        var s = await db.Samples.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (s.Status != SampleStatus.Approved)
            return Conflict(new { message = "Only an Approved sample can have its report finalized." });

        s.Status = SampleStatus.Finalized;
        s.FinalizedBy = Me;
        s.FinalizedAt = DateTime.UtcNow;

        // The report ID (R-YYYY-0001). If two reports are finalized at the same moment and pick the same number,
        // the unique index refuses the second one and we pick the next number.
        var prefix = $"R-{DateTime.UtcNow.Year}-";
        for (var attempt = 1; ; attempt++)
        {
            var next = await db.Samples.CountAsync(x => x.ReportNumber != null && x.ReportNumber.StartsWith(prefix)) + 1;
            s.ReportNumber = $"{prefix}{next:D4}";
            try { await db.SaveChangesAsync(); break; }
            catch (DbUpdateConcurrencyException) { return Conflict(new { message = ConflictMessage }); }
            catch (DbUpdateException) when (attempt < 3) { /* number already taken: try the next one */ }
        }

        await audit.Log(Me, "FinalizeReport", "Report", id.ToString(), $"{s.ReportNumber} for {s.SampleCode}", s.SampleCode, s.ReportNumber);
        return Ok(new { status = s.Status, reportNumber = s.ReportNumber });
    }
}

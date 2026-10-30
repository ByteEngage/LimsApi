using LimsApi.Data;
using LimsApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Controllers;

// Worklist for the Result entry module: every test still waiting for results.
// Results are saved through POST api/samples/{id}/results.
[ApiController, Route("api/results"), Authorize(Policy = Modules.ResultEntry)]
public class ResultsController(AppDbContext db) : ControllerBase
{
    [HttpGet("worklist")]
    public async Task<IActionResult> Worklist()
    {
        var items = await db.SampleTests
            .Where(t => !t.Done && t.Sample!.Status == SampleStatus.Registered)
            .OrderBy(t => t.SampleId).ThenBy(t => t.Id)
            .Select(t => new
            {
                SampleTestId = t.Id,
                t.SampleId,
                SampleCode = t.Sample!.SampleCode,
                ClientName = t.Sample.ClientName,
                Domain = t.Sample.Domain!.Name,
                TestType = t.TestType!.Name,
                ReturnNote = t.Sample.ReturnNote,
                Parameters = t.TestType.Parameters.OrderBy(p => p.Id).Select(p => new
                {
                    p.Id, p.Name, p.Unit, p.ResultType, p.Method,
                    Lower = p.Specification != null ? (decimal?)p.Specification.MinValue : null,
                    Upper = p.Specification != null ? (decimal?)p.Specification.MaxValue : null
                })
            })
            .ToListAsync();
        return Ok(items);
    }
}

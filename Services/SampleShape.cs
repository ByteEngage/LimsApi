using LimsApi.Models;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Services;

public static class SampleQueries
{
    // Everything needed to show a sample or its report.
    public static IQueryable<Sample> WithDetail(this IQueryable<Sample> q) => q
        .Include(s => s.Domain)
        .Include(s => s.Tests).ThenInclude(t => t.TestType).ThenInclude(tt => tt!.Parameters).ThenInclude(p => p.Specification)
        .Include(s => s.Tests).ThenInclude(t => t.Results)
        .AsSplitQuery();
}

public static class SampleShape
{
    // The JSON shape of a sample page and of a report page (same data).
    public static object From(Sample s) => new
    {
        s.Id, s.SampleCode, s.ReportNumber, s.Status, s.ClientName, s.Description,
        Domain = s.Domain?.Name, s.RegisteredAt, s.RegisteredBy,
        s.ReviewedBy, s.QcBy, s.ApprovedBy, s.FinalizedBy, s.FinalizedAt,
        s.ReviewNote, s.QcNote, s.ReturnNote,
        Tests = s.Tests.OrderBy(t => t.Id).Select(t => new
        {
            t.Id, t.Done, TestType = t.TestType!.Name,
            Parameters = t.TestType.Parameters.OrderBy(p => p.Id).Select(p =>
            {
                var r = t.Results.FirstOrDefault(x => x.ParameterId == p.Id);
                var numeric = p.ResultType == ResultTypes.Numeric;
                return new
                {
                    p.Id, p.Name, p.Unit, p.ResultType,
                    Method = r?.Method ?? p.Method,
                    Expected = numeric ? null : ResultTypes.Expected(p.ResultType),
                    // Show the limits the result was checked against; before entry, the current limits.
                    Lower = !numeric ? null : r != null ? r.LowerLimit : p.Specification?.MinValue,
                    Upper = !numeric ? null : r != null ? r.UpperLimit : p.Specification?.MaxValue,
                    Result = r == null ? null : new { r.Value, r.TextValue, r.Conforming, r.EnteredBy }
                };
            })
        })
    };
}

namespace LimsApi.Models;

// Access is granted ONLY by module. There are no roles.
// An administrator ticks the modules each user may use.
public static class Modules
{
    public const string ViewSamples = "ViewSamples";
    public const string RegisterSamples = "RegisterSamples";
    public const string ResultEntry = "ResultEntry";
    public const string Review = "Review";
    public const string QC = "QC";
    public const string Approval = "Approval";
    public const string ReportFinalizer = "ReportFinalizer";
    public const string Reports = "Reports";
    public const string TestSetup = "TestSetup";
    public const string AuditTrail = "AuditTrail";
    public const string UserManagement = "UserManagement";

    public static readonly string[] All =
    {
        ViewSamples, RegisterSamples, ResultEntry, Review, QC, Approval,
        ReportFinalizer, Reports, TestSetup, AuditTrail, UserManagement
    };

    // Modules that work on samples need the sample pages, so they always include ViewSamples.
    static readonly string[] NeedViewSamples =
        { RegisterSamples, ResultEntry, Review, QC, Approval, ReportFinalizer };

    public static List<string> Normalize(IEnumerable<string> assigned)
    {
        var list = assigned.Where(m => All.Contains(m)).Distinct().ToList();
        if (list.Any(m => NeedViewSamples.Contains(m)) && !list.Contains(ViewSamples))
            list.Add(ViewSamples);
        return list;
    }
}

// Extra policies that are not a single module.
public static class Policies
{
    // Domains, tests and parameters are needed to register samples and to set up tests.
    public const string ConfigRead = "ConfigRead";
}

// How a parameter's result is recorded.
// Numeric: a number, checked against a lower and an upper limit (both required).
// PassFail: Pass or Fail (for example NDT inspections). Expected result: Pass.
// Presence: Absent or Detected (for example microbiological detection tests). Expected result: Absent.
public static class ResultTypes
{
    public const string Numeric = "Numeric";
    public const string PassFail = "PassFail";
    public const string Presence = "Presence";

    public static readonly string[] All = { Numeric, PassFail, Presence };

    public static string[] Choices(string type) =>
        type == PassFail ? new[] { "Pass", "Fail" }
        : type == Presence ? new[] { "Absent", "Detected" }
        : Array.Empty<string>();

    public static string Expected(string type) =>
        type == PassFail ? "Pass" : type == Presence ? "Absent" : "";
}

public static class SampleStatus
{
    public const string Registered = "Registered";
    public const string Tested = "Tested";
    public const string Reviewed = "Reviewed";
    public const string QCChecked = "QCChecked";
    public const string Approved = "Approved";
    public const string Finalized = "Finalized";
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    // Removed users are kept (hidden, cannot sign in) so old records and the audit trail stay meaningful.
    public bool IsDeleted { get; set; }
    public List<UserModule> ModuleAccess { get; set; } = new();
}

public class UserModule
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Module { get; set; } = "";
}

// ---- Configuration entities (domain knowledge lives here, not in code) ----
public class LabDomain
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<TestType> TestTypes { get; set; } = new();
}

public class TestType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DomainId { get; set; }
    public LabDomain? Domain { get; set; }
    public List<Parameter> Parameters { get; set; } = new();
}

public class Parameter
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string ResultType { get; set; } = ResultTypes.Numeric;
    public string? Method { get; set; }   // test method or standard, for example "IS 15185:2016"
    public int TestTypeId { get; set; }
    public TestType? TestType { get; set; }
    // Numeric parameters: exactly one limit pair per parameter, and both limits are always required.
    public Specification? Specification { get; set; }
}

public class Specification
{
    public int Id { get; set; }
    public int ParameterId { get; set; }
    public Parameter? Parameter { get; set; }
    public string StandardName { get; set; } = "";
    public decimal MinValue { get; set; }   // lower limit
    public decimal MaxValue { get; set; }   // upper limit
}

// ---- Operational entities ----
public class Sample
{
    public int Id { get; set; }
    public string SampleCode { get; set; } = "";
    public int DomainId { get; set; }
    public LabDomain? Domain { get; set; }
    public string Description { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Status { get; set; } = SampleStatus.Registered;
    public DateTime RegisteredAt { get; set; }
    public string RegisteredBy { get; set; } = "";
    public string? ReviewedBy { get; set; }
    public string? QcBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public string? ReportNumber { get; set; }   // report ID, given when the report is finalized, for example R-2026-0001
    public string? ReviewNote { get; set; }
    public string? QcNote { get; set; }
    public string? ReturnNote { get; set; }   // why the sample was last sent back for retest
    // SQL Server rowversion: detects two users changing the same sample at once.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<SampleTest> Tests { get; set; } = new();
}

public class SampleTest
{
    public int Id { get; set; }
    public int SampleId { get; set; }
    public Sample? Sample { get; set; }
    public int TestTypeId { get; set; }
    public TestType? TestType { get; set; }
    public bool Done { get; set; }
    public List<Result> Results { get; set; } = new();
}

public class Result
{
    public int Id { get; set; }
    public int SampleTestId { get; set; }
    public SampleTest? SampleTest { get; set; }
    public int ParameterId { get; set; }
    public Parameter? Parameter { get; set; }
    public decimal Value { get; set; }           // numeric results
    public string? TextValue { get; set; }       // Pass/Fail or Absent/Detected results
    public string? Method { get; set; }          // method used, copied at entry time
    // Limits are copied at entry time, so later changes to Test setup never rewrite old results.
    public decimal LowerLimit { get; set; }
    public decimal UpperLimit { get; set; }
    public bool Conforming { get; set; }
    public string EnteredBy { get; set; } = "";
    public DateTime EnteredAt { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? SampleCode { get; set; }     // sample ID shown to users, for example S-2026-0001
    public string? ReportNumber { get; set; }   // report ID, for example R-2026-0001
    public string? Details { get; set; }
}

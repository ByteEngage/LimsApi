using LimsApi.Models;

namespace LimsApi.Data;

public static class SeedData
{
    // The 13 testing services of the laboratory (Ghaziabad Testing Laboratories).
    // They are added on every start if they are missing, so existing databases get them too.
    static readonly string[] LabDomains =
    {
        "Electrical Testing", "Building Material", "Road Material", "Leather & Textiles",
        "Food Products", "Pharmaceuticals", "Cosmetics & Personal Care", "Water Testing",
        "Microbiological", "Metal & Alloys", "Soil & Geotechnical", "Non-Destructive", "Environmental"
    };

    public static void Run(AppDbContext db)
    {
        if (!db.Users.Any()) SeedUsers(db);
        EnsureDomains(db);
    }

    // Demo passwords are all "Pass@123": change them in Users and access before sharing the app.
    // Each demo user holds only the modules needed for one job, to show how access works.
    static void SeedUsers(AppDbContext db)
    {
        AppUser U(string name, params string[] modules)
        {
            var user = new AppUser { Username = name, PasswordHash = BCrypt.Net.BCrypt.HashPassword("Pass@123") };
            foreach (var m in Modules.Normalize(modules)) user.ModuleAccess.Add(new UserModule { Module = m });
            return user;
        }

        db.Users.AddRange(
            U("admin", Modules.All),
            U("reception", Modules.RegisterSamples),
            U("analyst", Modules.ResultEntry),
            U("reviewer", Modules.Review),
            U("qc", Modules.QC),
            U("approver", Modules.Approval),
            U("finalizer", Modules.ReportFinalizer, Modules.Reports)
        );
        db.SaveChanges();
    }

    static void EnsureDomains(AppDbContext db)
    {
        var existing = db.Domains.Select(d => d.Name).ToList().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in LabDomains)
        {
            if (existing.Contains(name)) continue;
            var domain = new LabDomain { Name = name };
            foreach (var t in StarterTests(name)) domain.TestTypes.Add(t);
            db.Domains.Add(domain);
        }
        db.SaveChanges();
    }

    // A few starter tests so the demo works. Everything else is set up by the administrator in Test setup.
    // Numeric limits here are placeholders for demonstration, NOT real standard values.
    // Methods are taken from the laboratory's published list. Confirm the expected results with the laboratory.
    static IEnumerable<TestType> StarterTests(string domain)
    {
        TestType Numeric(string test, string parameter, string unit, decimal lower, decimal upper) => new()
        {
            Name = test,
            Parameters =
            {
                new Parameter
                {
                    Name = parameter, Unit = unit, ResultType = ResultTypes.Numeric,
                    Specification = new Specification { StandardName = "Demo limit", MinValue = lower, MaxValue = upper }
                }
            }
        };
        TestType Detect(string test, string parameter, string method) => new()
        {
            Name = test,
            Parameters = { new Parameter { Name = parameter, ResultType = ResultTypes.Presence, Method = method } }
        };
        TestType Inspect(string test) => new()
        {
            Name = test,
            Parameters = { new Parameter { Name = "Acceptance", ResultType = ResultTypes.PassFail } }
        };

        switch (domain)
        {
            case "Building Material":
                yield return Numeric("Concrete cube test", "Compressive strength", "MPa", 25, 60);
                break;
            case "Food Products":
                yield return Numeric("Moisture analysis", "Moisture content", "% (mass)", 0, 14);
                break;
            case "Water Testing":
                yield return Numeric("Water quality", "pH", "pH units", 6.5m, 8.5m);
                break;
            case "Microbiological":
                yield return Detect("Detection of E. coli", "E. coli", "IS 15185:2016");
                yield return Detect("Detection of coliform", "Coliform", "IS 15185:2016");
                yield return Detect("Detection of Salmonella spp.", "Salmonella spp.", "IS 5887 (Part 3) Sec.1: 2020");
                yield return Detect("Detection of Staphylococcus aureus", "Staphylococcus aureus", "IS 5887 (Part II): 1976");
                break;
            case "Non-Destructive":
                yield return Inspect("Visual testing (VT)");
                yield return Inspect("Liquid penetrant testing (PT)");
                yield return Inspect("Magnetic particle testing (MT)");
                yield return Inspect("Ultrasonic testing (UT)");
                yield return Inspect("Radiographic testing (RT)");
                break;
        }
    }
}

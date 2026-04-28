using Smplkit;
using Smplkit.Management;

/// <summary>
/// Smpl SDK — Management API Showcase
/// =====================================
///
/// Demonstrates <c>client.Management.*</c>:
///
///   1. SDK Initialization
///   2. Environments — list, create (AD_HOC), update in place
///   3. Context Types — create with attributes, list, mutate
///   4. Contexts — register (immediate flush), list by type, get, delete
///   5. Account Settings — read, mutate, save
///   6. Cleanup
/// </summary>
public static class ManagementShowcase
{
    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();
    }

    private static void Step(string description)
    {
        Console.WriteLine($"  -> {description}");
    }

    public static async Task<int> RunAsync(SmplClient client)
    {
        // ==============================================================
        // 1. SDK INITIALIZATION
        // ==============================================================
        Section("1. SDK Initialization");
        Step("SmplClient initialized");

        // Best-effort cleanup of leftovers from previous runs
        foreach (var envId in new[] { "preview_acme" })
        {
            try { await client.Management.Environments.DeleteAsync(envId); } catch { /* not found is fine */ }
        }
        foreach (var ctId in new[] { "user", "account", "device" })
        {
            try { await client.Management.ContextTypes.DeleteAsync(ctId); } catch { /* not found is fine */ }
        }

        // ==============================================================
        // 2a. LIST BUILT-IN ENVIRONMENTS
        // ==============================================================
        Section("2a. List Built-In Environments");

        var environments = await client.Management.Environments.ListAsync();
        foreach (var e in environments)
        {
            Step($"id={e.Id,-20} name={e.Name,-20} classification={e.Classification}");
        }

        // ==============================================================
        // 2b. CREATE AN AD_HOC ENVIRONMENT
        // ==============================================================
        Section("2b. Create an AD_HOC Environment");

        var preview = client.Management.Environments.New(
            "preview_acme",
            name: "Preview — Acme branch",
            color: "#8b5cf6",
            classification: EnvironmentClassification.AdHoc);
        await preview.SaveAsync();
        Step($"Created: id={preview.Id}, classification={preview.Classification}");

        // ==============================================================
        // 2c. UPDATE AN ENVIRONMENT IN PLACE
        // ==============================================================
        Section("2c. Update an Environment In Place");

        var prod = await client.Management.Environments.GetAsync("production");
        var originalColor = prod.Color;
        prod.Color = "#ef4444";
        await prod.SaveAsync();
        Step($"Updated: id={prod.Id}, color={prod.Color}");

        // Restore original color
        prod.Color = originalColor;
        await prod.SaveAsync();
        Step($"Restored: id={prod.Id}, color={prod.Color}");

        // ==============================================================
        // 3a. CREATE CONTEXT TYPES
        // ==============================================================
        Section("3a. Create Context Types");

        var userCt = client.Management.ContextTypes.New("user", name: "User");
        userCt.AddAttribute("plan");
        userCt.AddAttribute("region");
        userCt.AddAttribute("beta_tester");
        userCt.AddAttribute("signup_date");
        userCt.AddAttribute("account_age_days");
        await userCt.SaveAsync();
        Step($"user: attributes=[{string.Join(", ", userCt.Attributes.Keys)}]");

        var accountCt = client.Management.ContextTypes.New("account", name: "Account");
        foreach (var attr in new[] { "tier", "industry", "region", "employee_count", "annual_revenue" })
            accountCt.AddAttribute(attr);
        await accountCt.SaveAsync();

        var deviceCt = client.Management.ContextTypes.New("device", name: "Device");
        foreach (var attr in new[] { "os", "version", "type" })
            deviceCt.AddAttribute(attr);
        await deviceCt.SaveAsync();
        Step("account, device created");

        // ==============================================================
        // 3b. LIST + MUTATE AN EXISTING CONTEXT TYPE
        // ==============================================================
        Section("3b. List + Mutate an Existing Context Type");

        var allTypes = await client.Management.ContextTypes.ListAsync();
        foreach (var t in allTypes)
        {
            Step($"id={t.Id,-20} name={t.Name}");
        }

        var existing = await client.Management.ContextTypes.GetAsync("user");
        existing.AddAttribute("lifetime_value");
        existing.RemoveAttribute("account_age_days");
        await existing.SaveAsync();
        Step($"user attributes now: [{string.Join(", ", existing.Attributes.Keys)}]");

        // ==============================================================
        // 4a. REGISTER CONTEXTS (IMMEDIATE FLUSH)
        // ==============================================================
        Section("4a. Register Contexts (Immediate Flush)");

        await client.Management.Contexts.RegisterAsync(
            new[]
            {
                new Context("user", "usr_a1b2c3", new Dictionary<string, object?> { ["plan"] = "free", ["region"] = "us" }),
                new Context("user", "usr_d4e5f6", new Dictionary<string, object?> { ["plan"] = "enterprise", ["region"] = "eu" }),
                new Context("account", "acct_acme_inc", new Dictionary<string, object?> { ["tier"] = "enterprise", ["industry"] = "retail" }),
            },
            flush: true);
        Step("3 contexts registered + flushed");

        // ==============================================================
        // 4b. LIST CONTEXTS OF A SINGLE TYPE
        // ==============================================================
        Section("4b. List Contexts of a Single Type");

        var userContexts = await client.Management.Contexts.ListAsync("user");
        foreach (var c in userContexts)
        {
            Step($"  type={c.Type,-10} key={c.Key,-20} attributes={string.Join(", ", c.Attributes.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        // ==============================================================
        // 4c. GET + DELETE BY COMPOSITE ID (OR BY (TYPE, KEY))
        // ==============================================================
        Section("4c. Get + Delete by Composite ID");

        try
        {
            var one = await client.Management.Contexts.GetAsync("user:usr_a1b2c3");
            Step($"got: {one.Type}:{one.Key}");

            var same = await client.Management.Contexts.GetAsync("user", "usr_a1b2c3");
            Step($"got via (type, key): {same.Type}:{same.Key}");

            await client.Management.Contexts.DeleteAsync("user:usr_a1b2c3");
            Step("deleted user:usr_a1b2c3");
        }
        catch (Exception ex)
        {
            Step($"Context get/delete skipped: {ex.Message}");
        }

        // ==============================================================
        // 5a. READ ACCOUNT SETTINGS
        // ==============================================================
        Section("5a. Read Account Settings");

        var settings = await client.Management.AccountSettings.GetAsync();
        Step($"environment_order={string.Join(", ", settings.EnvironmentOrder)}");
        Step($"raw keys: [{string.Join(", ", settings.Raw.Keys)}]");

        // ==============================================================
        // 5b. MUTATE + SAVE (ACTIVE RECORD)
        // ==============================================================
        Section("5b. Mutate + Save (Active Record)");

        settings.EnvironmentOrder = new List<string> { "production", "staging", "development" };
        await settings.SaveAsync();
        Step($"saved: environment_order=[{string.Join(", ", settings.EnvironmentOrder)}]");

        // ==============================================================
        // 6. CLEANUP
        // ==============================================================
        Section("6. Cleanup");

        foreach (var c in await client.Management.Contexts.ListAsync("user"))
        {
            try { await client.Management.Contexts.DeleteAsync(c.Type, c.Key); } catch { }
        }
        foreach (var c in await client.Management.Contexts.ListAsync("account"))
        {
            try { await client.Management.Contexts.DeleteAsync(c.Type, c.Key); } catch { }
        }

        foreach (var ctId in new[] { "user", "account", "device" })
        {
            try { await client.Management.ContextTypes.DeleteAsync(ctId); } catch { }
        }

        try { await client.Management.Environments.DeleteAsync("preview_acme"); } catch { }

        Section("Management Showcase Complete");
        return 0;
    }
}

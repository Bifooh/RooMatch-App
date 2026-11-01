// Lodge MVP fictional data generator.
// Deterministic: same --seed and --size always produce byte-identical output.
// ALL names and records are FICTIONAL. Do not feed real resident data into this tool.
//
// Usage:
//   dotnet run -- --size small --seed 2027 --out ../../db
//   dotnet run -- --size large --seed 2027 --out ../../db
//   dotnet run -- --size 250   --seed 7    --out /tmp/x --name custom
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lodge.DataGen;

/// <summary>SplitMix64: tiny, portable, deterministic PRNG (independent of .NET's Random implementation).</summary>
sealed class Rng(ulong seed)
{
    private ulong _s = seed;
    public ulong NextU64() { ulong z = (_s += 0x9E3779B97F4A7C15UL); z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return z ^ (z >> 31); }
    public int Next(int maxExclusive) => (int)(NextU64() % (ulong)maxExclusive);
    public int Range(int minIncl, int maxIncl) => minIncl + Next(maxIncl - minIncl + 1);
    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));
    public bool Chance(double p) => NextDouble() < p;
    public T Pick<T>(IReadOnlyList<T> xs) => xs[Next(xs.Count)];
    public void Shuffle<T>(IList<T> xs) { for (int i = xs.Count - 1; i > 0; i--) { int j = Next(i + 1); (xs[i], xs[j]) = (xs[j], xs[i]); } }
}

record FloorPlan(int Id, string Code, string Name, string Variant, int Bedrooms, decimal Bathrooms, string Category, int Capacity, bool CapacityIsAssumption, string Notes);
class Apartment { public int Id; public string Number = ""; public int PlanId; public string Building = ""; public int Floor; public int? CapOverride; public string? OverrideReason; }
class Tenant { public int Id; public string Code = ""; public string First = ""; public string Last = ""; public string Full => $"{First} {Last}"; }
class Contract { public int Id; public int TenantId; public int PlanId; public bool Renewing; public int? CurrentAptId; public string? Stay; public bool IsEdge; }
class Pref { public string Sleep = "unknown"; public bool Animal; public bool Allergy; public int? Age; public string? Social; public string? Major; public string? Stage; }
class Request { public int Id; public int ContractId; public string Raw = ""; }
class Restriction { public int Id; public int ContractId; public string Type = ""; public int? AptId; public int? OtherTenantId; public string? Value; public string Description = ""; }
class Assignment { public int Id; public int DraftId; public int ContractId; public int AptId; public bool Locked; public string Source = "manual"; public string Explanation = ""; }
class EdgeCase { public string Id = ""; public string Kind = ""; public string Description = ""; public List<string> TenantCodes = new(); public List<string> Apartments = new(); public string ExpectedHandling = ""; }

static class Program
{
    const int TargetTermId = 2; // W2027
    static readonly string[] Firsts = {
        "Avery","Blake","Cameron","Dakota","Emerson","Finley","Harper","Indigo","Jules","Kai","Logan","Marlow",
        "Noel","Oakley","Parker","Quinn","Reese","Sage","Tatum","Umber","Vale","Wren","Xen","Yael","Zephyr",
        "Ashby","Briar","Cove","Dune","Ember","Fable","Grove","Haven","Ira","Juniper","Kestrel","Lark","Moss",
        "Nova","Orion","Piper","Rook","Skye","Thorne","Vesper","Willow","Arlo","Bexley","Cyrus","Dallin" };
    // Two-part, deliberately silly surnames so nobody can mistake them for real residents.
    static readonly string[] SurA = { "Wobble","Thistle","Quill","Marrow","Copper","Fizz","Glimmer","Noodle","Brindle","Pebble",
        "Sprocket","Moon","Tumble","Zigzag","Pickle","Biscuit","Snorkel","Puddle","Crumpet","Waffle","Jelly","Bumble","Doodle","Kazoo" };
    static readonly string[] SurB = { "sprocket","whump","bottom","wick","fern","fizzle","quark","beam","nugget","bean",
        "toes","whistle","berry","socks","muffin","hopper","boots","crumb","worth","tooth" };
    static readonly string[] Majors = { "Computer Science","Software Engineering","Nursing","Business Management","Exercise Science",
        "Elementary Education","Psychology","Accounting","Graphic Design","Biology","Mechanical Engineering","Communication","Music","Undeclared" };
    const string NonexistentName = "Rowan Glimmerpatch"; // "patch" is not a SurB part, so this can never be generated.

    static Rng rng = null!;
    static readonly List<FloorPlan> Plans = new();
    static readonly List<Apartment> Apts = new();
    static readonly List<Tenant> Tenants = new();
    static readonly List<Contract> Contracts = new();
    static readonly Dictionary<int, Pref> Prefs = new();
    static readonly List<Request> Requests = new();
    static readonly List<Restriction> Restrictions = new();
    static readonly List<Assignment> Assignments = new();
    static readonly List<EdgeCase> Edges = new();
    static readonly HashSet<string> UsedNames = new();
    static readonly Dictionary<string, List<int>> Noise = new();

    static int Main(string[] args)
    {
        string size = "small"; ulong seed = 2027; string outDir = "."; string? name = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--size": size = args[++i]; break;
                case "--seed": seed = ulong.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[++i]; break;
                case "--name": name = args[++i]; break;
                case "-h": case "--help": Console.WriteLine("--size small|large|<N>  --seed <n>  --out <dir>  [--name <label>]"); return 0;
                default: Console.Error.WriteLine($"Unknown arg {args[i]}"); return 2;
            }
        }
        int n = size switch { "small" => 32, "large" => 1100, _ => int.Parse(size, CultureInfo.InvariantCulture) };
        name ??= size;
        rng = new Rng(seed);
        try { Generate(n); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine("Generation failed: " + ex.Message); return 1; }
        Directory.CreateDirectory(outDir);
        var sqlPath = Path.Combine(outDir, $"seed_{name}.sql");
        var keyPath = Path.Combine(outDir, $"seed_{name}_answer_key.json");
        File.WriteAllText(sqlPath, BuildSql(n, seed, name), new UTF8Encoding(false));
        File.WriteAllText(keyPath, BuildAnswerKey(n, seed), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {sqlPath} and {keyPath}: {Tenants.Count} tenants, {Apts.Count} apartments, {Requests.Count} roommate requests, {Edges.Count} edge cases.");
        return 0;
    }

    // ------------------------------------------------------------------ generation
    static FloorPlan P(string code) => Plans.First(p => p.Code == code);

    static void Generate(int n)
    {
        // Floor plans: REAL Lodge plan names/bed/bath (from the Lodge application form).
        // resident_capacity is an explicit per-plan value, set here on purpose and NEVER computed from bedrooms.
        // Source: Samuel/manager input, 2026-10-31 ("LODGE UPDATE"): every bedroom holds 2 by default, giving the
        // per-plan numbers below, so capacity_is_assumption = FALSE for them. Men's Standard 3bd (M-STD-3) was left
        // blank at first and then confirmed as 6 by Samuel/manager the same day, so all 10 plans are confirmed.
        // Private bedrooms (a tenant paying for a bedroom + bathroom alone) lower the real headcount per apartment;
        // that is not modelled in the seeds yet (see docs/assignment-algorithm.md, proposed schema changes).
        var defs = new (string Code, string Name, string Variant, int Bed, decimal Bath, string Cat, int Cap, bool Assumed)[] {
            ("W-STD-2",  "Women's - Standard. 2 Bedroom, 2 Bath",          "standard",           2, 2.0m, "women", 4, false),
            ("W-OVR-2",  "Women's - Oversized. 2 Bedroom, 2 Bath",         "oversized",          2, 2.0m, "women", 4, false),
            ("W-STD-3",  "Women's - Standard. 3 Bedroom, 3 Bath",          "standard",           3, 3.0m, "women", 6, false),
            ("W-FLAT-4", "Women's - Flat. 4 Bedroom, 4 Bath",              "flat",               4, 4.0m, "women", 8, false),
            ("W-STH-4",  "Women's - Standard Townhome. 4 Bedroom, 4 Bath", "standard_townhome",  4, 4.0m, "women", 8, false),
            ("W-OTH-4",  "Women's - Oversized Townhome. 4 Bedroom, 4 Bath","oversized_townhome", 4, 4.0m, "women", 8, false),
            ("M-STD-3",  "Men's - Standard. 3 Bedroom, 3 Bath",            "standard",           3, 3.0m, "men",   6, false),
            ("M-FLAT-4", "Men's - Flat. 4 Bedroom, 4 Bath",                "flat",               4, 4.0m, "men",   8, false),
            ("M-STH-4",  "Men's - Standard Townhome. 4 Bedroom, 4 Bath",   "standard_townhome",  4, 4.0m, "men",   8, false),
            ("M-OTH-4",  "Men's - Oversized Townhome. 4 Bedroom, 4 Bath",  "oversized_townhome", 4, 4.0m, "men",   8, false),
        };
        int pid = 1;
        foreach (var d in defs)
            Plans.Add(new FloorPlan(pid++, d.Code, d.Name, d.Variant, d.Bed, d.Bath, d.Cat, d.Cap, d.Assumed,
                d.Assumed ? "Real plan name; resident_capacity ASSUMED, confirm with staff"
                : d.Code == "M-STD-3" ? "Real plan name; resident_capacity confirmed by Samuel/manager 2026-10-31"
                          : "Real plan name; resident_capacity from Samuel/manager input 2026-10-31 (2 per bedroom by default)"));
        const string Oversubscribed = "M-OTH-4";

        // ---- edge-case tenants (identical structure for every size) ----
        var pend = new List<Action>(); // things that need apartments to exist first

        // E1 roommate group larger than capacity (W-STD-2 capacity 4, group of 5, all mutual)
        var g = Enumerable.Range(0, 5).Select(_ => NewContract("W-STD-2", edge: true)).ToList();
        foreach (var a in g) foreach (var b in g) if (a != b) AddRequest(a, T(b).Full);
        Edge("E01", "group_exceeds_capacity", "Five tenants all request each other but W-STD-2 holds 4 per apartment (2 per bedroom, per Samuel/manager).", g,
            "Cannot place all five together; flag to review queue, keep requests, do not silently split without explanation.");

        // E2 animal + allergy conflict inside a mutual request pair
        var an = NewContract("M-STD-3", edge: true); var al = NewContract("M-STD-3", edge: true);
        AddRequest(an, T(al).Full); AddRequest(al, T(an).Full);
        pend.Add(() => { Prefs[an.Id].Animal = true; Prefs[al.Id].Allergy = true; Prefs[an.Id].Allergy = false; Prefs[al.Id].Animal = false; });
        Edge("E02", "animal_allergy_conflict", "Mutual roommate request, but one tenant has an animal and the other has an animal-allergy restriction.",
            new() { an, al }, "Must not share an apartment; flag the request with the reason.");

        // E3 misspelled roommate name
        var c3 = NewContract("W-STD-3", edge: true); var d3 = NewContract("W-STD-3", edge: true);
        string miss = Misspell(T(d3));
        AddRequest(c3, miss); AddRequest(d3, T(c3).Full);
        Edge("E03", "misspelled_name", $"Request text '{miss}' is a misspelling of '{T(d3).Full}' ({T(d3).Code}); the reverse request is spelled correctly.",
            new() { c3, d3 }, "Suggest the likely match for staff confirmation; keep the original wording; do not auto-resolve without confidence.");

        // E4 request naming a person who does not exist
        var e4 = NewContract("W-OVR-2", edge: true);
        AddRequest(e4, NonexistentName);
        Edge("E04", "nonexistent_person", $"Requests '{NonexistentName}', who has no tenant record.", new() { e4 },
            "Unresolved; review queue with original wording.");

        // E5 non-mutual request
        // Lives in M-OTH-4 (was M-STH-4) so the small set can still oversubscribe M-OTH-4 (capacity 8) within 32 tenants.
        var f5 = NewContract("M-OTH-4", edge: true); var g5 = NewContract("M-OTH-4", edge: true);
        AddRequest(f5, T(g5).Full);
        Edge("E05", "non_mutual_request", "First tenant requests the second; the second made no request.", new() { f5, g5 },
            "Valid but weaker; attempt if it does not hurt others; show as non-mutual.");

        // E6 ambiguous first-name-only request (two Jordans)
        var h6 = NewContract("M-FLAT-4", edge: true);
        var j1 = NewContract("M-FLAT-4", edge: true, first: "Jordan"); var j2 = NewContract("M-FLAT-4", edge: true, first: "Jordan");
        AddRequest(h6, "Jordan");
        Edge("E06", "ambiguous_name", "Request just says 'Jordan'; two tenants with first name Jordan share that floor plan.", new() { h6, j1, j2 },
            "Ambiguous; do not guess; review queue lists both candidates.");

        // E7 request across housing categories
        var i7 = NewContract("W-STD-3", edge: true); var k7 = NewContract("M-STD-3", edge: true);
        AddRequest(i7, T(k7).Full);
        Edge("E07", "cross_category_request", "A women's-plan tenant requests a men's-plan tenant by full name.", new() { i7, k7 },
            "Ineligible to share; exclude with reason, keep in review queue.");

        // E8 mutual request but different contracted floor plans
        var k8 = NewContract("M-STD-3", edge: true); var l8 = NewContract("M-OTH-4", edge: true);
        AddRequest(k8, T(l8).Full); AddRequest(l8, T(k8).Full);
        Edge("E08", "different_floor_plan_request", "Mutual request, but the two tenants contracted different floor plans.", new() { k8, l8 },
            "Cannot share without a contract change; flag, do not change contracts automatically.");

        // E9 renewals who want to stay together in their current apartment
        var s1 = NewContract("W-STD-3", edge: true); var s2 = NewContract("W-STD-3", edge: true);
        AddRequest(s1, T(s2).Full); AddRequest(s2, T(s1).Full);
        pend.Add(() => { var apt = AptsOf("W-STD-3").First(); MakeRenewal(s1, apt, "stay"); MakeRenewal(s2, apt, "stay");
            Edge("E09", "renewal_stay", $"Two renewing tenants live in {apt.Number} and both want to stay.", new() { s1, s2 },
                "Prefer keeping both in their current apartment.", apt); });

        // E10 renewal who wants to move
        var m10 = NewContract("M-STD-3", edge: true);
        pend.Add(() => { var apt = AptsOf("M-STD-3").First(); MakeRenewal(m10, apt, "move");
            Edge("E10", "renewal_move", $"Renewing tenant in {apt.Number} wants to move.", new() { m10 },
                "Move to a different apartment of the same plan, preferring the same floor and the same ward (wards not in the data yet); an adjacent floor is an unconfirmed fallback.", apt); });

        // E11 renewal whose current apartment's floor plan differs from the new contract
        var n11 = NewContract("M-FLAT-4", edge: true);
        pend.Add(() => { var apt = AptsOf("M-STD-3").Skip(1).First(); MakeRenewal(n11, apt, "stay");
            Edge("E11", "renewal_floor_plan_changed", $"Renewing tenant lives in {apt.Number} (M-STD-3) and wants to stay, but contracted M-FLAT-4.", new() { n11 },
                "Staying is impossible under the contract; flag it and propose M-FLAT-4 apartments on the same floor (and ward, once known), then adjacent floors.", apt); });

        // E12 staff-confirmed floor restriction + manager-locked placement
        var p12 = NewContract("M-STD-3", edge: true);
        // E13 manager-locked renewal stay
        var q13 = NewContract("W-OVR-2", edge: true);
        pend.Add(() =>
        {
            var used = Contracts.Where(c => c.CurrentAptId != null && c.Stay == "stay").Select(c => c.CurrentAptId!.Value).ToHashSet();
            var apt = AptsOf("M-STD-3").FirstOrDefault(a => a.Floor == 1 && !used.Contains(a.Id))
                      ?? throw new InvalidOperationException("No free floor-1 M-STD-3 apartment for E12");
            AddRestriction(p12, "required_floor", value: "1", desc: "Staff-confirmed: ground-floor placement required (fictional)");
            AddLocked(p12, apt, "Locked by manager: staff-confirmed ground-floor requirement (fictional). Solver must keep this.");
            Edge("E12", "manager_lock_required_floor", $"Staff-confirmed required_floor=1; manager pre-placed and locked in {apt.Number}.", new() { p12 },
                "Preserve the lock; any move must be refused with the restriction shown.", apt);

            var apt13 = AptsOf("W-OVR-2").First();
            MakeRenewal(q13, apt13, "stay");
            AddLocked(q13, apt13, "Locked by manager: renewal stay already promised to tenant (fictional).");
            Edge("E13", "manager_lock_renewal", $"Renewal stay in {apt13.Number}, already locked by a manager.", new() { q13 },
                "Preserve the lock.", apt13);
        });

        // E14 staff-confirmed separation that conflicts with a roommate request
        // Lives in M-OTH-4 (was M-FLAT-4), same reason as E05.
        var r14 = NewContract("M-OTH-4", edge: true); var s14 = NewContract("M-OTH-4", edge: true);
        AddRequest(s14, T(r14).Full);
        AddRestriction(r14, "must_not_share_with", other: T(s14).Id, desc: "Staff-confirmed: do not place with the named tenant (fictional)");
        AddRestriction(s14, "must_not_share_with", other: T(r14).Id, desc: "Staff-confirmed: do not place with the named tenant (fictional)");
        Edge("E14", "restriction_vs_request", "Second tenant requests the first, but staff confirmed they must not share an apartment.", new() { r14, s14 },
            "Restriction wins; request excluded with reason.");

        // E15 oversubscribed floor plan: top up demand so it exceeds its apartments' capacity.
        int cap = P(Oversubscribed).Capacity;
        int ov = Contracts.Count(c => c.PlanId == P(Oversubscribed).Id);
        var forced = new List<Contract>();
        while (ov < cap + 2) { forced.Add(NewContract(Oversubscribed, edge: false)); ov++; }

        int edgeCount = Contracts.Count;
        if (edgeCount > n) throw new InvalidOperationException($"size {n} is too small; need at least {edgeCount} tenants for the edge cases");

        // ---- filler tenants ----
        var weights = new (string Code, double W)[] {
            ("W-STD-2",.10),("W-OVR-2",.07),("W-STD-3",.14),("W-FLAT-4",.08),("W-STH-4",.07),("W-OTH-4",.05),
            ("M-STD-3",.20),("M-FLAT-4",.12),("M-STH-4",.10),("M-OTH-4",.07) };
        while (Contracts.Count < n)
        {
            double x = rng.NextDouble(), acc = 0; string code = weights[^1].Code;
            foreach (var w in weights) { acc += w.W; if (x < acc) { code = w.Code; break; } }
            NewContract(code, edge: false);
        }

        // ---- apartments ----
        var demand = Plans.ToDictionary(p => p.Id, p => Contracts.Count(c => c.PlanId == p.Id));
        var aptCount = new Dictionary<int, int>();
        foreach (var p in Plans)
        {
            int d = demand[p.Id];
            int need = (d + p.Capacity - 1) / p.Capacity;
            int count = p.Code == Oversubscribed
                ? Math.Max(1, d / p.Capacity - (d / p.Capacity >= 2 ? 1 : 0))
                : Math.Max(1, need + Math.Max(1, (int)Math.Ceiling(need * 0.10)));
            if (p.Code == Oversubscribed && count * p.Capacity >= d) throw new InvalidOperationException("oversubscription not achieved");
            aptCount[p.Id] = count;
        }
        int aid = 1;
        // Two buildings (women's, men's), 4 floors each. Apartment numbers are random unique 4-digit
        // numbers that do NOT encode the floor; floor is stored explicitly. Each plan's apartments are spread
        // over floors 1..4 in turn so every plan exists on several floors.
        var usedNumbers = new HashSet<int>();
        foreach (var p in Plans)
        {
            string building = p.Category == "women" ? "Women's" : "Men's";
            for (int k = 0; k < aptCount[p.Id]; k++)
            {
                int num; do num = rng.Range(1000, 9999); while (!usedNumbers.Add(num));
                Apts.Add(new Apartment { Id = aid++, PlanId = p.Id, Building = building, Floor = k % 4 + 1, Number = num.ToString(CultureInfo.InvariantCulture) });
            }
        }

        // E16: one apartment with a staff-confirmed capacity override (last W-STD-3 apartment).
        var ovApt = AptsOf("W-STD-3").Last();
        ovApt.CapOverride = 5; ovApt.OverrideReason = "Fictional: one sleeping area unavailable this term (assumption)";

        // ---- preferences for everyone ----
        bool baseRates = n > 100; // small set: no random animals/allergies, so every conflict there is deliberate
        foreach (var c in Contracts)
        {
            var pr = new Pref
            {
                Sleep = rng.Pick(new[] { "early", "typical", "typical", "late", "unknown" }),
                Animal = baseRates && !c.IsEdge && rng.Chance(0.03),
                Allergy = baseRates && !c.IsEdge && rng.Chance(0.04),
                Age = rng.Chance(0.75) ? rng.Range(18, 27) : null,
                Social = rng.Chance(0.6) ? rng.Pick(new[] { "outgoing", "balanced", "introverted" }) : null,
                Major = rng.Chance(0.6) ? rng.Pick(Majors) : null,
                Stage = rng.Chance(0.6) ? rng.Pick(new[] { "first_year", "second_year", "third_year", "fourth_year_plus", "graduate" }) : null,
            };
            if (pr.Animal && pr.Allergy) pr.Allergy = false;
            Prefs[c.Id] = pr;
        }

        foreach (var a in pend) a();

        Edge("E15", "floor_plan_oversubscribed",
            $"{Oversubscribed}: {demand[P(Oversubscribed).Id]} contracts but only {aptCount[P(Oversubscribed).Id]} apartment(s) x {cap} = {aptCount[P(Oversubscribed).Id] * cap} places.",
            Contracts.Where(c => c.PlanId == P(Oversubscribed).Id).ToList(), "Some tenants cannot be placed; report shortfall honestly, never exceed capacity or move them to another plan silently.");
        Edge("E16", "capacity_override", $"Apartment {ovApt.Number} has capacity_override=5 (W-STD-3 plan capacity 6).", new(),
            "Validation must use the override, not the plan capacity.", ovApt);

        // ---- random renewals among filler tenants ----
        var occupancy = Apts.ToDictionary(a => a.Id, a => Contracts.Count(c => c.CurrentAptId == a.Id));
        foreach (var c in Contracts.Where(c => !c.IsEdge && !c.Renewing))
        {
            if (!rng.Chance(0.35)) continue;
            var options = Apts.Where(a => a.PlanId == c.PlanId && a.CapOverride == null && occupancy[a.Id] < P2(a).Capacity).ToList();
            if (options.Count == 0) continue;
            var apt = rng.Pick(options);
            double r = rng.NextDouble();
            MakeRenewal(c, apt, r < 0.6 ? "stay" : r < 0.9 ? "move" : "no_preference");
            occupancy[apt.Id]++;
        }

        // ---- random roommate groups among filler tenants (always within one plan, never above capacity) ----
        foreach (var p in Plans)
        {
            var pool = Contracts.Where(c => !c.IsEdge && c.PlanId == p.Id).ToList();
            rng.Shuffle(pool);
            int idx = 0;
            while (idx < pool.Count)
            {
                if (!rng.Chance(0.45)) { idx++; continue; }
                int sz = Math.Min(rng.Range(2, p.Capacity), pool.Count - idx);
                if (sz < 2) break;
                var grp = pool.GetRange(idx, sz); idx += sz;
                foreach (var a in grp)
                    foreach (var b in grp)
                    {
                        if (a == b) continue;
                        if (n > 100 && rng.Chance(0.08)) { Note("random_dropped_reverse_request_non_mutual", a.Id); continue; }
                        string text = FormatName(T(b));
                        if (n > 100 && rng.Chance(0.02)) { text = Misspell(T(b)); Note("random_misspelled_request", a.Id); }
                        AddRequest(a, text);
                    }
            }
            if (n > 100 && pool.Count > 0)
                foreach (var c in pool.Where(_ => rng.Chance(0.005))) { AddRequest(c, $"{rng.Pick(Firsts)} Unknownperson"); Note("random_nonexistent_request", c.Id); }
        }
    }

    static FloorPlan P2(Apartment a) => Plans[a.PlanId - 1];
    static Tenant T(Contract c) => Tenants[c.TenantId - 1];
    static List<Apartment> AptsOf(string code) => Apts.Where(a => a.PlanId == P(code).Id).ToList();
    static void Note(string key, int contractId) { if (!Noise.TryGetValue(key, out var l)) Noise[key] = l = new(); l.Add(contractId); }

    static Contract NewContract(string planCode, bool edge, string? first = null)
    {
        string f, l; int guard = 0;
        do
        {
            f = first ?? rng.Pick(Firsts);
            l = rng.Pick(SurA) + rng.Pick(SurB);
            if (++guard > 10000) throw new InvalidOperationException("name space exhausted");
        } while (!UsedNames.Add($"{f} {l}") || (first == null && f == "Jordan"));
        var t = new Tenant { Id = Tenants.Count + 1, Code = $"FAKE-{Tenants.Count + 1:00000}", First = f, Last = l };
        Tenants.Add(t);
        var c = new Contract { Id = Contracts.Count + 1, TenantId = t.Id, PlanId = P(planCode).Id, IsEdge = edge };
        Contracts.Add(c);
        return c;
    }

    static void MakeRenewal(Contract c, Apartment apt, string stay) { c.Renewing = true; c.CurrentAptId = apt.Id; c.Stay = stay; }
    static void AddRequest(Contract c, string raw) => Requests.Add(new Request { Id = Requests.Count + 1, ContractId = c.Id, Raw = raw });
    static void AddRestriction(Contract c, string type, string desc, int? apt = null, int? other = null, string? value = null) =>
        Restrictions.Add(new Restriction { Id = Restrictions.Count + 1, ContractId = c.Id, Type = type, AptId = apt, OtherTenantId = other, Value = value, Description = desc });
    static void AddLocked(Contract c, Apartment a, string why) =>
        Assignments.Add(new Assignment { Id = Assignments.Count + 1, DraftId = 1, ContractId = c.Id, AptId = a.Id, Locked = true, Source = "manual", Explanation = why });

    static void Edge(string id, string kind, string desc, List<Contract> cs, string expected, Apartment? apt = null)
    {
        var e = new EdgeCase { Id = id, Kind = kind, Description = desc, ExpectedHandling = expected, TenantCodes = cs.Select(c => T(c).Code).ToList() };
        if (apt != null) e.Apartments.Add(apt.Number);
        Edges.Add(e);
    }

    static string FormatName(Tenant t)
    {
        double r = rng.NextDouble();
        return r < 0.75 ? t.Full : r < 0.9 ? t.Full.ToLowerInvariant() : $"{t.Last}, {t.First}";
    }

    static string Misspell(Tenant t)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            char[] s = t.Last.ToCharArray();
            int i = rng.Range(1, s.Length - 2);
            string last = rng.Next(3) switch
            {
                0 => new string(s.Take(i).Concat(new[] { s[i + 1], s[i] }).Concat(s.Skip(i + 2)).ToArray()), // swap
                1 => t.Last.Remove(i, 1),                                                                    // drop
                _ => t.Last.Insert(i, s[i].ToString()),                                                      // double
            };
            string full = $"{t.First} {last}";
            if (last != t.Last && !UsedNames.Contains(full)) return full;
        }
        return $"{t.First} {t.Last}e";
    }

    // ------------------------------------------------------------------ output
    static string S(string? v) => v == null ? "NULL" : "'" + v.Replace("\\", "\\\\").Replace("'", "''") + "'";
    static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
    static string B(bool v) => v ? "TRUE" : "FALSE";

    static void Insert(StringBuilder sb, string table, string cols, IEnumerable<string> rows)
    {
        var list = rows.ToList();
        for (int i = 0; i < list.Count; i += 250)
        {
            sb.Append($"INSERT INTO {table} ({cols}) VALUES\n  ");
            sb.Append(string.Join(",\n  ", list.Skip(i).Take(250)));
            sb.Append(";\n");
        }
        sb.Append('\n');
    }

    static string BuildSql(int n, ulong seed, string name)
    {
        var sb = new StringBuilder();
        sb.Append($"-- Lodge MVP seed '{name}': {n} tenants, seed {seed}. GENERATED by tools/DataGen - do not edit by hand.\n");
        sb.Append("-- ALL DATA IS FICTIONAL. Load into a database that already has db/schema.sql applied (tables empty).\n");
        sb.Append("SET NAMES utf8mb4;\nSTART TRANSACTION;\n\n");
        Insert(sb, "terms", "term_id, code, name, start_date, end_date", new[] {
            "(1, 'F2026', 'Fall 2026 (fictional dates)', '2026-09-14', '2026-12-18')",
            "(2, 'W2027', 'Winter 2027 (fictional dates)', '2027-01-06', '2027-04-14')" });
        Insert(sb, "floor_plans", "floor_plan_id, code, name, variant, bedrooms, bathrooms, housing_category, resident_capacity, capacity_is_assumption, notes",
            Plans.Select(p => $"({p.Id}, {S(p.Code)}, {S(p.Name)}, {S(p.Variant)}, {p.Bedrooms}, {p.Bathrooms.ToString("0.0", CultureInfo.InvariantCulture)}, {S(p.Category)}, {p.Capacity}, {B(p.CapacityIsAssumption)}, {S(p.Notes)})"));
        Insert(sb, "apartments", "apartment_id, apartment_number, floor_plan_id, building, floor, capacity_override, override_reason",
            Apts.Select(a => $"({a.Id}, {S(a.Number)}, {a.PlanId}, {S(a.Building)}, {a.Floor}, {I(a.CapOverride)}, {S(a.OverrideReason)})"));
        Insert(sb, "tenants", "tenant_id, tenant_code, first_name, last_name",
            Tenants.Select(t => $"({t.Id}, {S(t.Code)}, {S(t.First)}, {S(t.Last)})"));
        Insert(sb, "contracts", "contract_id, tenant_id, term_id, floor_plan_id, tenant_type, current_apartment_id, stay_preference",
            Contracts.Select(c => $"({c.Id}, {c.TenantId}, {TargetTermId}, {c.PlanId}, {S(c.Renewing ? "renewing" : "new")}, {I(c.CurrentAptId)}, {S(c.Stay)})"));
        Insert(sb, "preferences", "contract_id, sleep_schedule, has_animal, animal_allergy_restriction, age, social_preference, major, academic_stage",
            Contracts.Select(c => { var p = Prefs[c.Id]; return $"({c.Id}, {S(p.Sleep)}, {B(p.Animal)}, {B(p.Allergy)}, {I(p.Age)}, {S(p.Social)}, {S(p.Major)}, {S(p.Stage)})"; }));
        if (Requests.Count > 0)
            Insert(sb, "roommate_requests", "request_id, contract_id, raw_text, resolved_tenant_id, status, exclusion_reason",
                Requests.Select(r => $"({r.Id}, {r.ContractId}, {S(r.Raw)}, NULL, 'pending', NULL)"));
        Insert(sb, "placement_restrictions", "restriction_id, contract_id, restriction_type, apartment_id, other_tenant_id, value, description, confirmed_by, confirmed_at",
            Restrictions.Select(r => $"({r.Id}, {r.ContractId}, {S(r.Type)}, {I(r.AptId)}, {I(r.OtherTenantId)}, {S(r.Value)}, {S(r.Description)}, 'Fictional Staff Member', '2026-11-15 10:00:00')"));
        Insert(sb, "assignment_drafts", "draft_id, term_id, name, source, status, priority_config, notes", new[] {
            $"(1, {TargetTermId}, 'Manager pre-placements (fictional)', 'manual', 'draft', NULL, 'Locked placements a manager made before generating a draft')" });
        Insert(sb, "assignments", "assignment_id, draft_id, term_id, contract_id, apartment_id, is_locked, locked_by, locked_at, source, explanation",
            Assignments.Select(a => $"({a.Id}, {a.DraftId}, {TargetTermId}, {a.ContractId}, {a.AptId}, {B(a.Locked)}, 'Fictional Manager', '2026-11-20 09:00:00', {S(a.Source)}, {S(a.Explanation)})"));
        sb.Append("COMMIT;\n");
        return sb.ToString();
    }

    static string BuildAnswerKey(int n, ulong seed)
    {
        var key = new
        {
            note = "FICTIONAL test data. Deliberate edge cases and the handling we expect. Use in tests; never show as real data.",
            seed, tenants = n,
            edge_cases = Edges.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => new { id = e.Id, kind = e.Kind, description = e.Description, tenant_codes = e.TenantCodes, apartments = e.Apartments, expected_handling = e.ExpectedHandling }),
            random_noise = Noise.OrderBy(k => k.Key, StringComparer.Ordinal)
                .ToDictionary(k => k.Key, k => k.Value.Select(cid => Tenants[Contracts[cid - 1].TenantId - 1].Code).ToList()),
        };
        return JsonSerializer.Serialize(key, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}

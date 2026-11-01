using Lodge.Core.Services;

namespace Lodge.Tests;

public class RoommateNameMatcherTests
{
    private static readonly RoommateNameMatcher.TenantName Harper = new(
        8, "FAKE-00008", "Harper", "Pebblebottom", 8, "W-STD-3");
    private static readonly RoommateNameMatcher.TenantName Quinn = new(
        9, "FAKE-00009", "Quinn", "Jellyberry", 9, "W-STD-3");
    private static readonly RoommateNameMatcher.TenantName JordanW = new(
        14, "FAKE-00014", "Jordan", "Wobblewick", 14, "M-FLAT-4");
    private static readonly RoommateNameMatcher.TenantName JordanM = new(
        15, "FAKE-00015", "Jordan", "Moontooth", 15, "M-FLAT-4");
    private static readonly RoommateNameMatcher.TenantName Piper = new(
        13, "FAKE-00013", "Piper", "Kazoonugget", 13, "M-FLAT-4");

    private static List<RoommateNameMatcher.TenantName> Pool() =>
        [Harper, Quinn, JordanW, JordanM, Piper];

    [Fact]
    public void Exact_FullName_IsSingleExactCandidate()
    {
        var c = RoommateNameMatcher.FindCandidates("Harper Pebblebottom", Pool(), excludeTenantId: Quinn.TenantId);
        Assert.Single(c);
        Assert.Equal(RoommateNameMatcher.KindExact, c[0].MatchKind);
        Assert.Equal(Harper.TenantId, c[0].Tenant.TenantId);

        var (kind, _) = RoommateNameMatcher.Classify(c);
        Assert.Equal(RoommateNameMatcher.ConflictExact, kind);
    }

    [Fact]
    public void Exact_LastCommaFirst_Matches()
    {
        var c = RoommateNameMatcher.FindCandidates("Pebblebottom, Harper", Pool());
        Assert.Contains(c, x => x.MatchKind == RoommateNameMatcher.KindExact && x.Tenant.TenantId == Harper.TenantId);
    }

    [Fact]
    public void Missing_Name_HasNoCandidates_DoesNotInventIdentity()
    {
        var c = RoommateNameMatcher.FindCandidates("Rowan Glimmerpatch", Pool());
        Assert.Empty(c);
        var (kind, _) = RoommateNameMatcher.Classify(c);
        Assert.Equal(RoommateNameMatcher.ConflictMissing, kind);
    }

    [Fact]
    public void Misspelling_IsNearOnly_NeverAutoExact()
    {
        var c = RoommateNameMatcher.FindCandidates("Quinn Jelyberry", Pool(), excludeTenantId: Harper.TenantId);
        Assert.DoesNotContain(c, x => x.MatchKind == RoommateNameMatcher.KindExact);
        Assert.Contains(c, x => x.MatchKind == RoommateNameMatcher.KindNear && x.Tenant.TenantId == Quinn.TenantId);
        var (kind, _) = RoommateNameMatcher.Classify(c);
        Assert.Equal(RoommateNameMatcher.ConflictNearOnly, kind);
    }

    [Fact]
    public void Duplicate_FirstName_ListsBothJordans()
    {
        var c = RoommateNameMatcher.FindCandidates("Jordan", Pool(), excludeTenantId: Piper.TenantId);
        Assert.Equal(2, c.Count(x => x.MatchKind == RoommateNameMatcher.KindFirstName));
        Assert.Contains(c, x => x.Tenant.TenantId == JordanW.TenantId);
        Assert.Contains(c, x => x.Tenant.TenantId == JordanM.TenantId);
        var (kind, _) = RoommateNameMatcher.Classify(c);
        Assert.Equal(RoommateNameMatcher.ConflictDuplicate, kind);
    }

    [Fact]
    public void ExcludesRequesterFromCandidates()
    {
        var c = RoommateNameMatcher.FindCandidates("Harper Pebblebottom", Pool(), excludeTenantId: Harper.TenantId);
        Assert.Empty(c.Where(x => x.Tenant.TenantId == Harper.TenantId));
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        Assert.Equal("Quinn Jellyberry", RoommateNameMatcher.Normalize("  Quinn   Jellyberry "));
    }
}

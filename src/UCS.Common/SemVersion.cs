namespace UCS.Common;

/// <summary>
/// Minimal Semantic Versioning 2.0 (major.minor.patch[-prerelease]) with correct precedence:
/// 1.0.0-alpha &lt; 1.0.0-alpha.1 &lt; 1.0.0-beta &lt; 1.0.0-rc.1 &lt; 1.0.0.
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string[] PreRelease { get; }
    public bool IsPreRelease => PreRelease.Length > 0;

    private SemVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = pre;
    }

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var nums = core.Split('.');
        if (nums.Length is < 1 or > 4) return false;
        var parsed = new int[3];
        for (var i = 0; i < Math.Min(3, nums.Length); i++)
            if (!int.TryParse(nums[i], out parsed[i]) || parsed[i] < 0) return false;
        version = new SemVersion(parsed[0], parsed[1], parsed[2], pre);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid version '{text}'");

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release is newer than any of its pre-releases
        if (!IsPreRelease && other.IsPreRelease) return 1;
        if (IsPreRelease && !other.IsPreRelease) return -1;
        for (var i = 0; i < Math.Min(PreRelease.Length, other.PreRelease.Length); i++)
        {
            var a = PreRelease[i];
            var b = other.PreRelease[i];
            var aNum = int.TryParse(a, out var an);
            var bNum = int.TryParse(b, out var bn);
            if (aNum && bNum) c = an.CompareTo(bn);
            else if (aNum) c = -1;          // numeric identifiers have lower precedence
            else if (bNum) c = 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return PreRelease.Length.CompareTo(other.PreRelease.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + string.Join('.', PreRelease) : "");
}

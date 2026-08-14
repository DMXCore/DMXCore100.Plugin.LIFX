namespace DMXCore100.LIFX;

public static class LifxTargets
{
    public static IReadOnlyList<LifxLight> Resolve(IReadOnlyList<LifxLight> lights, string? target)
    {
        if (target is null)
        {
            return [];
        }

        string needle = target.Trim();
        if (string.Equals(needle, "all", StringComparison.OrdinalIgnoreCase) || needle == "*")
        {
            return lights;
        }

        if (needle.Length == 0)
        {
            return [];
        }

        LifxLight[] exact = [.. lights.Where(light =>
            string.Equals(light.Id, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(light.Ip, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(light.Label, needle, StringComparison.OrdinalIgnoreCase))];
        if (exact.Length > 0)
        {
            return exact;
        }

        return lights.Where(light => light.Label.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}

namespace DMXCore100.LIFX;

public static class LifxTargets
{
    public static IReadOnlyList<LifxLight> Resolve(IReadOnlyList<LifxLight> lights, string target)
    {
        if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase) || target == "*")
        {
            return lights;
        }

        string needle = target.Trim();
        return lights.Where(light =>
                string.Equals(light.Id, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(light.Ip, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(light.Label, needle, StringComparison.OrdinalIgnoreCase)
                || light.Label.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}

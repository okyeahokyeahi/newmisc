namespace DesktopBuddy;

internal static class Format
{
    public static string Bytes(long bytes) => Bytes((double)bytes);
    public static string Bytes(ulong bytes) => Bytes((double)bytes);

    private static string Bytes(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024d * 1024 => $"{bytes / (1024d * 1024):0} MB",
        _ => $"{bytes / 1024d:0} KB",
    };

    public static string Temp(double? celsius) => celsius.HasValue ? $"{celsius.Value:0}°C" : "n/a";
}

internal static class SmokeAssert
{
    public static void That(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("  ok: " + description);
    }
}

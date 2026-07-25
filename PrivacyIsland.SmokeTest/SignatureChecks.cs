using PrivacyIsland.Native;

internal static class SignatureChecks
{
    public static void Run(IEnumerable<string> paths)
    {
        foreach (string path in paths)
            SmokeAssert.That(SeewoSignatureVerifier.IsSignedBySeewo(path), $"希沃数字签名有效: {path}");
        SmokeAssert.That(!SeewoSignatureVerifier.IsSignedBySeewo(typeof(Program).Assembly.Location),
            "未签名的冒烟测试程序集被拒绝");
        Console.WriteLine("SIGNATURE PASS");
    }
}

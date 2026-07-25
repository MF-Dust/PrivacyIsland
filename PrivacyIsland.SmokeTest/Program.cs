// 默认：IPC + 纯逻辑回归检查；privacy：仅纯逻辑；live：真注入；signature：数字签名。
if (args.Length > 0 && args[0] == "live")
{
    LiveChecks.Run();
    return;
}

if (args.Length > 0 && args[0] == "privacy")
{
    PrivacyChecks.Run();
    Console.WriteLine("PRIVACY PASS");
    return;
}

if (args.Length > 1 && args[0] == "signature")
{
    SignatureChecks.Run(args.Skip(1));
    return;
}

IpcChecks.Run();
PrivacyChecks.Run();
Console.WriteLine("PASS");

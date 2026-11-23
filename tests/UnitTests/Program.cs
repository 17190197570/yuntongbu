using CloudDrive.Core;

int passed = 0;
void Accept(string[] arguments, Action<ProviderCommand>? check = null)
{
    check?.Invoke(ProviderCommand.Parse(arguments));
    if (check is null) ProviderCommand.Parse(arguments);
    passed++;
}
void Reject(params string[] arguments)
{
    try { ProviderCommand.Parse(arguments); }
    catch (ArgumentException) { passed++; return; }
    throw new Exception("Expected rejection: " + string.Join(' ', arguments));
}
Accept([]);
Accept(["doctor"]);
Accept(["run", "--source", @"C:\模拟远端", "--root", @"C:\同步目录"], command =>
{
    if (command.Source != @"C:\模拟远端" || command.Root != @"C:\同步目录")
        throw new Exception("Unicode paths were changed.");
    if (!command.NativeArguments().SequenceEqual(new[] { "run", "--source", @"C:\模拟远端", "--root", @"C:\同步目录" }))
        throw new Exception("Native arguments lost their boundaries.");
});
Accept(["run", "--source", @"C:\source", "--root", @"C:\source-sibling"]);
Accept(["status", "--root", @"C:\cloud", "--path", @"中文目录\report.txt"]);
Accept(["status", "--root", @"C:\cloud", "--path", "中文目录/report.txt"], command =>
{
    if (command.RelativePath != @"中文目录\report.txt") throw new Exception("Path separators were not normalized.");
});
Accept(["unregister", "--root", @"C:\cloud"]);
Reject("copy");
Reject("run", "--source", @"C:\source", "--root", @"C:\source\child");
Reject("run", "--source", @"C:\cloud\child", "--root", @"C:\cloud");
Reject("run", "--source", @"C:\SOURCE", "--root", @"c:\source\");
Reject("run", "--source", @"C:\", "--root", @"C:\cloud");
Reject("run", "--source", @"\\server\share", "--root", @"C:\cloud");
Reject("run", "--source", @"C:\source", "--root", "relative");
Reject("run", "--source", @"C:\source", "--root", @"C:cloud");
Reject("run", "--source", @"C:\source", "--root", @"C:\cloud:stream");
Reject("status", "--root", @"C:\cloud", "--path", @"..\outside.txt");
Reject("status", "--root", @"C:\cloud", "--path", @"sub\..\outside.txt");
Reject("status", "--root", @"C:\cloud", "--path", @"C:\outside.txt");
Reject("release", "--root", @"C:\cloud", "--path", "file.txt:stream");
Reject("release", "--root", @"C:\cloud", "--path", @"sub\\file.txt");
Reject("release", "--root", @"C:\cloud", "--path", @".\file.txt");
Reject("release", "--root", @"C:\cloud", "--path", "file.txt ");
Reject("release", "--root", @"C:\cloud", "--path", "ambiguous.\\file.txt");
Reject("release", "--root", @"C:\cloud", "--path", "file\n.txt");
Reject("release", "--root", @"C:\cloud");
Reject("run", "--source", @"C:\source", "--root", @"C:\cloud", "--root", @"C:\other");
Reject("run", "--source", @"C:\source", "--root");
Reject("run", "--source", @"C:\source", "--root", @"C:\cloud", "--path", "file");
Reject("unregister", "--root", @"C:\cloud", "--source", @"C:\source");
Console.WriteLine($"PASS {passed} command and path safety checks.");

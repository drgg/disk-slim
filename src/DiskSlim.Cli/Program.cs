using System.Text;
using DiskSlim.Cli;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // 没有控制台（比如被其他程序调用）时忽略。
}

try
{
    return CliApp.Run(args, Console.Out, Console.Error, Console.In);
}
catch (Exception ex)
{
    // 设置环境变量 DISKSLIM_DEBUG=1 可以看到完整的异常堆栈。
    Console.Error.WriteLine(Environment.GetEnvironmentVariable("DISKSLIM_DEBUG") is null ? $"出错了：{ex.Message}" : ex.ToString());
    return CliApp.ExitError;
}

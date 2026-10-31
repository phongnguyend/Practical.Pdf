using PdfObjectKit.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
return await CliApplication.Run(args, Console.Out, Console.Error, cancellation.Token);

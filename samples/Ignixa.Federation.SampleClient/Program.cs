using Ignixa.Federation.SampleClient;

// Only the variables the CLI documents are passed on; nothing else from the environment is read.
Dictionary<string, string?> environment = [];
foreach (var name in new[] { CliOptions.BaseUrlVariable, CliOptions.FederationTokenVariable, CliOptions.ShardTokenVariable })
{
    environment[name] = Environment.GetEnvironmentVariable(name);
}

return await CliApp.RunAsync(args, environment, Console.Out, Console.Error);

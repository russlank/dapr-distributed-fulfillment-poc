using Fulfillment.ClientSimulator;

// External client simulator: ordinary HTTP to the public order API, no Dapr (see Cli for commands).
return await Cli.Build().Parse(args).InvokeAsync();

using System.CommandLine;
using Kusto.Cli;

var rootCommand = CommandFactory.CreateRootCommand();
var configuration = new ParserConfiguration();
return await rootCommand.Parse(args, configuration).InvokeAsync();

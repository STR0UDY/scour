using System.CommandLine;
using System.Text;
using Scour.Cli;

Console.OutputEncoding = Encoding.UTF8;

var rulesDir = new Option<string?>("--rules-dir")
{
    Description = "Load rules from this folder instead of the built-in set.",
    Recursive = true,
};

var root = new RootCommand("Scour: a Windows cleaner that never deletes anything you haven't seen first.")
{
    rulesDir,
    ScanCommand.Create(rulesDir),
    CleanCommand.Create(rulesDir),
    RulesCommand.Create(rulesDir),
};

return await root.Parse(args).InvokeAsync();

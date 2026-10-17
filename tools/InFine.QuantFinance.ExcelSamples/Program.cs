using System;
using System.IO;
using InFine.QuantFinance.ExcelSamples;

// Usage: dotnet run --project tools/InFine.QuantFinance.ExcelSamples [output directory]
// Default output: samples/excel at the root of the repository (found by walking up to QuantFinance.sln).
string output;
if (args.Length > 0) output = args[0];
else
{
    // From the working directory (dotnet run from anywhere in the repository), else from the executable.
    DirectoryInfo? dir = null;
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        dir = new DirectoryInfo(start);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QuantFinance.sln"))) dir = dir.Parent;
        if (dir != null) break;
    }
    if (dir == null)
    {
        Console.Error.WriteLine("QuantFinance.sln not found above the working directory or the executable: pass the output directory.");
        return 1;
    }
    output = Path.Combine(dir.FullName, "samples", "excel");
}

foreach (var path in SampleWorkbooks.WriteAll(output)) Console.WriteLine(path);
return 0;

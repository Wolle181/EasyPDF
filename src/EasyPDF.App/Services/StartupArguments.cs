using System;
using System.Collections.Generic;

namespace EasyPDF.App.Services;

/// <summary>
/// Command line: <c>&lt;app&gt; [--merge | --read] [file.pdf ...]</c>.
/// Without an option one PDF is opened and several go to the Concatenate dialog; with <c>--merge</c> (context menu
/// "Merge with &lt;app&gt;") the files always go to the Concatenate dialog, also a single one; with <c>--read</c>
/// (context menu "Read with &lt;app&gt;") each file opens in a window of its own. <c>--merge</c> wins over <c>--read</c>.
/// </summary>
public sealed record StartupArguments(IReadOnlyList<string> Files, bool Merge, bool Read = false)
{
    public const string MergeOption = "--merge";
    public const string ReadOption = "--read";

    public static StartupArguments Parse(IEnumerable<string>? args)
    {
        var files = new List<string>();
        var merge = false;
        var read = false;
        foreach (var arg in args ?? [])
        {
            if (string.Equals(arg, MergeOption, StringComparison.OrdinalIgnoreCase))
                merge = true;
            else if (string.Equals(arg, ReadOption, StringComparison.OrdinalIgnoreCase))
                read = true;
            else if (!string.IsNullOrWhiteSpace(arg))
                files.Add(arg);
        }
        return new StartupArguments(files, merge, read);
    }
}

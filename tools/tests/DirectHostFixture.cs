using System;
using System.Threading;

// A process-level fixture: no Wi-Fi APIs and no radio/network changes.
internal static class DirectHostFixture
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--direct-host") return 2;
        string mode = Environment.GetEnvironmentVariable("ANYDISPLAY_DIRECT_FIXTURE");
        if (mode == "busy") { Console.WriteLine("AD_DIRECT Aborted ResourceInUse"); return 1; }
        Console.WriteLine("AD_DIRECT Started Success");
        if (mode == "unresponsive") { Thread.Sleep(30000); return 3; }
        while (true) { string line = Console.ReadLine(); if (line == null || line == "stop") break; }
        Console.WriteLine("AD_DIRECT Stopped Success");
        return 0;
    }
}

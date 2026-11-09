using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

// Exercises the shipping controller against real child processes, without starting
// Wi-Fi Direct or interfering with a running user stream.
internal static class DirectLifecycleTest
{
    private static Type controller;
    private static object Get(object instance, string name) { return controller.GetProperty(name).GetValue(instance, null); }
    private static void Call(object instance, string name) { controller.GetMethod(name).Invoke(instance, null); }
    private static void Wait(Func<bool> condition, string description, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > milliseconds) throw new Exception(description); Thread.Sleep(25); }
    }
    private static void Run(string fixture, string mode)
    {
        Environment.SetEnvironmentVariable("ANYDISPLAY_DIRECT_FIXTURE", mode);
        var owned = Activator.CreateInstance(controller, new object[] { fixture });
        try {
            Call(owned, "Start");
            var process = (Process)controller.GetField("process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owned);
            if (mode == "busy") {
                Wait(() => (string)Get(owned, "Error") == "ResourceInUse", "Native conflict was lost", 4000);
                Wait(() => (bool)Get(owned, "HasExited"), "Failed helper remained alive", 4000);
                if (Get(owned, "Status").ToString() != "Aborted") throw new Exception("Conflict status was lost on exit");
            } else {
                Wait(() => Get(owned, "Status").ToString() == "Started", "Start status did not reach launcher", 4000);
                if (mode == "eof") process.StandardInput.Close();
                else { Call(owned, "Stop"); Call(owned, "Stop"); }
                Wait(() => (bool)Get(owned, "HasExited"), "Owned helper leaked after " + mode, 6000);
                if (Get(owned, "Status").ToString() != "Stopped") throw new Exception("Exited helper still advertised as active");
            }
            Console.WriteLine("PASS direct helper " + mode);
        } finally { Call(owned, "Dispose"); Call(owned, "Dispose"); }
    }
    private static int Main(string[] args)
    {
        try {
            controller = Assembly.LoadFrom(args[0]).GetType("DirectAccessPoint", true);
            for (int i = 0; i < 3; ++i) Run(args[1], "normal");
            Run(args[1], "eof");
            Run(args[1], "busy");
            Run(args[1], "unresponsive");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Environment.SetEnvironmentVariable("ANYDISPLAY_DIRECT_FIXTURE", null); }
    }
}

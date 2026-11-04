using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Rig
{
    // Minimal reflection runner: [SetUp] -> test -> [TearDown], then LogAssert evaluation.
    public static class Runner
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static string Describe(Exception e)
        {
            while (e is TargetInvocationException t && t.InnerException != null) e = t.InnerException;
            return e is AssertionException ? e.Message : $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
        }

        // Unity coroutines can yield another IEnumerator; drive nested waits as well. The rig has no
        // frame loop, so tests that need real asynchrony use short sleeps before yielding.
        private static void Drive(IEnumerator enumerator)
        {
            while (enumerator.MoveNext())
                if (enumerator.Current is IEnumerator nested) Drive(nested);
        }

        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            int pass = 0, fail = 0;
            var failures = new List<string>();

            foreach (var type in typeof(Runner).Assembly.GetTypes().OrderBy(t => t.FullName))
            {
                var tests = type.GetMethods(All)
                    .Where(m => m.IsDefined(typeof(TestAttribute), false) || m.IsDefined(typeof(UnityTestAttribute), false))
                    .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
                if (tests.Count == 0) continue;
                var setUps = type.GetMethods(All).Where(m => m.IsDefined(typeof(SetUpAttribute), false)).ToList();
                var tearDowns = type.GetMethods(All).Where(m => m.IsDefined(typeof(TearDownAttribute), false)).ToList();

                foreach (var test in tests)
                {
                    string name = type.Name + "." + test.Name;
                    if (filter != null && !name.Contains(filter)) continue;

                    LogSink.Clear();
                    LogAssert.Reset();
                    var instance = Activator.CreateInstance(type, true);
                    string error = null;
                    try
                    {
                        foreach (var m in setUps) m.Invoke(instance, null);
                        var result = test.Invoke(instance, null);
                        if (result is IEnumerator e) Drive(e);   // [UnityTest]: nested coroutines run to completion
                    }
                    catch (Exception ex) { error = Describe(ex); }
                    finally
                    {
                        try { foreach (var m in tearDowns) m.Invoke(instance, null); }
                        catch (Exception ex) { error = error ?? "TearDown: " + Describe(ex); }
                    }
                    error = error ?? LogAssert.Evaluate();

                    if (error == null) pass++;
                    else { fail++; failures.Add($"  FAIL {name}\n       {error}"); }
                }
            }

            Console.WriteLine($"{pass} passed, {fail} failed");
            foreach (var f in failures) Console.WriteLine(f);
            return fail == 0 ? 0 : 1;
        }
    }
}

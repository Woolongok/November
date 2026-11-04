using System;
using System.Linq;

// ── Rig-only: just enough NUnit 3 surface for the package's tests. ──
namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class TestAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class SetUpAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class TearDownAttribute : Attribute { }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class CollectionAssert
    {
        public static void AreEqual(System.Collections.IEnumerable expected, System.Collections.IEnumerable actual)
        {
            var e = expected.Cast<object>().ToList();
            var a = actual.Cast<object>().ToList();
            if (e.Count != a.Count)
                throw new AssertionException($"Expected collection of {e.Count} but was {a.Count}: [{string.Join(", ", a)}]");
            for (int i = 0; i < e.Count; i++)
                if (!Equals(e[i], a[i]))
                    throw new AssertionException($"Collections differ at index {i}: expected {e[i]} but was {a[i]}");
        }
    }

    public static class Assert
    {
        private static string Msg(string message) => string.IsNullOrEmpty(message) ? "" : "  " + message;

        public static void Fail(string message = null) => throw new AssertionException("Fail." + Msg(message));
        public static void Pass(string message = null) { }

        public static void IsTrue(bool condition, string message = null)
        { if (!condition) throw new AssertionException("Expected: True But was: False." + Msg(message)); }

        public static void IsFalse(bool condition, string message = null)
        { if (condition) throw new AssertionException("Expected: False But was: True." + Msg(message)); }

        public static void IsNull(object o, string message = null)
        { if (o != null) throw new AssertionException($"Expected: null But was: {o}." + Msg(message)); }

        public static void IsNotNull(object o, string message = null)
        { if (o == null) throw new AssertionException("Expected: not null But was: null." + Msg(message)); }

        public static void AreSame(object expected, object actual, string message = null)
        { if (!ReferenceEquals(expected, actual)) throw new AssertionException("Expected: same instance But was: different instance." + Msg(message)); }

        public static void AreNotSame(object expected, object actual, string message = null)
        { if (ReferenceEquals(expected, actual)) throw new AssertionException("Expected: different instances But was: same instance." + Msg(message)); }

        public static void AreEqual(object expected, object actual, string message = null)
        { if (!Equals(expected, actual)) throw new AssertionException($"Expected: {expected} But was: {actual}." + Msg(message)); }

        public static void AreNotEqual(object expected, object actual, string message = null)
        { if (Equals(expected, actual)) throw new AssertionException($"Expected: not {expected} But was: {actual}." + Msg(message)); }

        public static void IsInstanceOf<T>(object o, string message = null)
        { if (!(o is T)) throw new AssertionException($"Expected: instance of {typeof(T).Name} But was: {o?.GetType().Name ?? "null"}." + Msg(message)); }

        // NUnit's Throws<T> requires the EXACT exception type; Catch<T> accepts derived types.
        public static T Throws<T>(Action code, string message = null) where T : Exception
        {
            try { code(); }
            catch (Exception e)
            {
                if (e.GetType() == typeof(T)) return (T)e;
                throw new AssertionException($"Expected: {typeof(T).Name} But was: {e.GetType().Name} ({e.Message.Split('\n')[0]})." + Msg(message));
            }
            throw new AssertionException($"Expected: {typeof(T).Name} But was: no exception." + Msg(message));
        }

        public static T Catch<T>(Action code, string message = null) where T : Exception
        {
            try { code(); }
            catch (Exception e)
            {
                if (e is T t) return t;
                throw new AssertionException($"Expected: {typeof(T).Name} or derived But was: {e.GetType().Name}." + Msg(message));
            }
            throw new AssertionException($"Expected: {typeof(T).Name} But was: no exception." + Msg(message));
        }

        public static void DoesNotThrow(Action code, string message = null)
        {
            try { code(); }
            catch (Exception e) { throw new AssertionException($"Expected: no exception But was: {e.GetType().Name}: {e.Message.Split('\n')[0]}." + Msg(message)); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

// ── Rig-only stand-in for the slice of Unity that November.DI and its tests touch. ──
// Models: destroyed-object == null, an object hierarchy, Awake timing (Awake runs when an
// object becomes active in the hierarchy), Instantiate cloning, and Debug -> LogAssert routing.

namespace Rig
{
    public static class LogSink
    {
        public static readonly List<(UnityEngine.LogType type, string message)> Entries = new List<(UnityEngine.LogType, string)>();
        public static void Add(UnityEngine.LogType t, object m) => Entries.Add((t, m?.ToString() ?? ""));
        public static void Clear() => Entries.Clear();
        public static int Count(UnityEngine.LogType t) => Entries.Count(e => e.type == t);
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class InitializeOnLoadMethodAttribute : Attribute { }
}

namespace UnityEngine.Scripting
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class PreserveAttribute : Attribute { }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public static class SceneManager
    {
        public static Scene GetActiveScene() => default;
        public static void MoveGameObjectToScene(GameObject go, Scene scene) { }
    }
}

namespace UnityEngine.TestTools
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnityTestAttribute : Attribute { }

    public static class LogAssert
    {
        internal static readonly List<(LogType type, Regex rx)> Expected = new List<(LogType, Regex)>();
        public static bool ignoreFailingMessages;
        public static void Expect(LogType type, Regex message) => Expected.Add((type, message));
        public static void Expect(LogType type, string message) => Expected.Add((type, new Regex("^" + Regex.Escape(message) + "$")));
        internal static void Reset() { Expected.Clear(); ignoreFailingMessages = false; }

        // Fails when any log (of any type) was received that no Expect consumed.
        public static void NoUnexpectedReceived()
        {
            var remaining = Rig.LogSink.Entries.ToList();
            foreach (var (type, rx) in Expected)
            {
                int i = remaining.FindIndex(e => e.type == type && rx.IsMatch(e.message));
                if (i >= 0) remaining.RemoveAt(i);
            }
            if (remaining.Count > 0)
                throw new NUnit.Framework.AssertionException($"Unexpected log received: [{remaining[0].type}] {remaining[0].message.Split('\n')[0]}");
        }

        // Mirrors Unity: every expected log must have occurred; any other Error/Assert/Exception log fails the test.
        internal static string Evaluate()
        {
            var remaining = Rig.LogSink.Entries.ToList();
            foreach (var (type, rx) in Expected)
            {
                int i = remaining.FindIndex(e => e.type == type && rx.IsMatch(e.message));
                if (i < 0) return $"Expected log did not appear: [{type}] {rx}";
                remaining.RemoveAt(i);
            }
            if (ignoreFailingMessages) return null;
            var bad = remaining.FirstOrDefault(e => e.type == LogType.Error || e.type == LogType.Assert || e.type == LogType.Exception);
            return bad.message == null ? null : $"Unhandled log message: [{bad.type}] {bad.message.Split('\n')[0]}";
        }
    }
}

namespace UnityEngine
{
    public enum LogType { Error, Assert, Warning, Log, Exception }

    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad, BeforeSceneLoad, AfterAssembliesLoaded, BeforeSplashScreen, SubsystemRegistration
    }

    [Flags]
    public enum HideFlags { None = 0, HideInHierarchy = 1, HideInInspector = 2, DontSaveInEditor = 4, NotEditable = 8, DontSaveInBuild = 16, DontUnloadUnusedAsset = 32, DontSave = 52, HideAndDontSave = 61 }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType) { }
    }

    public static class Application
    {
        public static bool isPlaying = true;

        public delegate void LogCallback(string condition, string stackTrace, LogType type);
        public static event LogCallback logMessageReceived;

        internal static void RaiseLog(LogType type, object message) => logMessageReceived?.Invoke(message?.ToString() ?? "", "", type);
    }

    public static class Debug
    {
        private static void Emit(LogType type, object message)
        {
            Rig.LogSink.Add(type, message);
            Application.RaiseLog(type, message);
        }

        public static void Log(object m) => Emit(LogType.Log, m);
        public static void LogWarning(object m) => Emit(LogType.Warning, m);
        public static void LogWarning(object m, Object ctx) => Emit(LogType.Warning, m);
        public static void LogError(object m) => Emit(LogType.Error, m);
        public static void LogError(object m, Object ctx) => Emit(LogType.Error, m);
    }

    // Mirrors Unity's "destroyed object == null" semantics.
    public class Object
    {
        public string name { get; set; }
        public HideFlags hideFlags { get; set; }
        public bool Destroyed;

        public static bool operator ==(Object a, Object b)
        {
            bool an = ReferenceEquals(a, null), bn = ReferenceEquals(b, null);
            if (an && bn) return true;
            if (bn) return a.Destroyed;
            if (an) return b.Destroyed;
            return ReferenceEquals(a, b);
        }

        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => other is Object o ? this == o : (other == null && Destroyed);
        public override int GetHashCode() => base.GetHashCode();

        private static readonly List<Object> PendingDestroys = new List<Object>();

        public static void DestroyImmediate(Object o)
        {
            if (ReferenceEquals(o, null) || o.Destroyed) return;

            if (o is GameObject gameObject)
            {
                // Unity destroys the whole hierarchy: components and child GameObjects included.
                var components = gameObject.ComponentList.ToList();
                var children = gameObject.transform.ChildList.ToList();
                foreach (var child in children) DestroyImmediate(child.gameObject);
                foreach (var component in components) component.Destroyed = true;
                gameObject.Destroyed = true;
                return;
            }

            o.Destroyed = true;
        }

        // Unity defers Destroy to the end of the frame; the rig processes it on each coroutine yield.
        public static void Destroy(Object o)
        {
            if (ReferenceEquals(o, null) || o.Destroyed) return;
            if (!PendingDestroys.Contains(o)) PendingDestroys.Add(o);
        }

        internal static void ProcessPendingDestroys()
        {
            if (PendingDestroys.Count == 0) return;

            var pending = PendingDestroys.ToArray();
            PendingDestroys.Clear();
            foreach (var o in pending) DestroyImmediate(o);
        }

        public static void DontDestroyOnLoad(Object o) { }

        // Unity's Instantiate of an asset: a NEW managed object (constructor runs) with the serialized fields copied.
        internal Object ShallowClone()
        {
            var clone = (Object)Activator.CreateInstance(GetType(), true);
            for (var t = GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    f.SetValue(clone, f.GetValue(this));
            return clone;
        }

        public static T Instantiate<T>(T original) where T : Object => Instantiate(original, null);

        public static T Instantiate<T>(T original, Transform parent) where T : Object
        {
            if (ReferenceEquals(original, null)) throw new ArgumentException("The Object you want to instantiate is null.");
            var src = original as GameObject ?? (original as Component)?.gameObject;
            if (src == null)
            {
                // Assets (ScriptableObject): a shallow clone; a parent is meaningless for them.
                if (original is ScriptableObject)
                {
                    var assetClone = original.ShallowClone();
                    assetClone.Destroyed = false;
                    return (T)assetClone;
                }
                throw new NotSupportedException("stub: only GameObject/Component/ScriptableObject can be instantiated");
            }
            var clone = src.CloneHierarchy(parent);
            if (original is GameObject) return (T)(object)clone;
            int idx = src.ComponentList.IndexOf((Component)(object)original);
            return (T)(object)clone.ComponentList[idx];
        }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; internal set; }
        public Transform transform => gameObject.transform;

        public Component GetComponent(Type type) => ReferenceEquals(gameObject, null) ? null : gameObject.FindOnSelf(type);
        public T GetComponent<T>() where T : class => (T)(object)GetComponent(typeof(T));
        public Component GetComponentInChildren(Type type, bool includeInactive) => gameObject.FindInChildren(type, includeInactive);
        public Component GetComponentInParent(Type type, bool includeInactive) => gameObject.FindInParent(type, includeInactive);
    }

    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => (T)Activator.CreateInstance(typeof(T), true);
    }

    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { internal bool AwakeDone; }

    public class Transform : Component
    {
        internal readonly List<Transform> ChildList = new List<Transform>();
        public Transform parent { get; private set; }
        public int childCount => ChildList.Count;
        public Transform GetChild(int index) => ChildList[index];

        internal void SetParentNoAwake(Transform p)
        {
            parent?.ChildList.Remove(this);
            parent = p;
            p?.ChildList.Add(this);
        }

        public void SetParent(Transform p, bool worldPositionStays)
        {
            SetParentNoAwake(p);
            gameObject.AwakeIfActive();
        }
    }

    public class GameObject : Object
    {
        internal readonly List<Component> ComponentList = new List<Component>();
        public bool activeSelf { get; private set; } = true;
        public Transform transform { get; }

        public GameObject() : this("GameObject") { }

        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform { gameObject = this };
            ComponentList.Add(transform);
        }

        public bool activeInHierarchy => activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);

        public void SetActive(bool value) { activeSelf = value; if (value) AwakeIfActive(); }

        public T AddComponent<T>() where T : Component, new()
        {
            var c = new T { gameObject = this };
            ComponentList.Add(c);
            if (c is MonoBehaviour mb && activeInHierarchy) RunAwake(mb);
            return c;
        }

        internal Component FindOnSelf(Type t) => ComponentList.FirstOrDefault(c => t.IsInstanceOfType(c));

        internal Component FindInChildren(Type t, bool includeInactive)
        {
            if (!includeInactive && !activeInHierarchy) return null;
            var own = FindOnSelf(t);
            if (own != null) return own;
            foreach (var child in transform.ChildList)
            {
                var found = child.gameObject.FindInChildren(t, includeInactive);
                if (found != null) return found;
            }
            return null;
        }

        internal Component FindInParent(Type t, bool includeInactive)
        {
            for (var go = this; go != null; go = go.transform.parent?.gameObject)
                if (includeInactive || go.activeInHierarchy)
                {
                    var c = go.FindOnSelf(t);
                    if (c != null) return c;
                }
            return null;
        }

        public Component GetComponent(Type type) => FindOnSelf(type);
        public T GetComponent<T>() where T : class => (T)(object)FindOnSelf(typeof(T));

        public T GetComponentInChildren<T>(bool includeInactive) => (T)(object)FindInChildren(typeof(T), includeInactive);

        public T GetComponentInChildren<T>() => GetComponentInChildren<T>(false);

        public T[] GetComponentsInChildren<T>(bool includeInactive)
        {
            var list = new List<T>();
            Collect(list, includeInactive);
            return list.ToArray();
        }

        // Unity clears the list before filling it.
        public void GetComponentsInChildren<T>(bool includeInactive, List<T> results)
        {
            results.Clear();
            Collect(results, includeInactive);
        }

        private void Collect<T>(List<T> into, bool includeInactive)
        {
            if (!includeInactive && !activeInHierarchy) return;
            into.AddRange(ComponentList.OfType<T>());
            foreach (var child in transform.ChildList) child.gameObject.Collect(into, includeInactive);
        }

        // ── Awake modelling: runs once, when the object first becomes active in the hierarchy ──
        internal void AwakeIfActive()
        {
            if (!activeInHierarchy) return;
            foreach (var mb in ComponentList.OfType<MonoBehaviour>().ToList()) RunAwake(mb);
            foreach (var child in transform.ChildList.ToList()) child.gameObject.AwakeIfActive();
        }

        private static void RunAwake(MonoBehaviour mb)
        {
            if (mb.AwakeDone) return;
            mb.AwakeDone = true;
            var m = mb.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            m?.Invoke(mb, null);
        }

        // ── Instantiate support ──
        internal GameObject CloneHierarchy(Transform parent)
        {
            var root = CloneNode();
            root.transform.SetParentNoAwake(parent);
            root.AwakeIfActive();
            return root;
        }

        private GameObject CloneNode()
        {
            var go = new GameObject(name);
            go.activeSelf = activeSelf;
            foreach (var c in ComponentList)
            {
                if (c is Transform) continue;
                var nc = (Component)Activator.CreateInstance(c.GetType(), true);
                for (var t = c.GetType(); t != null && !(t.Namespace ?? "").StartsWith("UnityEngine"); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        f.SetValue(nc, f.GetValue(c));
                nc.gameObject = go;
                go.ComponentList.Add(nc);
            }
            foreach (var child in transform.ChildList)
                child.gameObject.CloneNode().transform.SetParentNoAwake(go.transform);
            return go;
        }
    }
}

# November

A lightweight, single-threaded dependency injection container for Unity. It
supports constructor, field, and method injection, three service lifetimes,
child scopes, factory registrations, aliases, multi-bindings, implicit
`Func<T>` / `Lazy<T>` / collection injection, prefab instantiation that injects
before `Awake`, a startup `Validate()` pass, and `[Resolve]` component lookup on
a GameObject.

This repository contains the DI runtime and its tests, plus development and CI
tooling. The public namespace and assembly are `November.DI`.

## What is included

- `Runtime/` — `DiContainer`, the DI interfaces and attributes, injection
  metadata, the `[Resolve]` component resolver, and `link.xml` for IL2CPP code
  stripping.
- `Tests/EditMode/` — the container contract tests and component-resolution
  tests.
- `Tests/PlayMode/` — tests that need a running player loop (prefab injection
  must happen before `Awake`, which EditMode never runs).
- `CHANGELOG.md` — what changed, including breaking changes.
- `Tools~/rig/` — the .NET verification rig and mutation definitions; the
  trailing `~` keeps Unity from compiling the rig's engine stubs.
- `.github/workflows/ci.yml` — a secret-free .NET rig job, GameCI EditMode /
  PlayMode jobs gated by the `UNITY_CI_ENABLED` repository variable, and a manual
  Linux IL2CPP player build with a headless smoke run.

## Requirements

- Unity 6000.0 or newer.
- C# 9.
- Main-thread use only. The container uses plain dictionaries and per-scope
  caches instead of thread-safe collections. In the Editor and development
  builds a call from another thread throws `InvalidOperationException` naming
  both thread ids; release builds skip the check.

## Installation

Once published, install from Git in the Package Manager:

```text
https://github.com/Woolongok/November.git
```

Or clone it into a project's `Packages/` folder:

```bash
git clone https://github.com/Woolongok/November.git \
  Packages/com.woolongok.november
```

For local development, use **Window > Package Manager > + > Add package from
disk...** and select this repository's `package.json`.

## Quick start

```csharp
using November.DI;
using UnityEngine;

public interface IClock
{
    float Now { get; }
}

public sealed class UnityClock : IClock
{
    public float Now => Time.time;
}

public sealed class Session
{
    public IClock Clock { get; }

    public Session(IClock clock)
    {
        Clock = clock;
    }
}

public sealed class SessionRunner : MonoBehaviour
{
    [Inject] private IClock _clock;

    private void Awake()
    {
        var container = DiContainer.CreateOnMainThread();

        container.RegisterSingleton<IClock, UnityClock>();
        container.RegisterScoped<Session>();

        // Fail at startup, with every problem listed, instead of at the first resolve.
        container.Validate();

        // Populate [Inject] fields on an existing object.
        container.InjectInto(this);

        using (var scope = container.CreateScope())
        {
            var session = scope.Resolve<Session>();
            Debug.Log(session.Clock.Now);
        }
    }
}
```

## Container behavior

`DiContainer` implements `IDiScope`. The smaller interfaces let consumers depend
on only the part they use: `IServiceRegistry` for registration, `IServiceResolver`
for resolution, `IInjector` for injection and prefab instantiation, `IScopeFactory`
for opening child scopes, and `IDiProvider` for resolution plus injection.
`IScopeFactory` is not disposable; dispose the `IDiScope` returned by
`CreateScope()`. `IDiScope` combines registration, resolution, injection, child
scope creation, `IDisposable`, and `IAsyncDisposable`.

`DiContainer.IsDisposed` reports whether that container or scope has been
disposed. `DiContainer.IsMainThread` reports whether the current thread matches
the captured Unity main thread. `DiContainer.CreateOnMainThread()` always checks
the main-thread requirement, including in release builds; the constructor's
assertion is enabled in the Editor and development builds.

`InjectionQuery.TypeNeedsAnyInjection(typeof(ActorView))` is a main-thread-only, cached query
for scene-management code that wants to skip components with no `[Inject]` or
`[Resolve]` members. `ComponentResolver.Resolve(component)` and the
`component.ResolveComponents()` extension apply `[Resolve]` lookup without
requiring a container.

### Lifetimes and ownership

| Lifetime | Behavior |
| --- | --- |
| `Transient` | A new instance on every resolve. Built by the scope that is resolving. |
| `Singleton` | One instance for the container that owns the registration. Built, cached and disposed by that owner, from the owner's own registrations. |
| `Scoped` | One instance per resolving scope, even when the registration lives on an ancestor. Built, cached and disposed by the resolving scope. |

A scope never builds an object from its parent's point of view: a transient or
scoped service registered on the root but resolved through a child scope sees
the child's registrations (and local overrides) for all of its dependencies.

Ownership follows the scope that actually builds the object, not the scope that
happens to hold the registration. Singletons belong to the container that owns
their registration; scoped and transient disposables belong to the resolving
scope. Ownership is tracked centrally by object identity, so a factory that
forwards a parent singleton through a child scope cannot make the child claim or
dispose it. An object is disposed once, by its real owner; a cached reference to
an object whose owner was already disposed is not served again.

`RegisterInstance` hands out an object the caller owns. It is never injected and
never disposed by the container. Registering an object the container had already
built transfers ownership back to the caller. Replacing a binding drops the old
cached reference immediately but disposes the old instance later, when its owning
scope is disposed, in reverse creation order.

### Registration

```csharp
container.Register<IService, ServiceImpl>(Lifetime.Transient);
container.Register(typeof(IService), typeof(ServiceImpl), Lifetime.Singleton);
container.RegisterSingleton<IService, ServiceImpl>();   // also RegisterScoped / RegisterTransient
container.RegisterSingleton<ServiceImpl>();             // concrete type, no interface
container.RegisterInstance<IConfig>(config);
container.RegisterFactory<ISession>(provider =>
    new Session(provider.Resolve<IClock>()), Lifetime.Scoped);
container.ReplaceInstance<IConfig>(replacementConfig);
container.ReplaceFactory<IService>(provider =>
    new ServiceImpl(), Lifetime.Singleton);

// One object behind several service types.
container.RegisterSingleton<Player>();
container.RegisterAlias<IDamageable, Player>();
```

Each service type has one binding per container unless you say otherwise:

| Call | Effect |
| --- | --- |
| `Register*` | Adds the binding. A second one for the same type in the same container throws. |
| `Replace*` | Swaps every existing binding of the type in this container (cached instances of the old bindings are dropped). |
| `Add` | Appends another binding (multi-binding); `Resolve` returns the last one, `ResolveAll` returns all. |

A child scope may register the same type again: its binding shadows the parent's
for that scope only. Re-registering a scene object whose previous registration
was destroyed (scene unload) is allowed without `Replace`.

A duplicate `RegisterInstance` always throws; use `ReplaceInstance` to replace
a caller-owned object explicitly. The legacy
`DuplicateRegistrationPolicy.ReplaceWithWarning` option applies to duplicate
type, factory, and alias registrations, not instance registrations.

If an existing code base still re-registers type or factory bindings on every
scene load, set
`DiContainerOptions.DuplicateRegistrations = DuplicateRegistrationPolicy.ReplaceWithWarning`
for the old replace-and-warn behavior while you migrate to `Replace`.

Registration validates before it changes anything. A lifetime must be one of
`Transient`, `Singleton`, or `Scoped`; service and implementation types must be
closed, non-void, non-pointer, non-by-ref types; implementations must be concrete
classes rather than structs, arrays, delegates, open generic types, or
`UnityEngine.Object` types. An alias whose target already leads back to it is
rejected when the second alias is registered. A rejected registration leaves
existing bindings, caches, and ownership untouched.

### Resolution

```csharp
var service = container.Resolve<IService>();
var all = container.ResolveAll<IPlugin>();           // ancestors' bindings first, built by this scope
var local = container.ResolveAll<IPlugin>(includeParents: false);
var has = container.HasRegistration<IService>();      // silent probe, never logs
```

- Unregistered services throw `InvalidOperationException`; the message names the
  full path (`A -> B -> field 'c' on B`) that led there.
- `Resolve` uses the last binding in the nearest scope. `ResolveAll` returns
  ancestor bindings first and then local bindings, preserving each scope's
  registration order; `includeParents: false` restricts it to the current scope.
- `TryResolve` returns `false` **only** when nothing is registered. A throwing
  constructor or factory, a dependency cycle, a disposed container or a wrong
  thread all propagate. The first miss per type logs one warning per root
  container; turn that off with `DiContainerOptions.WarnOnTryResolveMiss`, or
  probe with `HasRegistration`.
- A factory result is checked before injection or caching. Null, a destroyed
  Unity object, or a value that is not assignable to the registered service type
  throws immediately and is never placed in the resolution cache.
- `Func<T>` and `Lazy<T>` work for any resolvable `T` without registering them.
  `Func<T>` resolves again on every call; `Lazy<T>` resolves on first `.Value`,
  and a failed `.Value` is **not** cached, so it can succeed on a later call.
  Asking for them when `T` is not registered throws immediately.
- `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>`,
  `ICollection<T>` and `T[]` (reference-type `T`) inject every binding of `T`
  (an empty array when there are none), exactly as `ResolveAll<T>` would.

### Injection

Order for an object the container builds: constructor, then `[Inject]` fields
(base classes first), then `[Inject]` methods (base classes first), then `[Resolve]`
fields. Within one declaring type, fields and methods run in declaration order,
not reflection order.

Every `[Inject]` dependency is required. A missing dependency aborts construction
or `InjectInto`; `[Inject]` has no optional or best-effort mode.

- `[Inject]` marks constructors, fields, and methods. Constructor injection uses
  the `[Inject]` constructor, or the single public constructor when there is
  exactly one. Two `[Inject]` constructors, or several public constructors
  without `[Inject]`, are an error that says so.
- Static `[Inject]` fields and methods, static `[Resolve]` fields, and generic
  `[Inject]` methods are rejected when the type is first inspected instead of
  being silently ignored.
- Private fields and private base-class members are injected.
- Auto-properties use the field target: `[field: Inject] public IFoo Foo { get; private set; }`.
- A virtual `[Inject]` method runs once, not once per override.
- Constructor and `[Inject]`-method parameters with default values fall back to
  the default when the type is not registered. An unregistered *interface or
  abstract* parameter that quietly becomes `null` is reported once, because it
  usually hides a mis-wire.
- A factory that returns an object the container already injected and cached
  (for example a forwarding factory) does not inject it a second time.
- Exceptions thrown by a constructor or `[Inject]` method arrive unwrapped (not
  as `TargetInvocationException`), and carry the resolution path in
  `exception.Data[DiContainer.ResolutionPathKey]`. The container itself never
  logs errors while resolving: your code decides what to do with the exception.

### `[Resolve]`

`[Inject]` means "get it from the container"; `[Resolve]` means "find it in the
GameObject hierarchy":

```csharp
public sealed class ActorView : MonoBehaviour
{
    [Resolve] private Animator _animator;                                   // same GameObject
    [Resolve(Source = ComponentSource.Children)] private Renderer _body;    // this object or a child
    [Resolve(Source = ComponentSource.Parent)] private Canvas _canvas;      // this object or a parent
    [Resolve(Optional = true)] private AudioSource _audio;                  // may be absent
}
```

A required miss clears the field to `null` (so a stale Inspector reference cannot
hide a missing component) and logs a warning. `Optional = true` misses are silent
and leave the field untouched. Child and parent searches include inactive
objects. Fields that are neither a `Component` nor an interface are reported once
and skipped.

### Prefabs and hierarchies

```csharp
var enemy = container.InstantiatePrefab(enemyPrefab, parent);   // injected before Awake / OnEnable
container.InjectGameObjectHierarchy(existingRoot);              // inactive children included
```

`InstantiatePrefab` clones under a hidden inactive holder, injects the whole
hierarchy, then re-parents the clone, which activates it when its own state and
the new parent allow. `Awake` and `OnEnable` therefore see every `[Inject]` and
`[Resolve]` field. If injection fails the cloned root GameObject is destroyed,
along with its components and children, and the exception is rethrown; the source
prefab is untouched. A `ScriptableObject` can be passed too; it is cloned and
injected. `InjectGameObjectHierarchy` cannot help objects that already ran `Awake`.
Missing-script entries in a hierarchy are skipped with a warning.

### Scopes and disposal

- Child scopes inherit parent registrations and can override them locally.
- A scope disposes, in reverse creation order, every `IDisposable` /
  `IAsyncDisposable` it built and owns: its singletons, its scoped services, and
  the transient disposables resolved **through it**. The same object is disposed
  once across all scopes, even when two registrations or a forwarding factory
  hand it out. `RegisterInstance` objects are not disposed.
- Disposing a parent does not dispose child scopes. Each scope is an independent
  lifetime boundary and must be disposed separately.
- `await scope.DisposeAsync()` awaits `IAsyncDisposable` instances (preferred
  over `IDisposable` when a type has both). Every caller made while cleanup is
  running awaits the same completion task, so `DisposeAsync` is idempotent.
  Plain `Dispose()` accepts an async disposal that finishes synchronously; if the
  scope is already being disposed asynchronously it logs an error naming the
  still-running cleanup instead of blocking the main thread or pretending the
  work finished.
- If construction or injection fails, the half-built object is cleaned up before
  the exception escapes: a Unity object is destroyed, an `IDisposable` is
  disposed, and an async-only disposable is cleaned up when it completes
  synchronously (otherwise an error explains what was left behind). Caller-owned
  and already-owned objects are never cleaned up by the failing attempt.
- Everything on a disposed scope throws `ObjectDisposedException`. A live child of
  a disposed parent that needs one of the parent's singletons gets an error that
  says the owner was disposed.
- Resolve per-action disposables through a short-lived scope: the root keeps a
  transient disposable until the root itself is disposed. In the Editor and
  development builds this logs one warning per type
  (`WarnOnRootTransientDisposables`).

### Validation

`ValidateScopes` (default **on** in the Editor and development builds, off in
release builds) rejects a scoped service resolved straight from the root container
and singletons that capture scoped services, both of which would otherwise
silently behave like singletons. A singleton owned by a child scope and a scoped
service resolved by that same child are released together, so that pair is valid.

`container.Validate()` walks every registration visible from the scope without
constructing anything and throws one exception listing every problem. It uses the
same view as resolution:

- a singleton is checked from its owning container's perspective, while scoped
  and transient services are checked from the resolving scope;
- an alias keeps the consumer's resolution scope and lifetime; it is a graph
  edge, not a new lifetime boundary;
- a collection expands to each visible binding in `ResolveAll` order;
- `Func<T>` / `Lazy<T>` are deferred for cycle detection, so they can break an
  eager cycle, but their target is still checked for registration, graph
  validity, scope, and lifetime compatibility.

It reports unregistered constructor and `[Inject]` dependencies, ambiguous
constructors, eager cycles, captive dependencies, dead instances, and dangling
aliases, including the full dependency path. Factory delegates are opaque: their
bodies cannot be inspected, so a graph that is invalid only inside a factory is
not detected by `Validate()` (the factory result checks still apply at runtime).
`container.DescribeRegistrations()` returns a readable dump of every scope level
for debugging.

### Options

```csharp
var container = DiContainer.CreateOnMainThread(options: new DiContainerOptions
{
    ValidateScopes = true,
    WarnOnRootTransientDisposables = true,
    WarnOnTryResolveMiss = true,
    DuplicateRegistrations = DuplicateRegistrationPolicy.Throw,
});
```

| Option | Default | Effect |
| --- | --- | --- |
| `ValidateScopes` | Editor / development: `true`; release: `false` | Reject root-scoped resolution and captive singleton dependencies. |
| `WarnOnRootTransientDisposables` | Editor / development: `true`; release: `false` | Warn once per type when a root-resolved transient disposable is retained until root disposal. |
| `WarnOnTryResolveMiss` | `true` | Warn once per missing type per root container. |
| `DuplicateRegistrations` | `Throw` | `ReplaceWithWarning` restores replace-and-warn for duplicate type, factory, and alias registrations; duplicate instances still throw. |

The options are copied when the root is created and shared by every scope created
from it. A child created with `new DiContainer(parent, options)` receives its own
copy of the supplied options; otherwise it inherits the parent's options.

### IL2CPP and code stripping

The container builds objects by reflection, so managed code stripping can remove
constructors and `[Inject]` members that nothing else references.

- `Runtime/link.xml` preserves this package. Unity picks up `link.xml` files that
  live inside packages.
- Your own types that are only reached through the container need the same
  protection under Medium / High stripping: put `[UnityEngine.Scripting.Preserve]`
  on them (or on their `[Inject]` members), or list their assembly in a
  `link.xml` in your project.
- `Func<T>` / `Lazy<T>` of a **value-type** `T` needs an AOT hint (a generic
  instantiation the compiler can see); reference-type `T` is fine.

### Threading

Everything, including `InjectionQuery` and the metadata cache, is main-thread
only; there is no cross-thread safety guarantee. In the Editor and development
builds, wrong-thread container calls throw. Those assertions are omitted from
release builds. Metadata is built lazily per type. Its cache, the `Func<T>` /
`Lazy<T>` creator caches, and the injection-marker epoch are reset on domain
reload and when entering play mode with "Enter Play Mode Options", so objects
marked as injected in a previous play session are injected again.

## Tests

The EditMode suite covers lifetimes and ownership, registration rules,
fail-fast resolution, constructor/field/method injection, `Func<T>` / `Lazy<T>`,
collections, aliases, scope inheritance, disposal (sync and async), cycle
detection, `Validate()`, error paths, and `[Resolve]` lookup. The PlayMode suite
checks that prefab and ScriptableObject injection happen before `Awake`.

To run the tests in a host project, add the package to `testables` in
`Packages/manifest.json`:

```json
{
  "testables": [
    "com.woolongok.november"
  ]
}
```

Then open **Window > General > Test Runner** and run the EditMode and PlayMode
suites.

### Continuous integration

The `.NET rig tests` GitHub Actions job needs no secrets: it installs .NET 8 and
runs the runtime and source tests against the engine stubs in `Tools~/rig` on
every push and pull request. Run the same check locally with:

```bash
dotnet run --project Tools~/rig/rig.csproj \
  -p:RuntimeDir=../../Runtime -p:TestsDir=../../Tests
```

The Unity `editmode` and `playmode` jobs run both suites in UPM package mode with
Unity `6000.0.85f1` through GameCI. They only start when the repository variable
`UNITY_CI_ENABLED` is set to `true`, and they require the repository secrets
`UNITY_LICENSE`, `UNITY_EMAIL`, and `UNITY_PASSWORD`; GameCI cannot authenticate
the Editor without them. Leave the variable unset on a repository whose secrets
are not configured, because a gated job that tries to authenticate fails rather
than skipping. Pull requests from forks cannot read secrets even when the variable
is set.

The `il2cpp` job is manual: start the workflow with **Run workflow**, enable
`run_il2cpp`, and it builds a high-stripping Linux IL2CPP player against the same
Unity version, runs it headlessly under `xvfb-run`, and fails unless the player
exits successfully within three minutes and logs its completion marker. It is
gated by the same `UNITY_CI_ENABLED` variable. The workflow has not yet run in
this repository, and a passing rig run does not replace real Unity or IL2CPP
verification.

## Upgrading

See [CHANGELOG.md](CHANGELOG.md) for breaking changes. The short version:
`IServiceProvider` is now `IDiProvider`, a second `Register` for a type throws
unless you use `Replace`, and transient disposables are now disposed by the scope
that resolved them.

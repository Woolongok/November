# November

A lightweight, single-threaded dependency injection container for Unity. It
supports constructor, field, and method injection, three service lifetimes,
child scopes, factory registrations, `Func<T>` / `Lazy<T>` resolution, prefab
instantiation, and `[Resolve]` component lookup on a GameObject.

This repository contains only the DI runtime and its tests. The public namespace
and assembly are `November.DI`.

## What is included

- `Runtime/` — `DiContainer`, the DI interfaces and attributes, injection
  metadata, and the `[Resolve]` component resolver.
- `Tests/EditMode/` — the container contract tests and component-resolution
  tests.

## Requirements

- Unity 6000.0 or newer.
- C# 9.
- Main-thread use only. The container deliberately uses plain dictionaries and
  per-scope caches instead of thread-safe collections.

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

        container.Register<IClock, UnityClock>(Lifetime.Singleton);
        container.Register<Session, Session>(Lifetime.Scoped);

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

### Lifetimes

| Lifetime | Behavior |
| --- | --- |
| `Transient` | A new instance on every resolve. |
| `Singleton` | One instance per container that owns the registration. |
| `Scoped` | One instance per resolving scope, even when the descriptor lives on an ancestor. |

### Registration and resolution

```csharp
container.Register<IService, ServiceImpl>(Lifetime.Transient);
container.Register(typeof(IService), typeof(ServiceImpl), Lifetime.Singleton);
container.RegisterInstance<IConfig>(config);
container.RegisterFactory<ISession>(provider =>
    new Session(provider.Resolve<IClock>()), Lifetime.Scoped);

var service = container.Resolve<IService>();
var all = container.ResolveAll<IPlugin>();
var hasRegistration = container.HasRegistration(typeof(IService));
```

Unregistered services throw. `TryResolve` returns `false` only when no
registration exists; construction failures are rethrown. `Func<T>` creates a
new resolution per invocation, and `Lazy<T>` defers resolution until `.Value`.

### Injection

- `[Inject]` marks constructors, fields, and methods.
- Constructor injection uses the `[Inject]` constructor, or the single public
  constructor when there is exactly one.
- `[Resolve]` fills a field from the same GameObject using `GetComponent`:

```csharp
public sealed class ActorView : MonoBehaviour
{
    [Resolve] private Animator _animator;
}
```

- `container.InstantiatePrefab(prefab, parent)` injects a new prefab hierarchy.
- `container.InjectGameObjectHierarchy(root)` injects an existing hierarchy.

### Scopes and disposal

- Child scopes inherit parent registrations and can override them locally.
- Disposing a parent does not dispose child scopes. Each scope is an independent
  lifetime boundary and must be disposed separately.
- Disposable instances created by a scope are disposed in reverse creation
  order. Externally registered instances are not disposed.
- Resolving after disposal throws `ObjectDisposedException`.

## Tests

The EditMode suite covers lifetimes, registration validation, fail-fast
resolution, constructor/field/method injection, `Func<T>` / `Lazy<T>`, scope
inheritance, disposal, cycle detection, and `[Resolve]` lookup.

To run the tests in a host project, add the package to `testables` in
`Packages/manifest.json`:

```json
{
  "testables": [
    "com.woolongok.november"
  ]
}
```

Then open **Window > General > Test Runner** and run the EditMode suite.

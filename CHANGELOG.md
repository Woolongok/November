# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[Semantic Versioning](https://semver.org/).

## [2.0.0] - 2026-11-04

This release fixes ownership and lifetime bugs that could not be fixed without
changing behavior, so several things are breaking. Each breaking item says what
to do about it.

### Breaking changes

- **`IServiceProvider` is now `IDiProvider`.** The old name clashed with
  `System.IServiceProvider`. Rename the type wherever you name it, for example in
  `RegisterFactory` lambdas that spell out the parameter type.
- **`IScopeFactory` no longer extends `IDisposable`; `IDiScope` now extends
  `IScopeFactory` and `IAsyncDisposable`.** A scope can open a child scope
  without a cast, and a factory is no longer something you dispose.
- **A second `Register*` for the same service type in one container throws**
  `InvalidOperationException` instead of silently replacing the first. Use
  `Replace*` to override on purpose, `Add` for several implementations, or
  register in a child scope to shadow. Duplicate `RegisterInstance` calls always
  throw and must be replaced with `ReplaceInstance`. While migrating duplicate
  type, factory, or alias registrations, set
  `DiContainerOptions.DuplicateRegistrations = DuplicateRegistrationPolicy.ReplaceWithWarning`
  to keep the old replace-and-warn behavior for those registration kinds.
- **Inherited Transient and Scoped services are built by the resolving scope.**
  Before, a Transient registered on the root and resolved through a child scope
  was built by the root, so it could not see the child's scoped services or local
  overrides, and `ResolveAll<T>` / collection parameters had the same problem.
  Singletons are still built by the container that owns the registration, now
  strictly from that container's own registrations.
- **Transient disposables are disposed by the scope that resolved them.** Before,
  the registering container kept every one until it was itself disposed.
  Resolving per-action disposables from the root still works but now warns once
  per type in the Editor and development builds
  (`DiContainerOptions.WarnOnRootTransientDisposables`).
- **`ValidateScopes` is on by default in the Editor and development builds.**
  Resolving a Scoped service straight from the root container throws, and
  `Validate()` reports singletons that capture a scoped dependency. Set
  `ValidateScopes = false` to get the old behavior (the root treats Scoped as
  Singleton).
- **`TryResolve` returns `false` only when nothing is registered.** A throwing
  constructor or factory, a dependency cycle, a disposed container and a
  wrong-thread call now propagate instead of being reported as a miss.
- **`Func<T>` and `Lazy<T>` of an unregistered `T` throw when requested**, not
  when invoked or on first `.Value`.
- **The container no longer logs errors while resolving.** The exception is the
  report; it is thrown once and carries the resolution path in
  `exception.Data[DiContainer.ResolutionPathKey]`. Constructor and `[Inject]`
  method exceptions are no longer wrapped in `TargetInvocationException`.
- **Removed `DiContainer.ResolveFailureCount` and
  `DiContainer.ClearResolveFailureHistory()`.** They were process-wide statics
  that made tests depend on order. Once-only warnings are now tracked per root
  container.
- **A disposed container throws `ObjectDisposedException` from every entry
  point**, including `TryResolve`, `ResolveAll`, `HasRegistration`, the
  registration methods, `InjectInto`, `InstantiatePrefab` and
  `InjectGameObjectHierarchy`. A live child of a disposed parent that needs one of
  the parent's singletons gets an error that names the disposed owner.
- **`DiContainer`'s constructor and `CreateOnMainThread` take an optional
  `DiContainerOptions`.**
- **Two `[Inject]` constructors on one type are an error** that says so, instead
  of the first one being picked silently. Mark exactly one.
- **Static `[Inject]` fields/methods, static `[Resolve]` fields, and generic
  `[Inject]` methods are rejected.** They were previously skipped silently.
  Remove the attribute, make the member an instance member, or use a
  non-generic method.

### Added

- `Replace`, `ReplaceInstance`, `ReplaceFactory`, `Add` (multi-binding) and
  `RegisterAlias` (one object behind several service types).
- `RegisterInstance(Type, object)`, `RegisterFactory(Type, ...)`, the
  `Register<TImpl>()` shorthand, `RegisterSingleton` / `RegisterScoped` /
  `RegisterTransient`, and `HasRegistration<T>()`.
- Implicit `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`,
  `IList<T>`, `ICollection<T>` and `T[]` injection for reference-type `T`.
- `DiContainerOptions` (`ValidateScopes`, `WarnOnRootTransientDisposables`,
  `WarnOnTryResolveMiss`, `DuplicateRegistrations`).
- `Validate()` for startup validation (unregistered dependencies, ambiguous
  constructors, cycles, captive dependencies, dead instances, dangling aliases)
  and `DescribeRegistrations()` for debugging.
- `[Resolve(Source = ComponentSource.Children | Parent, Optional = true)]`.
- `IAsyncDisposable` support and `DisposeAsync()` on containers and scopes.
- `[field: Inject]` for auto-properties; private base-class fields and methods
  are injected.
- `InstantiatePrefab` accepts a `ScriptableObject` (cloned and injected).
- `Runtime/link.xml` and `[Preserve]` on the reflection entry points for IL2CPP
  code stripping.
- PlayMode tests for prefab injection before `Awake`, a log-counting test helper,
  and a GitHub Actions workflow with a secret-free .NET rig job. The GameCI
  EditMode / PlayMode jobs are gated by the `UNITY_CI_ENABLED` repository variable;
  the manual IL2CPP job builds a high-stripping Linux player and runs it headlessly.
  The workflow has not yet run.

### Changed

- **Prefabs are injected before `Awake` / `OnEnable`.** `InstantiatePrefab`
  clones under a hidden inactive holder, injects the hierarchy, then re-parents
  the clone. Previously `Awake` ran before any field was set.
- `Lazy<T>` no longer caches a failed `.Value`; a later access retries.
- A virtual `[Inject]` method runs once, not once per override in the hierarchy.
- `[Inject]`-method parameters honor default values, like constructors. An
  unregistered optional *interface or abstract* parameter that falls back to its
  default is reported once instead of silently becoming `null`.
- A factory that returns an object the container already injected and cached no
  longer injects or disposes it a second time.
- The same object is disposed once, even if several registrations hand it out;
  disposal runs in reverse creation order.
- `InjectGameObjectHierarchy` and `[Resolve(Source = Children | Parent)]` search
  inactive objects, and objects with a missing script are skipped with a warning
  instead of throwing.
- A `[Resolve]` miss on a required field still clears the field and warns; the
  warning for fields that can never be resolved (not a `Component` or interface)
  is now reported once per type.
- Injection metadata is built lazily per type and cached; type walks stop at
  `System.*` and `UnityEngine.*`. Resolving a cached singleton no longer
  allocates.
- The main-thread id is re-captured when the Editor loads the domain and when the
  player starts, so the first thread to touch the container no longer decides
  which thread is "main". Static caches are reset for "Enter Play Mode Options".
- `TryResolve`'s miss warning is once per type per root container instead of a
  process-wide static.
- `[Inject]` fields and methods now run in declaration order within a declaring
  type instead of unspecified reflection order.
- Registration validates before mutating state. Undefined `Lifetime` values,
  open generic / pointer / by-ref / void service types, and non-constructible
  implementations (arrays, delegates) are rejected; a second alias that closes a
  cycle is rejected at registration instead of on first resolve.
- Documentation comments were corrected and the README documents ownership,
  injection order, options and stripping.

### Fixed

- A stale Scoped instance surviving a local override or `Replace`.
- Inherited Scoped and Transient services, and `ResolveAll<T>`, seeing the wrong
  scope's registrations.
- A disposed container that still half-worked, and a child scope that kept
  resolving singletons from a disposed parent.
- A registered scene object that was destroyed (scene unload) being handed out
  again, or blocking re-registration.
- `TryResolve` hiding real construction failures behind `false`.
- A missing-registration exception now stores the full resolution path, including the
  missing type, in `exception.Data[DiContainer.ResolutionPathKey]`.
- `Validate()` disagreeing with runtime resolution: it now follows transients,
  deferred `Func<T>` / `Lazy<T>` targets, collection bindings, aliases, and child
  scope overrides. Factories remain opaque by design.
- Cross-scope double disposal and ownership transfer. A factory forwarding a
  parent-owned singleton no longer lets the child claim it, a cached borrowed
  reference is not served after its owner is disposed, and a rejected duplicate
  `RegisterInstance` no longer strips the container's ownership or tracking.
- `DisposeAsync()` returning early for later callers. All callers now await the
  same cleanup task; `Dispose()` during a running async cleanup logs an explicit
  error instead of reporting success.
- Failed prefab injection destroying only the requested component and leaving the
  cloned GameObject hierarchy alive. The cloned root, its components and its
  children are now removed.
- Factory results being injected or cached before validation, and half-built
  factory results leaking on failed injection. Null, destroyed, and incompatible
  results are rejected, and failed construction cleans up `IDisposable`,
  `IAsyncDisposable` (when synchronous), and Unity objects without touching
  caller-owned or already-owned objects.
- Injection markers surviving a new play session with domain reload disabled, so
  a previously injected object could be skipped. Markers are now epoch-scoped and
  reset with the static caches.
- Static and generic injection members being silently ignored; they now fail with
  an error naming the member.

## [1.0.0]

Initial release.

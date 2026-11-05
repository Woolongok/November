# November.DI 2.0.0 — fix map

Where each flaw from my review is fixed, and what guards the fix. Read "Limits" first.

## Limits

1. **Nothing here was run in Unity.** I wrote a small stand-in for the slice of Unity and NUnit that the package and its
   tests touch (a "rig") and ran 158 tests in it. 29 of them are your original tests, unchanged apart from one removed
   `LogAssert.Expect`. The rig is my own code, so a pass there is evidence, not proof. Please run the EditMode and
   PlayMode suites in Unity 6 before you merge.
2. **I can no longer see my original numbered list of minor flaws (M1–M40).** That part of the conversation was
   compacted away. C1–C10 are mapped exactly. For the minor flaws I list the numbers my own working notes carried; a
   second table lists fixes whose number I do not have, described by what they changed. If a number here does not match
   your copy of the list, trust your copy and tell me.
3. **Behaviour that depends on real Unity and that I could not test:** prefab injection before `Awake` and the scene the
   clone lands in (C9), code stripping on IL2CPP (C10), objects with a missing script (M18), the main-thread id being
   re-captured at domain load and player start, and the reset of static caches for "Enter Play Mode Options". My notes
   also flag M17, M19 and M26 as Unity-dependent; I cannot tell you which of the three behaviours above each number was.
4. **Not done:** keyed bindings and open generics (part of M6). M13 is partial: the `DiContainer` constructor stays
   public; the public statics (`ResolveFailureCount`, `ClearResolveFailureHistory()`) were removed.
5. **Untested by me:** `.github/workflows/ci.yml`. The Unity version (`6000.0.23f1`) and the licence secrets are
   placeholders.
6. **Numbers:** allocations were measured on .NET 8, not Unity's Mono or IL2CPP, so only the direction carries over.
   A cached-singleton `Resolve` allocated 80 B before and 0 B now; a transient with one constructor dependency allocated
   352 B before and 56 B now.
7. **Your decisions:** I bumped the version to 2.0.0 because several changes are breaking (see CHANGELOG). I did not add
   a licence file or `license` field. I left the `[Resolve]` / `[Inject]` naming alone.

## Files

Delete from your repo: `Runtime/IServiceProvider.cs` and `Runtime/IServiceProvider.cs.meta` (the type is now
`IDiProvider`). Everything else in the zip replaces or adds to what you have. New files: `AssemblyInfo.cs`,
`DiContainer.Diagnostics.cs`, `DiContainer.Validation.cs`, `DiContainerOptions.cs`, `IDiProvider.cs`, `MainThread.cs`,
`ResolutionState.cs`, `ServiceRegistryExtensions.cs`, `link.xml`, `CHANGELOG.md`, `.github/workflows/ci.yml`, and the
tests `TestSupport.cs`, `DiContainerLifetimeTests.cs`, `DiContainerRegistrationTests.cs`, `DiContainerInjectionTests.cs`,
`ComponentResolverSourceTests.cs`, `PrefabEditModeTests.cs` and the whole `Tests/PlayMode` folder.

## Critical flaws

| ID | What the fix does | Where | Guard |
|---|---|---|---|
| C1 | Inherited Transient and Scoped services are built (Scoped also cached) by the scope that resolves them, from that scope's registrations. Singletons stay with the container that owns the registration. | `DiContainer.Resolution.cs` | `InheritedTransient_IsBuiltByTheResolvingScope_NotByTheAncestor`; mutations 1, 2 |
| C2 | `ResolveAll<T>` and collection parameters build every entry in the resolving scope. | `DiContainer.Resolution.cs` | `ResolveAll_BuildsScopedBindingsInTheResolvingScope`; mutation 3 |
| C3 | Every entry point checks for disposal. A live child of a disposed parent gets an error that names the disposed owner. | `DiContainer.cs`, `.Resolution`, `.Registration`, `.Injection`, `.Validation`, `.Disposal` | `AfterDispose_EveryEntryPoint_ThrowsObjectDisposedException`, `ChildOfADisposedParent_GetsAnAccurateError_ForAnInheritedSingleton`; mutations 4–6, 36, 71–73 |
| C4 | An object is injected once and disposed once even when several registrations, aliases or factories hand it out: a weak "already injected" marker, reference-identity dedupe of tracked disposables, and `RegisterAlias`. | `DiContainer.Injection.cs`, `.Disposal`, `.Registration` | `ForwardingFactory_DoesNotInjectOrDisposeTheSameObjectTwice`, `Alias_ForwardsToTheSameInstance_InjectedOnce_DisposedOnce`; mutations 24, 25, 37, 59, 60, 74 |
| C5 | `ValidateScopes` (on by default in the Editor and development builds) makes resolving Scoped from the root throw. `Validate()` analyses the graph statically: missing dependencies, ambiguous constructors, cycles, captive dependencies, dead instances, dangling aliases. | `DiContainer.Validation.cs`, `DiContainerOptions.cs`, `DiContainer.Resolution.cs` | `ScopedResolvedFromTheRoot_Throws_WhenScopeValidationIsOn`, `SingletonCapturingAScopedService_IsReportedAsACaptiveDependency`, `Validate_*`; mutations 7, 50, 51 |
| C6 | A scope tracks and disposes the transient disposables it resolved. Resolving them from the root warns once per type (`WarnOnRootTransientDisposables`). | `DiContainer.Resolution.cs`, `.Disposal`, `.Diagnostics` | `InheritedTransientDisposable_IsReleasedWithTheScope_NotKeptByTheRoot`, `RootTransientDisposable_WarnsOncePerType_WithTheScopeAdvice`; mutations 10, 11 |
| C7 | `TryResolve` returns `false` only when nothing is registered. Constructor failures, cycles, a disposed container and wrong-thread calls propagate. | `DiContainer.Resolution.cs` | `TryResolve_*`; mutation 8 |
| C8 | Caches are keyed by registration, not by type, so a local override or `Replace` is never shadowed by a stale cached instance. | `DiContainer.cs`, `DiContainer.Resolution.cs` | `LocalScopedOverride_AfterAnInheritedScopedResolve_IsNotShadowedByTheCache`, `Replace_DropsTheCachedSingletonOfTheOldBinding`; mutation 9 |
| C9 | `InstantiatePrefab` clones under a hidden inactive holder, injects the hierarchy, then re-parents the clone, so `Awake` and `OnEnable` see injected fields. The holder lives in the active scene and is replaced when the active scene changes, so a parentless clone lands where `Object.Instantiate` would put it. A failed injection destroys the whole clone. In edit mode the holder is removed before the call returns. `InjectGameObjectHierarchy` includes inactive objects. | `DiContainer.Injection.cs`, `ResolutionState.cs`, `ComponentResolver.cs` | `Tests/PlayMode/PrefabInjectionTests.cs`, `PrefabEditModeTests.cs`; mutations 30–34, 56, 68, 79–82. **Unverified in Unity.** |
| C10 | `[Inject]` and `[Resolve]` derive from `UnityEngine.Scripting.PreserveAttribute`, so what they mark survives stripping for every install type. The `Func<T>` / `Lazy<T>` helpers are `[Preserve]`. `Runtime/link.xml` preserves the assembly when the package is embedded (Unity ignores `link.xml` in Git and registry packages). README has the guidance for your own types. | `InjectAttribute.cs`, `ResolveAttribute.cs`, `DiContainer.Factories.cs`, `link.xml`, `README.md` | `InjectAndResolveAttributes_ImplyPreserve_SoCodeStrippingKeepsWhatTheyMark`; mutations 77, 78. The test checks the inheritance only. **Behaviour under a real IL2CPP build is unverified.** |

## Minor flaws that carried a number in my notes

"Mutations" are the numbered checks in the appendix.

| ID | What the fix does | Where | Guard |
|---|---|---|---|
| M4 | A second `Register*` for the same service type throws. `Replace*`, `Add` and shadowing in a child scope are the ways to override. `DuplicateRegistrations = ReplaceWithWarning` keeps the old behaviour while you migrate. | `DiContainer.Registration.cs`, `DiContainerOptions.cs` | `Register_Twice_Throws_AndKeepsTheFirstBinding`, `RegisterInstance_AndRegister_AreSymmetric_BothThrowOnADuplicate`, `LegacyPolicy_ReplacesWithAWarning`; mutation 18 |
| M5 | The last binding wins; `Add` appends. | `DiContainer.Registration.cs`, `DiContainer.Resolution.cs` | `Add_AppendsBindings_ResolveReturnsTheLast_ResolveAllReturnsAll`; mutations 16, 19 |
| M6 (partial) | `ResolveAll`, `IEnumerable<T>`, `IReadOnlyList<T>`, `T[]` and the other collection shapes: ancestor bindings first, `includeParents: false`, an empty sequence when nothing is registered, no value-type `T[]`. **Keyed bindings and open generics are not implemented.** | `DiContainer.Resolution.cs`, `DiContainer.Factories.cs` | `ResolveAll_*`, `CollectionTypes_AreAlwaysResolvable_AndEmptyWhenNothingIsRegistered`, `CollectionsOfValueTypes_AreNotImplicitlyResolvable`; mutations 57, 58, 70 |
| M7 | `Func<T>` and `Lazy<T>` of an unregistered `T` throw when requested. | `DiContainer.Factories.cs` | `FuncAndLazy_OfAnUnregisteredService_AreNotResolvable`; mutation 15 |
| M8 | `Lazy<T>` does not cache a failed `.Value`. | `DiContainer.Factories.cs` | `Lazy_RetriesAfterAFailedValue`; mutation 40 |
| M9 | Two `[Inject]` constructors, or several public constructors and no `[Inject]`, are errors that say so. | `InjectionMetadata.cs` | `TwoInjectConstructors_AreRejected`, `TwoPublicConstructors_WithoutInject_AreRejected`; mutations 41, 42 |
| M10 | A virtual `[Inject]` method runs once. | `InjectionMetadata.cs` | `OverriddenInjectMethod_RunsOnce`; mutation 43 |
| M11 | `[Inject]`-method parameters honour default values, like constructors. | `DiContainer.Injection.cs` | `InjectMethod_HonoursParameterDefaults_LikeConstructors`; mutation 28 |
| M12 | `Register` rejects struct, `UnityEngine.Object` and open-generic implementations. | `DiContainer.Registration.cs` | `Register_StructImplementation_Throws`, `Register_ScriptableObjectImplementation_Throws`; mutations 21, 23 |
| M14 | Non-assignable implementations and instances, and null or destroyed instances, are rejected at registration. | `DiContainer.Registration.cs` | `RegisterInstance_OfTheWrongType_Throws`, `RegisterInstance_Null_Throws`; mutations 22, 61, 62 |
| M15 | `IAsyncDisposable` and `DisposeAsync()` on containers and scopes. | `DiContainer.Disposal.cs`, `IDiScope.cs` | `DisposeAsync_PrefersIAsyncDisposable_AndStillDisposesPlainDisposables`, `Dispose_AcceptsAnAsyncDisposable_ThatCompletesSynchronously`; mutations 39, 55 |
| M16 | `[Resolve(Source = Self/Children/Parent, Optional)]`; child and parent searches include inactive objects; fields that can never be resolved are reported once per type. | `ComponentResolver.cs`, `ResolveAttribute.cs` | `ComponentResolverSourceTests.*`; mutations 44, 46, 67, 68 |
| M20 | No string formatting on the resolve hot path. | `DiContainer.Resolution.cs` | `Resolve_OfACachedSingleton_DoesNotAllocate`; mutation 75 |
| M21 | No closure allocation on the resolve hot path. | `DiContainer.Resolution.cs`, `DiContainer.Factories.cs` | same test; mutation 76 |
| M30 | The container no longer logs an error on every failed resolve. The exception is the report and is thrown once. | `DiContainer.Diagnostics.cs` | `MissingRegistration_TwoLevelsDown_ThrowsOnce_WithTheFullPath_AndLogsNothing`; mutation 49 |
| M31 | Constructor exceptions are not wrapped in `TargetInvocationException`; the resolution path is in `exception.Data["November.DI.ResolutionPath"]`; a throwing `Dispose` does not stop the remaining disposals. | `DiContainer.Injection.cs`, `.Diagnostics`, `.Disposal` | `ConstructorExceptions_AreNotWrappedInTargetInvocationException`, `Dispose_ContinuesAfterADisposableThrows_AndReportsIt`; mutations 29, 48, 69 |
| M33 | Type names in messages read `Func<Foo>`, not `` Func`1 ``. | `DiContainer.Diagnostics.cs` | `PrettyName_FormatsGenericsAndArrays_WithoutBackticks`; mutation 54 |
| M13 (partial) | See "Limits", item 4. | | |
| M18 | Objects with a missing script are skipped with a warning instead of throwing. Fixed by reading the code; there is no portable test. | `DiContainer.Injection.cs` | none |

## Fixes whose flaw number I do not have

| What the fix does | Where | Guard |
|---|---|---|
| A destroyed registered or cached Unity object is never handed out again; it can be re-registered, and a destroyed singleton is evicted and rebuilt. | `DiContainer.Resolution.cs`, `DiContainer.Registration.cs` | `RegisterInstance_ReplacesAnInstanceWhoseUnityObjectWasDestroyed`, `Resolve_OfADestroyedRegisteredInstance_Throws_InsteadOfReturningTheDeadReference`, `DestroyedSingleton_IsEvictedAndRebuilt`; mutations 12, 20, 63 |
| `TryResolve` counts every miss and warns once per type per root container, with a switch (`WarnOnTryResolveMiss`). No process-wide statics. | `DiContainer.Resolution.cs`, `ResolutionState.cs` | `TryResolve_Miss_WarnsOncePerTypePerContainer_ButCountsEveryMiss`, `TryResolve_Warning_IsNotSharedAcrossContainers`; mutations 13, 14 |
| An instance whose injection failed is disposed before the exception propagates. | `DiContainer.Injection.cs` | `FailingInjection_DisposesTheHalfBuiltInstance_AndRethrowsTheOriginal`; mutation 26 |
| An unregistered optional service parameter that falls back to its default is reported once. | `DiContainer.Injection.cs` | `OptionalServiceParameter_WhenUnregistered_FallsBackLoudly`; mutation 27 |
| A factory that returns null is diagnosed. | `DiContainer.Injection.cs` | `FactoryReturningNull_Throws`; mutation 35 |
| `InjectInto(null)` throws `ArgumentNullException`; field-injection failures name the object and field. | `DiContainer.Injection.cs` | `InjectInto_Null_ThrowsArgumentNullException`, `InjectInto_AMissingDependency_NamesTheObjectAndTheField`; mutations 64–66 |
| Calls from a background thread throw in the Editor and development builds, on every resolving entry point. | `MainThread.cs`, `DiContainer.*.cs` | `EveryResolvingEntryPoint_RejectsABackgroundThread`, `Resolve_FromABackgroundThread_Throws_InEditorAndDevelopmentBuilds`; mutation 17. The main-thread id re-capture at domain load is unverified in Unity. |
| Private base-class fields and methods are injected, and base-class `[Inject]` methods run first. | `InjectionMetadata.cs` | `PrivateBaseClassFields_AreInjected`, `InjectMethods_RunBaseClassFirst`; mutation 45 |
| Disposal runs in reverse creation order; `RegisterInstance` objects are never disposed. | `DiContainer.Disposal.cs` | `Dispose_RunsInReverseCreationOrder`, `Dispose_NeverDisposesRegisteredInstances`; mutation 38 |
| Cycle detection, including cycles through `[Inject]` fields and aliases. | `DiContainer.Resolution.cs`, `ResolutionState.cs` | `CycleThroughInjectFields_IsDetected`, `AliasCycle_IsReportedAsACircularDependency`; mutation 47 |
| Scopes inherit the root options; options are copied, not shared. | `DiContainer.cs`, `DiContainerOptions.cs` | `Scopes_InheritTheRootOptions`, `Options_AreCopied_SoChangingThemAfterConstructionHasNoEffect`; mutations 52, 53 |
| `IServiceProvider` renamed `IDiProvider` (it clashed with `System.IServiceProvider`); `IScopeFactory` / `IDiScope` reshaped. | `IDiProvider.cs`, `IScopeFactory.cs`, `IDiScope.cs` | compiles against the tests |

## What else changed

`CHANGELOG.md` lists every behaviour change under Breaking, Added, Changed and Fixed. The README was rewritten to match,
and its code samples were compiled and run in the rig (the `[Resolve]` sample only compiled).

## How I checked it

- 158 tests in the rig, all passing.
- 82 mutation checks: each re-introduces one of the original flaws, or removes one guard, in a scratch copy of the
  runtime and runs the whole suite. All 82 make at least one test fail. Where one survived during the work, I added
  or tightened a test until it was caught.
- The runtime builds with no XML-documentation warnings.
- Mutation 79 (holder kept in the scene it was made in) and 82 (only the component destroyed on a failed injection)
  were added late. They guard a scene-placement bug and a leak that I found in my own first version of C9.
- One thing no test guards: the hidden holder carries only `HideFlags.HideInHierarchy`, with no `DontSave` flags,
  because I could not establish from Unity's documentation whether those flags change which scene an object belongs to.
  The rig cannot model that, so it cannot catch someone adding them back.

## Appendix: the mutation checks

Each row re-introduces one flaw, or removes one guard. The second column is the tag my notes used (a flaw number, or a short word where I did not record a number).

| # | Flaw tag | What the mutation does |
|---|---|---|
| 1 | C1 | inherited Transient is built by the registering ancestor |
| 2 | C1 | inherited Scoped is built/cached by the registering ancestor |
| 3 | C2 | ResolveAll builds scoped/transient entries with the registering scope, not the resolving one |
| 4 | C3 | Resolve works on a disposed container |
| 5 | C3 | TryResolve works on a disposed container |
| 6 | C3 | a disposed owner still serves its singletons to live children |
| 7 | C5 | no captive/scope validation: root resolves Scoped silently |
| 8 | C7 | TryResolve masks real failures as a miss |
| 9 | C8 | caches keyed by service type again and replaced bindings keep their cached instance |
| 10 | C6 | a scope no longer tracks the transient disposables it resolved |
| 11 | C6 | root never warns about transient disposables |
| 12 | M-dead | a destroyed cached Unity object is handed out again |
| 13 | M-miss | TryResolve does not count misses |
| 14 | M-miss | TryResolve miss warning is not once-per-type |
| 15 | M7 | Func<T>/Lazy<T> of an unregistered T no longer fail fast |
| 16 | M5 | the FIRST binding wins instead of the last |
| 17 | MainThread | Resolve does not assert the main thread |
| 18 | M4 | duplicate Register no longer throws |
| 19 | M5 | Add replaces instead of appending |
| 20 | M-dead | a destroyed registered instance can no longer be re-registered |
| 21 | M12 | UnityEngine.Object implementations are accepted for Register |
| 22 | M14 | non-assignable implementation is accepted |
| 23 | M12 | structs / open generics are accepted |
| 24 | C4 | a factory returning an already-injected cached object is injected a second time |
| 25 | C4 | cached instances are not marked as injected |
| 26 | M-inj | an instance whose injection failed is not disposed |
| 27 | M-opt | unregistered optional service parameter falls back silently |
| 28 | M11 | parameter defaults are ignored |
| 29 | M31 | TargetInvocationException is not unwrapped |
| 30 | C9 | prefab is instantiated straight into its destination: Awake runs before injection |
| 31 | C9 | prefab is never re-parented |
| 32 | C9 | inactive children of a hierarchy are skipped |
| 33 | C9 | a prefab clone whose injection failed is left alive (hierarchy) |
| 34 | C9 | a ScriptableObject clone whose injection failed is left alive |
| 35 | M-fac | a factory returning null is not diagnosed |
| 36 | C3 | InstantiatePrefab works on a disposed container |
| 37 | C4 | the same object is disposed once per registration that hands it out |
| 38 | order | disposal runs in creation order instead of reverse |
| 39 | M15 | IAsyncDisposable is ignored by DisposeAsync |
| 40 | M8 | Lazy<T> caches a failed resolve |
| 41 | M9 | two [Inject] constructors are no longer an error |
| 42 | M9 | several public constructors without [Inject] silently pick the first |
| 43 | M10 | a virtual [Inject] method runs once per override |
| 44 | M16 | non-component, non-interface [Resolve] fields are not reported |
| 45 | base | only the most-derived type is scanned for injection members |
| 46 | M16 | [Resolve(Optional)] misses are warned about and clear the field |
| 47 | cycle | cycle detection off (expected: stack overflow) |
| 48 | M31 | resolution path is no longer attached to exceptions |
| 49 | M30 | the library logs an error on every failed resolve |
| 50 | C5 | Validate() does not report captive dependencies |
| 51 | C5 | Validate() does not report missing dependencies |
| 52 | options | scopes do not inherit the root options |
| 53 | options | the options object is not copied (later changes leak into the container) |
| 54 | M33 | generic type names are printed raw (Func`1) |
| 55 | M15 | Dispose() ignores an IAsyncDisposable that finishes synchronously |
| 56 | holder | the root never releases the hidden prefab holder |
| 57 | M6 | ResolveAll(includeParents: false) still includes parents |
| 58 | M6 | ResolveAll lists the scope's own bindings before its ancestors' |
| 59 | alias | an alias to itself is accepted |
| 60 | alias | an alias that resolves to the wrong type is not diagnosed |
| 61 | M14 | RegisterInstance accepts null / destroyed objects |
| 62 | M14 | RegisterInstance accepts an object of the wrong type |
| 63 | dead | a destroyed registered instance is returned instead of diagnosed |
| 64 | M-null | InjectInto(null) is silently accepted |
| 65 | path | InjectInto pushes no frame (no path for field injection failures) |
| 66 | path | the parameter being resolved is not recorded in the path |
| 67 | M16 | [Resolve] fields are not filled by InjectInto |
| 68 | C9 | [Resolve(Parent)] ignores inactive parents (every parent is inactive under the prefab holder) |
| 69 | M31 | a throwing Dispose stops the remaining disposals |
| 70 | M6 | T[] of a value-type T is claimed to be resolvable |
| 71 | C3 | HasRegistration works on a disposed container |
| 72 | C3 | ResolveAll works on a disposed container |
| 73 | C3 | registering on a disposed container works |
| 74 | alias | aliases are resolved as separate objects (no forwarding) |
| 75 | M20 | Resolve formats a string on every call |
| 76 | M21 | Resolve allocates a closure on every call |
| 77 | C10 | [Inject] no longer implies [Preserve] |
| 78 | C10 | [Resolve] no longer implies [Preserve] |
| 79 | C9 | the hidden holder stays in the scene it was made in (parentless clones follow it there) |
| 80 | C9 | edit mode keeps the hidden holder in the scene after the call |
| 81 | C9 | edit mode releases the holder even while an outer clone is still under it |
| 82 | C9 | a failed injection destroys only the component, leaving the cloned GameObject behind |

# November.DI 2.0.0 — delivery and verification report

## Post-review repair status

The static review fixes were applied in phased commits after the initial delivery:

1. dependency validation regression tests and a scope-aware `Validate()`;
2. centralized ownership, cross-scope-safe disposal and idempotent `DisposeAsync()`;
3. prefab hierarchy cleanup and factory-result validation/cleanup;
4. registration hardening and transactional validation;
5. injection metadata validation, deterministic member order and epoch-scoped markers.

After those phases the .NET rig reports **186 passed, 0 failed**. This is still a
stub-based run: Unity Editor execution, real PlayMode behavior and IL2CPP builds
remain unverified, as described below.

## Integration status (target repository)

The delivery was integrated into the package repository at its root:

- `tools/rig` is checked in as `Tools~/rig`; Unity ignores folders ending in `~`, so the rig's `UnityEngine` stubs and NUnit shim are never compiled by the Unity Editor.
- Existing `.meta` files from the repository were preserved. The paths listed below as missing-original metadata are present here; `Runtime/IServiceProvider.cs` and its `.meta` were removed for the `IDiProvider` rename.
- The CI workflow stages the package under `.ci/package` before running GameCI package mode, because GameCI hangs when the package directory is the repository root.
- Integration fixes: `NotRegistered` now attaches the full path (including the missing type) to `exception.Data`; the two throwing-disposal tests declare their expected error logs to Unity's `LogAssert`; the rig stub gained `GameObject.GetComponentInChildren`.
- `dotnet run --project Tools~/rig/rig.csproj -p:RuntimeDir=../../Runtime -p:TestsDir=../../Tests` ran on .NET SDK 10.0.402: **186 passed, 0 failed** after the post-review repairs.
- Unity Editor, PlayMode under the real engine and IL2CPP builds were still not run; the rig compiles the runtime and all 186 source tests against stubs.

## Original delivery verification status

- **C# build / test rig:** not run. `dotnet` is not installed in this environment; the attempted `python3 tools/rig/mut.py M54` preflight exited with status 2 and printed: `The .NET 8 SDK is required to run the mutation suite (dotnet was not found).`
- **Unity Test Runner / IL2CPP:** not run. No Unity Editor or authenticated GameCI runner was available. The workflow explicitly notes this.
- **Mutation patches:** 74 definitions, 74 unique IDs; all 74 exact-edit sets matched their target text exactly once against the final Runtime source. This is an applicability dry run only—not a build, mutation execution, or KILLED result.
- **Test source:** 148 `[Test]` / `[UnityTest]` attributes exist (134 EditMode, 14 PlayMode). These are source counts, not executed pass counts.
- **New Unity metadata:** 17 `.meta` entries generated; 17 unique GUIDs; no duplicate GUIDs found.
- **Static checks:** Python syntax check for `mut.py`; JSON parse for package/asmdefs; XML parse for `link.xml`; YAML parse for CI workflow; embedded shell passes `bash -n`; stale-comment grep returned no matches. These checks do not replace C# compilation or Unity.

## Audit traceability (C1–C10 and M1–M40)

| ID | Code change | Test / evidence | Docs | Status |
|---|---|---|---|---|
| C1 | Runtime/DiContainer.Resolution.cs — inherited Transient/Scoped built by resolving scope | DiContainerLifetimeTests.InheritedTransient_IsBuiltByTheResolvingScope_NotByTheAncestor; InheritedTransientDisposable_IsReleasedWithTheScope_NotKeptByTheRoot | README §Container behavior > Lifetimes and ownership; CHANGELOG lines 29–35 | implemented; execution unverified |
| C2 | Runtime/DiContainer.Resolution.cs — descriptor-based resolving-scope cache; ResolveAll/collection resolver | DiContainerLifetimeTests.ResolveAll_BuildsScopedBindingsInTheResolvingScope; CollectionParameters_ReceiveEveryBinding_InRegistrationOrder | README §Container behavior > Registration and resolution; CHANGELOG lines 32–35 | implemented; execution unverified |
| C3 | DiContainer.Resolution/Injection/Registration/Disposal.cs — disposed guards; owner-disposed singleton diagnosis | DiContainerLifetimeTests.AfterDispose_EveryEntryPoint_ThrowsObjectDisposedException; ChildOfADisposedParent_GetsAnAccurateError_ForAnInheritedSingleton | README §Scopes and disposal; CHANGELOG lines 56–62 | implemented; execution unverified |
| C4 | DiContainer.Registration.cs + ServiceDescriptor.cs — RegisterAlias and shared identity tracking | DiContainerRegistrationTests.Alias_ForwardsToTheSameInstance_InjectedOnce_DisposedOnce; ForwardingFactory_DoesNotInjectOrDisposeTheSameObjectTwice | README registration aliases; CHANGELOG lines 70–72, 113–115 | implemented; execution unverified |
| C5 | DiContainerOptions.cs + DiContainer.Validation.cs + Diagnostics.cs — ValidateScopes and Validate() | DiContainerLifetimeTests.ScopedResolvedFromTheRoot_Throws_WhenScopeValidationIsOn; SingletonCapturingAScopedService_IsReportedAsACaptiveDependency; DiContainerInjectionTests.Validate_FlagsASingletonThatCapturesAScopedService | README §Validation and §Options; CHANGELOG lines 40–44, 77–80 | implemented; execution unverified |
| C6 | DiContainer.Resolution.cs + Diagnostics.cs + Disposal.cs — transient disposable ownership and root warning | DiContainerLifetimeTests.InheritedTransientDisposable_IsReleasedWithTheScope_NotKeptByTheRoot; RootTransientDisposable_WarnsOncePerType_WithTheScopeAdvice | README §Scopes and disposal; CHANGELOG lines 46–50 | implemented; execution unverified |
| C7 | DiContainer.Resolution.cs — TryResolve false only for genuine misses | DiContainerTests.TryResolve_CircularDependency_Rethrows_InsteadOfMaskingAsMiss; TryResolve_SingletonConstructionFailure_Rethrows_InsteadOfMaskingAsMiss; DiContainerRegistrationTests.TryResolve_OnADisposedContainer_Throws_EvenForAnUnregisteredType | README §Container behavior > TryResolve; CHANGELOG lines 47–49, 56–58 | implemented; execution unverified |
| C8 | DiContainer.Resolution.cs — caches keyed by ServiceDescriptor; override/Replace invalidates old caches | DiContainerLifetimeTests.LocalScopedOverride_AfterAnInheritedScopedResolve_IsNotShadowedByTheCache; Replace_DropsTheCachedSingletonOfTheOldBinding | README §Container behavior > Lifetimes and ownership; CHANGELOG lines 136–138 | implemented; execution unverified |
| C9 | DiContainer.Injection.cs + ResolutionState.cs — clone under inactive holder, inject, then re-parent/release on teardown | PrefabInjectionTests.InstantiatePrefab_InjectsTheWholeHierarchy_BeforeAwakeRuns; DisposingRoot_ReleasesTheHiddenPrefabHolder; failure cleanup tests | README §Prefabs and hierarchies; CHANGELOG lines 95–107 | rig coverage only; real Unity prefab lifecycle still required |
| C10 | Runtime/link.xml + [Preserve] annotations in reflection entry points; optional high-stripping IL2CPP job in .github/workflows/ci.yml | No real IL2CPP execution; source coverage and optional smoke build are not a substitute | README §IL2CPP and code stripping; CHANGELOG lines 87–88 | partial; Unity IL2CPP/device build not run |
| M1 | Runtime/IDiScope.cs — IDiScope now includes IScopeFactory | DiContainerRegistrationTests.Scopes_AreCreatedThroughTheSegregatedInterfaces | README §Scopes and disposal; CHANGELOG lines 18–20 | implemented; compile/test unverified |
| M2 | Runtime/IDiProvider.cs — renamed IDiProvider to avoid System.IServiceProvider ambiguity | Factory/provider registration tests compile against IDiProvider; no dedicated ambiguity test | CHANGELOG lines 15–17; README namespace/API examples | implemented; compile unverified |
| M3 | Runtime/IScopeFactory.cs — no longer inherits IDisposable | DiContainerRegistrationTests.Scopes_AreCreatedThroughTheSegregatedInterfaces | README §Scopes and disposal; CHANGELOG lines 18–20 | implemented; compile/test unverified |
| M4 | DiContainer.Registration.cs — duplicate Register throws; Replace* for deliberate replacement | DiContainerRegistrationTests.Register_Twice_Throws_AndKeepsTheFirstBinding; Replace_SwapsTheBinding; LegacyPolicy_ReplacesWithAWarning | README §Registration and resolution; CHANGELOG lines 21–28 | implemented; execution unverified |
| M5 | DiContainer.Registration.cs — Add appends; Resolve takes last binding, ResolveAll returns all | DiContainerRegistrationTests.Add_AppendsBindings_ResolveReturnsTheLast_ResolveAllReturnsAll; collection ordering tests | README §Container behavior > Registration and resolution; CHANGELOG lines 81–87 | implemented; execution unverified |
| M6 | DiContainer.Registration.cs + ServiceRegistryExtensions.cs — non-generic overloads, shorthand, HasRegistration, collection injection | DiContainerRegistrationTests.NonGenericInstanceAndFactoryOverloads_Register; SelfBinding_AndLifetimeShorthands_Register; HasRegistration_GenericOverload_Works; CollectionsOfValueTypes_AreNotImplicitlyResolvable | README API and collection injection sections; CHANGELOG lines 72–87 | partial: keyed bindings and open-generic registrations remain unsupported |
| M7 | DiContainer.Resolution.cs + Factories.cs — implicit Func/Lazy/collection resolution and HasRegistration | DiContainerRegistrationTests.FuncAndLazy_OfAnUnregisteredService_AreNotResolvable; FuncAndLazy_OfARegisteredService_AreResolvable; OptionalParameter_OfAResolvableFuncType_IsResolved_NotDefaulted | README §Registration and resolution; CHANGELOG lines 86–87 | implemented; execution unverified |
| M8 | DiContainer.Factories.cs — Lazy wrapper retries after failed Value | DiContainerInjectionTests.Lazy_RetriesAfterAFailedValue | README §Registration and resolution; CHANGELOG line 108 | implemented; execution unverified |
| M9 | InjectionMetadata.cs — explicit constructor ambiguity detection | DiContainerInjectionTests.TwoInjectConstructors_AreRejected; InjectMarkedConstructor_IsChosen_EvenWithSeveralPublicOnes | README §Injection; CHANGELOG lines 65–66 | implemented; execution unverified |
| M10 | InjectionMetadata.cs — virtual Inject methods de-duplicated by base definition | DiContainerInjectionTests.OverriddenInjectMethod_RunsOnce | README §Injection; CHANGELOG line 99 | implemented; execution unverified |
| M11 | InjectionMetadata.cs + DiContainer.Injection.cs — method parameters honor defaults | DiContainerInjectionTests.InjectMethod_HonoursParameterDefaults_LikeConstructors | README §Injection; CHANGELOG lines 100–102 | implemented; execution unverified |
| M12 | DiContainer.Registration.cs + DiContainer.Injection.cs — reject value types/Unity Object implementations; ScriptableObject prefab path | DiContainerRegistrationTests.Register_StructImplementation_Throws; Register_ScriptableObjectImplementation_Throws; PrefabInjectionTests.InstantiatePrefab_OfAScriptableObject_ClonesAndInjectsTheClone | README §Prefabs and hierarchies; CHANGELOG lines 86–87 | Unity-specific ScriptableObject behavior still needs real engine verification |
| M13 | DiContainer.cs + MainThread.cs — explicit constructor retained; internal main-thread bookkeeping, CreateOnMainThread | DiContainerRegistrationTests.Constructor_FromABackgroundThread_Throws_InEditorAndDevelopmentBuilds; CreateOnMainThread_ReturnsAWorkingContainer | README §Threading; CHANGELOG lines 63–64 | implemented; actual Unity domain initialization still needs Unity |
| M14 | ServiceDescriptor.cs — explicit Type/Factory/Instance/Alias descriptor kinds | DiContainerRegistrationTests.NonGenericInstanceAndFactoryOverloads_Register; Alias_ForwardsToTheSameInstance_InjectedOnce_DisposedOnce | README §Registration and resolution; CHANGELOG lines 70–85 | implemented; execution unverified |
| M15 | DiContainer.Disposal.cs — IDisposable and IAsyncDisposable ownership / DisposeAsync | DiContainerInjectionTests.Dispose_AcceptsAnAsyncDisposable_ThatCompletesSynchronously; DisposeAsync_PrefersIAsyncDisposable_AndStillDisposesPlainDisposables | README §Scopes and disposal; CHANGELOG line 94 | implemented; execution unverified |
| M16 | ResolveAttribute.cs + ComponentResolver.cs — self/children/parent, inactive search, Optional and required-miss clearing | ComponentResolverSourceTests.Children_FindsAComponentOnAChild; Parent_FindsAComponentOnAParent; Children_IncludesInactiveChildren; Optional_MissingComponent_IsSilent_AndKeepsTheAssignedValue; required miss test | README §[Resolve]; CHANGELOG lines 117–122 | implemented; execution unverified |
| M17 | MainThread.cs — Editor InitializeOnLoadMethod plus SubsystemRegistration recapture | DiContainerRegistrationTests.RegistrationAndDiagnosticEntryPoints_RejectABackgroundThread; main-thread capture tests | README §Threading; CHANGELOG lines 116–118 | code present; Editor domain-load callback needs Unity verification |
| M18 | DiContainer.Injection.cs — null/missing-script components are skipped with a warning | No reliable Unity missing-script test in the .NET stub | README §Prefabs and hierarchies; CHANGELOG lines 117–119 | partial; verify actual GetComponentsInChildren null entries in Unity |
| M19 | MainThread.cs + InjectionMetadata.cs + ResolutionState.cs — SubsystemRegistration resets static caches/state | ClearingTheMetadataCache_DoesNotBreakResolution is a cache sanity test; no domain-reload-disabled player test | README §Threading; CHANGELOG lines 116–118 | partial; Enter Play Mode Options behavior needs Unity |
| M20 | MainThread.Assert(Type) conditional formatting; Resolution.cs hot path avoids success-path message allocation | DiContainerInjectionTests.Resolve_OfACachedSingleton_DoesNotAllocate | README §Threading and tests; CHANGELOG lines 123–125 | code/test source present; allocation test unexecuted and runtime-specific |
| M21 | DiContainer.Resolution.cs — separate wrapper path avoids cached Resolve closure allocation | DiContainerInjectionTests.Resolve_OfACachedSingleton_DoesNotAllocate | README §Tests; CHANGELOG lines 123–125 | code/test source present; allocation test unexecuted |
| M22 | InjectionMetadata.cs — lazy TypePlan caches constructor choice and ParameterInfo array | DiContainerInjectionTests.Construction_RunsConstructor_ThenFields_ThenMethods; optional-parameter tests; allocation sanity test | README §Injection; CHANGELOG lines 123–125 | implementation present; no standalone transient allocation benchmark run |
| M23 | InjectionMetadata.cs — InjectMethodPlan caches parameters; no LINQ/array rebuild per invocation | DiContainerInjectionTests.InjectMethod_HonoursParameterDefaults_LikeConstructors; Construction_RunsConstructor_ThenFields_ThenMethods | README §Injection; CHANGELOG lines 109–112 | implemented; execution unverified |
| M24 | DiContainer.Factories.cs — generic wrappers avoid MethodInfo.Invoke/object[] on calls | DiContainerTests.Resolve_FuncGeneric_ProducesNewInstancePerInvocation; Resolve_LazyGeneric_DefersConstructionUntilValue | README §Registration and resolution; CHANGELOG lines 86–87 | implemented; execution unverified |
| M25 | DiContainer.Resolution.cs — collection arrays created without Expression.Compile | DiContainerLifetimeTests.CollectionParameters_ReceiveEveryBinding_InRegistrationOrder; collection-type registration tests | README §Registration and resolution; CHANGELOG lines 86–87 | implemented; execution unverified |
| M26 | InjectionMetadata.cs — lazy per-type plans; hierarchy walk stops at System/UnityEngine | InjectionQuery_ReportsInjectionPoints and metadata cache test; no large-assembly benchmark | README §Injection / §Tests; CHANGELOG lines 123–125 | partial; startup priming cost requires real Unity project measurement |
| M27 | DiContainer.cs + ResolutionState.cs — cached root/state and fewer per-scope allocations | DiContainerLifetimeTests inherited scope and lifetime tests | README §Scopes and disposal; CHANGELOG lines 113–125 | code review only; no direct allocation assertion |
| M28 | Runtime dictionaries/caches are non-concurrent; documented main-thread-only contract | DiContainerRegistrationTests.RegistrationAndDiagnosticEntryPoints_RejectABackgroundThread | README §Threading; CHANGELOG lines 116–118 | implemented; execution unverified |
| M29 | MainThread.Assert methods are Conditional for Editor/development checks | DiContainerRegistrationTests.RegistrationAndDiagnosticEntryPoints_RejectABackgroundThread; EveryResolvingEntryPoint_RejectsABackgroundThread | README §Threading; CHANGELOG lines 126–128 | implemented; release conditional compilation not run |
| M30 | DiContainer.Diagnostics.cs + Resolution.cs + Injection.cs — one thrown exception with ResolutionPathKey, no log-on-resolve | DiContainerInjectionTests.MissingRegistration_TwoLevelsDown_ThrowsOnce_WithTheFullPath_AndLogsNothing | README §Injection diagnostics; CHANGELOG lines 61–64 | implemented; execution unverified |
| M31 | DiContainer.Injection.cs — constructor and Inject-method TargetInvocationException unwrapped | DiContainerInjectionTests.ConstructorExceptions_AreNotWrappedInTargetInvocationException; Inject-method failure/path test | README §Injection; CHANGELOG lines 61–64 | implemented; execution unverified |
| M32 | DiContainer.Validation.cs — Validate() and DescribeRegistrations() | DiContainerInjectionTests.Validate_ReportsEveryProblem_InOneException; Validate_PassesForAHealthyGraph_IncludingLazyEdges; DescribeRegistrations_ListsEveryScopeLevel | README §Validation; CHANGELOG lines 79–80 | implemented; execution unverified |
| M33 | InjectionMetadata.cs — malformed XML docs corrected | No executable test; source XML-doc inspection | README public API descriptions; CHANGELOG line 122 | source/documentation review only |
| M34 | Runtime comments/docs — stale ILogService/Game.Gameplay/AotGenericHints/old-god/SOLID comments removed | Static grep of package sources found none of the named stale terms | README and CHANGELOG documentation reviewed | source inspection only |
| M35 | DiContainer.Registration.cs + Resolution.cs — redundant instance checks and unreachable external-instance paths removed | Registration tests for duplicate instance registration, destroyed instance eviction and type validation | README §Registration and resolution; CHANGELOG lines 83–85, 136–143 | implemented; no dedicated dead-code test |
| M36 | README.md — scopes/ownership, duplicates/Replace, ResolveAll, injection order/required fields, Resolve clearing, field-target properties documented | Relevant behavioral tests: lifetime, duplicate registration, ResolveAll, injection-order and auto-property tests | README §§Registration and resolution, Injection, [Resolve], Scopes and disposal; CHANGELOG lines 131–132 | documentation updated |
| M37 | Tests/EditMode and Tests/PlayMode — expanded coverage for listed gaps | Coverage includes prefab/hierarchy, destroyed instances, ctor selection, optional parameters, collections, dispose, scopes, captive dependencies and TryResolve thread/disposal | README §Tests; CHANGELOG lines 100–101 | test sources present; execution unverified |
| M38 | ResolutionState.cs — TryResolve miss counts/warnings are per root, not process-wide static | DiContainerRegistrationTests.TryResolve_Miss_WarnsOncePerTypePerContainer_ButCountsEveryMiss; TryResolve_Warning_IsNotSharedAcrossContainers | README §Registration and resolution; CHANGELOG lines 65–68, 129–130 | implemented; execution unverified |
| M39 | /.github/workflows/ci.yml — secret-free .NET rig job; gated GameCI EditMode/PlayMode package tests; manual IL2CPP player build with a headless smoke run | CI workflow itself; not executed | README §Tests; CHANGELOG lines 93–97 | workflow written; not run; GitHub Unity secrets and `UNITY_CI_ENABLED` required |
| M40 | CHANGELOG.md + package.json — v2.0.0 and changelogUrl; MIT license added | Package manifest parse/static check; no automated test | README installation/upgrade; LICENSE; CHANGELOG.md; package.json | MIT `LICENSE` and `license` field added; tag not created |

## Needs Unity verification

1. **C9:** confirm `Awake` / `OnEnable` order on a real prefab asset, including nested inactive children. The PlayMode source tests are not an executed Unity result.
2. **C10 / IL2CPP:** high-stripping player build on a device or IL2CPP target; verify `link.xml`, `[Preserve]`, `Func<T>` / `Lazy<T>` wrapper reachability, and private constructor/field/method reflection.
3. **M12:** real Unity `ScriptableObject` registration rejection, cloning, and injected clone behavior.
4. **M17 / M19:** Editor domain-load main-thread recapture and static resets with Enter Play Mode Options / domain reload disabled.
5. **M18:** confirm whether Unity returns null component entries for missing scripts and validate the skip/warning path.
6. **M26:** measure lazy metadata priming cost in a project with a large real assembly set.
7. **Performance:** repeat allocation measurements on Unity Mono and IL2CPP; the zero-allocation test source is present, but no runtime measurement was made here.

## Metadata and repository inputs still missing

- The bundles omitted existing `.meta` files. 22 original Runtime/Tests asset or folder metadata paths remain absent; their GUIDs must be restored from the original repository or supplied by the owner. Do not replace them with newly generated GUIDs.
  - `Runtime/ComponentResolver.cs.meta`
  - `Runtime/DiContainer.Disposal.cs.meta`
  - `Runtime/DiContainer.Factories.cs.meta`
  - `Runtime/DiContainer.Injection.cs.meta`
  - `Runtime/DiContainer.Registration.cs.meta`
  - `Runtime/DiContainer.Resolution.cs.meta`
  - `Runtime/DiContainer.cs.meta`
  - `Runtime/IDiScope.cs.meta`
  - `Runtime/IInjector.cs.meta`
  - `Runtime/IScopeFactory.cs.meta`
  - `Runtime/IServiceRegistry.cs.meta`
  - `Runtime/IServiceResolver.cs.meta`
  - `Runtime/InjectAttribute.cs.meta`
  - `Runtime/InjectionMetadata.cs.meta`
  - `Runtime/Lifetime.cs.meta`
  - `Runtime/November.DI.asmdef.meta`
  - `Runtime/ResolveAttribute.cs.meta`
  - `Runtime/ServiceDescriptor.cs.meta`
  - `Tests/EditMode.meta`
  - `Tests/EditMode/ComponentResolverTests.cs.meta`
  - `Tests/EditMode/DiContainerTests.cs.meta`
  - `Tests/EditMode/November.DI.Tests.asmdef.meta`
- `Runtime/IServiceProvider.cs` and its metadata are removed by the rename to `IDiProvider`; the old file should not be restored.
- The repository `.gitignore` was not in the supplied bundle and was not invented.
- GitHub clone was unavailable due to DNS/network resolution failure. The GitHub integration suggestion was surfaced, but no connection was confirmed.

## New metadata GUIDs

| Path | GUID |
|---|---|
| `Runtime/AssemblyInfo.cs.meta` | `092cb40499484a598ddff0eff1ca3db5` |
| `Runtime/DiContainer.Diagnostics.cs.meta` | `3f5826676b4047ad8a961faa990513e1` |
| `Runtime/DiContainer.Validation.cs.meta` | `3bcfadfd909a48c19743aaac01555e98` |
| `Runtime/DiContainerOptions.cs.meta` | `585989a618904300a253afda25918186` |
| `Runtime/IDiProvider.cs.meta` | `b6ab51160ad047e8ad752fc73aead255` |
| `Runtime/MainThread.cs.meta` | `2fdb4ed4f4c64251a174912f6bc1ec44` |
| `Runtime/ResolutionState.cs.meta` | `a90fc04f7f2a45a1a3621a1b7654b93c` |
| `Runtime/ServiceRegistryExtensions.cs.meta` | `5679113f82294aa283bb871fbf465e92` |
| `Runtime/link.xml.meta` | `e04f8583feff4ef3baf28a1ab036a465` |
| `Tests/EditMode/ComponentResolverSourceTests.cs.meta` | `2f10850bb460441bb29040f4d5f8406c` |
| `Tests/EditMode/DiContainerInjectionTests.cs.meta` | `8acb1b605c3f4a29af0c573a41d2e381` |
| `Tests/EditMode/DiContainerLifetimeTests.cs.meta` | `c2fe91f566dd404ab183b17c51cad7ed` |
| `Tests/EditMode/DiContainerRegistrationTests.cs.meta` | `33717ac45d61480d90d2f54ecdc0ed63` |
| `Tests/EditMode/TestSupport.cs.meta` | `945a9ecefb4e45ca929fbcfa07e12c37` |
| `Tests/PlayMode/November.DI.Tests.PlayMode.asmdef.meta` | `ad47ac615cb74ba0a37d64f11ee45982` |
| `Tests/PlayMode/PrefabInjectionTests.cs.meta` | `42a1ada061cd4bc3a8e13753137592db` |
| `Tests/PlayMode.meta` | `3451e81300f9401896b6008796a1ca01` |

## Owner decisions / follow-up required

- **License:** MIT. The repository includes `LICENSE` and `package.json` declares `"license": "MIT"`.
- **Version tag:** decide whether to publish/tag `v2.0.0` (no tag was created).
- **Unity metadata:** provide the original `.meta` files (or allow a connected GitHub repository read) to restore existing GUIDs, plus the original `.gitignore`.
- **CI:** the .NET rig job runs without secrets. For the Unity jobs, configure `UNITY_LICENSE`, `UNITY_EMAIL`, and `UNITY_PASSWORD`, then set the repository variable `UNITY_CI_ENABLED=true`; the manual IL2CPP job also needs `run_il2cpp` on a workflow dispatch. Nothing has been run on GitHub Actions.
- **M6 scope:** keyed registrations and open-generic registrations remain unsupported; decide whether to add them or explicitly keep them out of the 2.0.0 scope.
- **Optional package content:** `Samples~/` and `Documentation~/` were not added.

## Files and commands

- `FILE_INDEX.csv` indexes the package, CI, and rig files with status, line count, and SHA-256.
- `November-2.0.0-full-changed-files.txt` contains all new/changed files in full; unchanged-original files remain in the ZIP.
- Mutation applicability check: imported the 74 definitions from `Tools~/rig/mut.py`, then checked each edit tuple against the final Runtime source and required every `old` snippet count to equal its `expected_count`. Result: **74/74 applicable**.

#!/usr/bin/env python3
"""Mutation checks for the November.DI rewrite.

Each mutation re-introduces one of the original flaws (or removes one guard) in a scratch copy of the runtime,
builds the rig against it, and runs the whole suite. A mutation is KILLED when at least one test fails (or the
process dies); it SURVIVES when everything still passes, which means the fix has no test guarding it.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parents[2]
# The package lives at the repository root in this repository.
PACKAGE = ROOT
SRC = PACKAGE / 'Runtime'
TESTS = PACKAGE / 'Tests'
RIG = ROOT / 'Tools~' / 'rig' / 'rig.csproj'
OUT = Path(os.environ.get('NOVEMBER_MUTATION_OUT', Path(tempfile.gettempdir()) / 'november-di-mutations'))


def L(*lines):
    return '\n'.join(lines)


# (id, flaw, description, [(file, old, new, expected_count)])
M = []


def mut(mid, flaw, desc, *edits):
    M.append((mid, flaw, desc, list(edits)))


# ── Lifetimes and ownership (C1, C2, C3, C5, C6, C8) ───────────────────────────────────────────────────────────
mut('M01', 'C1', 'inherited Transient is built by the registering ancestor',
    ('DiContainer.Resolution.cs', 'return CreateTransient(descriptor);', 'return owner.CreateTransient(descriptor);', 1))

mut('M02', 'C1', 'inherited Scoped is built/cached by the registering ancestor',
    ('DiContainer.Resolution.cs',
     L('                if (_parent == null && _options.ValidateScopes) throw ScopedFromRoot(descriptor);',
       '                return GetOrCreateCached(descriptor);'),
     L('                if (_parent == null && _options.ValidateScopes) throw ScopedFromRoot(descriptor);',
       '                return owner.GetOrCreateCached(descriptor);'), 1))

mut('M03', 'C2', 'ResolveAll builds scoped/transient entries with the registering scope, not the resolving one',
    ('DiContainer.Resolution.cs',
     'target.SetValue(resolver.ResolveDescriptor(bindings[i], this), index++);',
     'target.SetValue(ResolveDescriptor(bindings[i], this), index++);', 1))

mut('M04', 'C3', 'Resolve works on a disposed container',
    ('DiContainer.Resolution.cs',
     L('            if (type == null) throw new ArgumentNullException(nameof(type));',
       '            ThrowIfDisposed();',
       '            MainThread.Assert(type);',
       '            return ResolveInternal(type);'),
     L('            if (type == null) throw new ArgumentNullException(nameof(type));',
       '            MainThread.Assert(type);',
       '            return ResolveInternal(type);'), 1))

mut('M05', 'C3', 'TryResolve works on a disposed container',
    ('DiContainer.Resolution.cs',
     L('        public bool TryResolve<TService>(out TService result)',
       '        {',
       '            ThrowIfDisposed();'),
     L('        public bool TryResolve<TService>(out TService result)',
       '        {'), 1))

mut('M06', 'C3', 'a disposed owner still serves its singletons to live children',
    ('DiContainer.Resolution.cs', 'if (owner._disposed) throw OwnerDisposed(descriptor);', '', 1))

mut('M07', 'C5', 'no captive/scope validation: root resolves Scoped silently',
    ('DiContainer.Resolution.cs',
     'if (_parent == null && _options.ValidateScopes) throw ScopedFromRoot(descriptor);', '', 1))

mut('M08', 'C7', 'TryResolve masks real failures as a miss',
    ('DiContainer.Resolution.cs',
     L('            result = (TService)ResolveInternal(type);',
       '            return true;'),
     L('            try { result = (TService)ResolveInternal(type); return true; }',
       '            catch (InvalidOperationException) { result = default; return false; }'), 1))

mut('M09', 'C8', 'caches keyed by service type again and replaced bindings keep their cached instance',
    ('DiContainer.Resolution.cs',
     'if (_instances != null && _instances.TryGetValue(descriptor, out var existing))',
     'if (_instances != null && TryGetByServiceType(descriptor, out var existing))', 1),
    ('DiContainer.Resolution.cs',
     L('        private object CreateTransient(ServiceDescriptor descriptor)',
       '        {'),
     L('        private bool TryGetByServiceType(ServiceDescriptor d, out object value)',
       '        {',
       '            foreach (var kv in _instances) if (kv.Key.ServiceType == d.ServiceType) { value = kv.Value; return true; }',
       '            value = null;',
       '            return false;',
       '        }',
       '',
       '        private object CreateTransient(ServiceDescriptor descriptor)',
       '        {'), 1),
    ('DiContainer.Registration.cs',
     L('            for (int i = 0; i < list.Count; i++)',
       '                if (_instances != null) _instances.Remove(list[i]);', ''),
     '', 1))

mut('M10', 'C6', 'a scope no longer tracks the transient disposables it resolved',
    ('DiContainer.Resolution.cs',
     L('                WarnIfRootTransientDisposable(instance);', '', '            Track(instance);', '            return instance;'),
     L('                WarnIfRootTransientDisposable(instance);', '', '            return instance;'), 1))

mut('M11', 'C6', 'root never warns about transient disposables',
    ('DiContainer.Resolution.cs',
     L('            if (_parent == null && _options.WarnOnRootTransientDisposables)',
       '                WarnIfRootTransientDisposable(instance);', ''),
     '', 1))

mut('M12', 'M-dead', 'a destroyed cached Unity object is handed out again',
    ('DiContainer.Resolution.cs',
     'if (!IsDestroyedUnityObject(existing)) return existing;', 'return existing;', 1))

mut('M13', 'M-miss', 'TryResolve does not count misses',
    ('DiContainer.Resolution.cs', '_state.MissCount++;', '', 1))

mut('M14', 'M-miss', 'TryResolve miss warning is not once-per-type',
    ('DiContainer.Resolution.cs',
     '_options.WarnOnTryResolveMiss && _state.FirstMiss(type)', '_options.WarnOnTryResolveMiss', 1))

mut('M15', 'M7', 'Func<T>/Lazy<T> of an unregistered T no longer fail fast',
    ('DiContainer.Resolution.cs',
     'if (!CanResolve(argument)) throw NotRegistered(argument, wrapper);', '', 1))

mut('M16', 'M5', 'the FIRST binding wins instead of the last',
    ('DiContainer.Resolution.cs', 'descriptor = list[list.Count - 1];', 'descriptor = list[0];', 1))

mut('M17', 'MainThread', 'Resolve does not assert the main thread',
    ('DiContainer.Resolution.cs',
     L('            ThrowIfDisposed();', '            MainThread.Assert(type);', '            return ResolveInternal(type);'),
     L('            ThrowIfDisposed();', '            return ResolveInternal(type);'), 1))

# ── Registration (M4, M5, M12, M14) ────────────────────────────────────────────────────────────────────────────
mut('M18', 'M4', 'duplicate Register no longer throws',
    ('DiContainer.Registration.cs', 'if (!legacyReplace) throw Duplicate(serviceType);', '', 1))

mut('M19', 'M5', 'Add replaces instead of appending',
    ('DiContainer.Registration.cs', ' || mode == RegistrationMode.Append)', ')', 1))

mut('M20', 'M-dead', 'a destroyed registered instance can no longer be re-registered',
    ('DiContainer.Registration.cs',
     'bool replacesDeadInstance = list.Count == 1 && list[0].IsDestroyedInstance;',
     'bool replacesDeadInstance = false;', 1))

mut('M21', 'M12', 'UnityEngine.Object implementations are accepted for Register',
    ('DiContainer.Registration.cs', '            AssertNotUnityObject(implType);\n', '', 1))

mut('M22', 'M14', 'non-assignable implementation is accepted',
    ('DiContainer.Registration.cs', 'if (!serviceType.IsAssignableFrom(implType))', 'if (false)', 1))

mut('M23', 'M12', 'structs / open generics are accepted',
    ('DiContainer.Registration.cs',
     'if (implType.IsValueType || implType.ContainsGenericParameters || serviceType.ContainsGenericParameters)',
     'if (false)', 1))

# ── Injection (C4, C9, M8-M11, M30, M31) ───────────────────────────────────────────────────────────────────────
mut('M24', 'C4', 'a factory returning an already-injected cached object is injected a second time',
    ('DiContainer.Injection.cs',
     'bool alreadyInjected = descriptor.Kind == DescriptorKind.Factory && s_injected.TryGetValue(instance, out _);',
     'bool alreadyInjected = false;', 1))

mut('M25', 'C4', 'cached instances are not marked as injected',
    ('DiContainer.Resolution.cs',
     L('            MarkInjected(instance);', '            Track(instance);', '            return instance;'),
     L('            Track(instance);', '            return instance;'), 1))

mut('M26', 'M-inj', 'an instance whose injection failed is not disposed',
    ('DiContainer.Injection.cs',
     L('                        DisposeQuietly(instance);', '                        throw;'),
     '                        throw;', 1))

mut('M27', 'M-opt', 'unregistered optional service parameter falls back silently',
    ('DiContainer.Injection.cs',
     'if (type.IsInterface || type.IsAbstract) WarnOptionalFallback(parameter);', '', 1))

mut('M28', 'M11', 'parameter defaults are ignored',
    ('DiContainer.Injection.cs',
     'if (parameter.HasDefaultValue && !CanResolve(type))',
     'if (false && parameter.HasDefaultValue && !CanResolve(type))', 1))

mut('M29', 'M31', 'TargetInvocationException is not unwrapped',
    ('DiContainer.Injection.cs',
     'catch (TargetInvocationException ex) { throw Unwrap(ex); }',
     'catch (TargetInvocationException) { throw; }', 2))

mut('M30', 'C9', 'prefab is instantiated straight into its destination: Awake runs before injection',
    ('DiContainer.Injection.cs',
     'T instance = Object.Instantiate(prefab, holder.transform);',
     'T instance = Object.Instantiate(prefab, parent);', 1))

mut('M31', 'C9', 'prefab is never re-parented',
    ('DiContainer.Injection.cs', '            root.transform.SetParent(parent, false);\n', '', 1))

mut('M32', 'C9', 'inactive children of a hierarchy are skipped',
    ('DiContainer.Injection.cs', 'root.GetComponentsInChildren(true, buffer);', 'root.GetComponentsInChildren(false, buffer);', 1))

mut('M33', 'C9', 'a prefab clone whose injection failed is left alive (hierarchy)',
    ('DiContainer.Injection.cs',
     L('                DestroyObject(instance);', '                throw;'),
     '                throw;', 1))

mut('M34', 'C9', 'a ScriptableObject clone whose injection failed is left alive',
    ('DiContainer.Injection.cs',
     L('                    DestroyObject(asset);', '                    throw;'),
     '                    throw;', 1))

mut('M35', 'M-fac', 'a factory returning null is not diagnosed',
    ('DiContainer.Injection.cs', 'if (result == null || IsDestroyedUnityObject(result))', 'if (false)', 1))

mut('M36', 'C3', 'InstantiatePrefab works on a disposed container',
    ('DiContainer.Injection.cs',
     L('        public T InstantiatePrefab<T>(T prefab, Transform parent = null) where T : Object',
       '        {',
       '            ThrowIfDisposed();',
       '            if (prefab == null) throw new ArgumentNullException(nameof(prefab));',
       '            MainThread.Assert(nameof(InstantiatePrefab));'),
     L('        public T InstantiatePrefab<T>(T prefab, Transform parent = null) where T : Object',
       '        {',
       '            if (prefab == null) throw new ArgumentNullException(nameof(prefab));',
       '            MainThread.Assert(nameof(InstantiatePrefab));'), 1))

# ── Disposal (C4, M15) ─────────────────────────────────────────────────────────────────────────────────────────
mut('M37', 'C4', 'the same object is disposed once per registration that hands it out',
    ('DiContainer.Disposal.cs', 'if (!_tracked.Add(instance)) return;', '_tracked.Add(instance);', 1))

mut('M38', 'order', 'disposal runs in creation order instead of reverse',
    ('DiContainer.Disposal.cs', 'for (int i = items.Count - 1; i >= 0; i--)', 'for (int i = 0; i < items.Count; i++)', 2))

mut('M39', 'M15', 'IAsyncDisposable is ignored by DisposeAsync',
    ('DiContainer.Disposal.cs',
     'if (item is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();',
     'if (false) await Task.CompletedTask;', 1))

# ── Lazy / metadata (M8, M9, M10, M12) ─────────────────────────────────────────────────────────────────────────
mut('M40', 'M8', 'Lazy<T> caches a failed resolve',
    ('DiContainer.Factories.cs', 'LazyThreadSafetyMode.PublicationOnly', 'LazyThreadSafetyMode.ExecutionAndPublication', 1))

mut('M41', 'M9', 'two [Inject] constructors are no longer an error',
    ('InjectionMetadata.cs', 'if (markedCount > 1)', 'if (markedCount > 1 && false)', 1))

mut('M42', 'M9', 'several public constructors without [Inject] silently pick the first',
    ('InjectionMetadata.cs',
     'var chosen = markedCount == 1 ? marked : (publicCount == 1 ? onlyPublic : null);',
     'var chosen = markedCount == 1 ? marked : (publicCount >= 1 ? onlyPublic : null);', 1))

mut('M43', 'M10', 'a virtual [Inject] method runs once per override',
    ('InjectionMetadata.cs',
     '(seenDefinitions ?? (seenDefinitions = new HashSet<MethodInfo>())).Add(method.GetBaseDefinition())', 'true', 1))

mut('M44', 'M16', 'non-component, non-interface [Resolve] fields are not reported',
    ('InjectionMetadata.cs',
     'if (!typeof(Component).IsAssignableFrom(fieldType) && !fieldType.IsInterface)', 'if (false)', 1))

mut('M45', 'base', 'only the most-derived type is scanned for injection members',
    ('InjectionMetadata.cs',
     'for (var t = type; t != null && !IsEngineOrFrameworkType(t); t = t.BaseType)',
     'for (var t = type; t != null && t == type; t = t.BaseType)', 1))

mut('M46', 'M16', '[Resolve(Optional)] misses are warned about and clear the field',
    ('ComponentResolver.cs',
     'if (plan.Optional) return; // may legitimately be absent: no warning, keep whatever is there', '', 1))

# ── Diagnostics (M30, M31, cycles) ─────────────────────────────────────────────────────────────────────────────
mut('M47', 'cycle', 'cycle detection off (expected: stack overflow)',
    ('DiContainer.Diagnostics.cs', 'throw Circular(descriptor);', ';', 1))

mut('M48', 'M31', 'resolution path is no longer attached to exceptions',
    ('DiContainer.Diagnostics.cs',
     'if (!ex.Data.Contains(ResolutionPathKey)) ex.Data[ResolutionPathKey] = BuildPath();', '', 1))

mut('M49', 'M30', 'the library logs an error on every failed resolve',
    ('DiContainer.Diagnostics.cs',
     L('        private void Annotate(Exception ex)', '        {', '            try'),
     L('        private void Annotate(Exception ex)', '        {', '            Debug.LogError("noise");', '            try'), 1))

# ── Validation (C5) ────────────────────────────────────────────────────────────────────────────────────────────
mut('M50', 'C5', 'Validate() does not report captive dependencies',
    ('DiContainer.Validation.cs',
     'if (!lazy && consumer.Lifetime == Lifetime.Singleton && owner._parent == null && found.Lifetime == Lifetime.Scoped',
     'if (false && !lazy && consumer.Lifetime == Lifetime.Singleton && owner._parent == null && found.Lifetime == Lifetime.Scoped', 1))

mut('M51', 'C5', 'Validate() does not report missing dependencies',
    ('DiContainer.Validation.cs',
     'Report($"\'{PrettyName(consumer.ServiceType)}\' ({what}) needs \'{PrettyName(dependency)}\', which is not registered.", errors, reported);',
     ';', 1))


# ── Wave 2: guards and details that wave 1 did not touch ───────────────────────────────────────────────────────
mut('M52', 'options', 'scopes do not inherit the root options',
    ('DiContainer.cs', '_options = options != null ? options.Clone() : parent._options;',
     '_options = (options ?? new DiContainerOptions()).Clone();', 1))

mut('M53', 'options', 'the options object is not copied (later changes leak into the container)',
    ('DiContainer.cs', '_options = (options ?? new DiContainerOptions()).Clone();', '_options = options ?? new DiContainerOptions();', 1))

mut('M54', 'M33', 'generic type names are printed raw (Func`1)',
    ('DiContainer.cs', 'if (tick > 0) name = name.Substring(0, tick);', '', 1))

mut('M55', 'M15', 'Dispose() ignores an IAsyncDisposable that finishes synchronously',
    ('DiContainer.Disposal.cs', 'if (pending.IsCompleted) pending.GetAwaiter().GetResult();',
     'if (false) pending.GetAwaiter().GetResult();', 1))

mut('M56', 'holder', 'the root never releases the hidden prefab holder',
    ('DiContainer.Disposal.cs', 'if (_parent == null) _state.ReleaseHolder();', '', 1))

mut('M57', 'M6', 'ResolveAll(includeParents: false) still includes parents',
    ('DiContainer.Resolution.cs', 'scope = includeParents ? scope._parent : null)', 'scope = scope._parent)', 1),
    ('DiContainer.Resolution.cs', 'if (includeParents && _parent != null)', 'if (_parent != null)', 1))

mut('M58', 'M6', 'ResolveAll lists the scope\'s own bindings before its ancestors\'',
    ('DiContainer.Resolution.cs',
     L('            if (includeParents && _parent != null)',
       '                _parent.CollectInto(resolver, elementType, true, target, ref index);',
       '',
       '            if (_registry == null || !_registry.TryGetValue(elementType, out var bindings)) return;',
       '            for (int i = 0; i < bindings.Count; i++)',
       '                target.SetValue(resolver.ResolveDescriptor(bindings[i], this), index++);'),
     L('            if (_registry != null && _registry.TryGetValue(elementType, out var bindings))',
       '                for (int i = 0; i < bindings.Count; i++)',
       '                    target.SetValue(resolver.ResolveDescriptor(bindings[i], this), index++);',
       '',
       '            if (includeParents && _parent != null)',
       '                _parent.CollectInto(resolver, elementType, true, target, ref index);'), 1))

mut('M59', 'alias', 'an alias to itself is accepted',
    ('DiContainer.Registration.cs', 'if (aliasType == targetType)', 'if (false)', 1))

mut('M60', 'alias', 'an alias that resolves to the wrong type is not diagnosed',
    ('DiContainer.Resolution.cs', 'if (!descriptor.ServiceType.IsInstanceOfType(result))', 'if (false)', 1))

mut('M61', 'M14', 'RegisterInstance accepts null / destroyed objects',
    ('DiContainer.Registration.cs', 'if (instance == null || IsDestroyedUnityObject(instance))', 'if (false)', 1))

mut('M62', 'M14', 'RegisterInstance accepts an object of the wrong type',
    ('DiContainer.Registration.cs', 'if (!serviceType.IsInstanceOfType(instance))', 'if (false)', 1))

mut('M63', 'dead', 'a destroyed registered instance is returned instead of diagnosed',
    ('DiContainer.Resolution.cs', 'if (descriptor.IsDestroyedInstance)', 'if (false)', 1))

mut('M64', 'M-null', 'InjectInto(null) is silently accepted',
    ('DiContainer.Injection.cs',
     L('            if (instance == null)', '                throw new ArgumentNullException(nameof(instance),'),
     L('            if (false)', '                throw new ArgumentNullException(nameof(instance),'), 1))

mut('M65', 'path', 'InjectInto pushes no frame (no path for field injection failures)',
    ('DiContainer.Injection.cs', 'stack.Add(new ResolutionFrame(null, this, type));', '', 1))

mut('M66', 'path', 'the parameter being resolved is not recorded in the path',
    ('DiContainer.Injection.cs',
     L('            SetMember(parameter);', '            return ResolveInternal(type);'),
     '            return ResolveInternal(type);', 1))

mut('M67', 'M16', '[Resolve] fields are not filled by InjectInto',
    ('DiContainer.Injection.cs',
     L('            if (plan.NeedsComponentResolution && instance is Component component)', '                ComponentResolver.Resolve(component);'),
     '', 1))

mut('M68', 'C9', '[Resolve(Parent)] ignores inactive parents (every parent is inactive under the prefab holder)',
    ('ComponentResolver.cs', 'component.GetComponentInParent(type, true)', 'component.GetComponentInParent(type, false)', 1))

mut('M69', 'M31', 'a throwing Dispose stops the remaining disposals',
    ('DiContainer.Disposal.cs',
     L('                    catch (Exception ex)', '                    {',
       '                        Debug.LogError($"[DiContainer] Exception disposing \'{item.GetType().Name}\':\\n{ex}");', '                    }'),
     L('                    catch (Exception)', '                    {', '                        throw;', '                    }'), 2))

mut('M70', 'M6', 'T[] of a value-type T is claimed to be resolvable',
    ('DiContainer.Resolution.cs', 'return type.GetArrayRank() == 1 && !type.GetElementType().IsValueType;', 'return true;', 1))

mut('M71', 'C3', 'HasRegistration works on a disposed container',
    ('DiContainer.Resolution.cs',
     L('            ThrowIfDisposed();', '            MainThread.Assert(nameof(HasRegistration));',
       '            if (type == null) throw new ArgumentNullException(nameof(type));',
       '            return CanResolve(type);'),
     L('            MainThread.Assert(nameof(HasRegistration));',
       '            if (type == null) throw new ArgumentNullException(nameof(type));',
       '            return CanResolve(type);'), 1))

mut('M72', 'C3', 'ResolveAll works on a disposed container',
    ('DiContainer.Resolution.cs',
     L('            ThrowIfDisposed();', '            MainThread.Assert(typeof(TService));', '            return (TService[])ResolveAllCore(typeof(TService), includeParents);'),
     L('            MainThread.Assert(typeof(TService));', '            return (TService[])ResolveAllCore(typeof(TService), includeParents);'), 1))

mut('M73', 'C3', 'registering on a disposed container works',
    ('DiContainer.Registration.cs',
     L('        private void RegisterType(Type serviceType, Type implType, Lifetime lifetime, RegistrationMode mode)', '        {', '            ThrowIfDisposed();'),
     L('        private void RegisterType(Type serviceType, Type implType, Lifetime lifetime, RegistrationMode mode)', '        {'), 1))

mut('M74', 'alias', 'aliases are resolved as separate objects (no forwarding)',
    ('DiContainer.Resolution.cs', 'case DescriptorKind.Alias: return ResolveAlias(descriptor);', '', 1))

def edit_file(path, old, new, count):
    with open(path, encoding='utf-8') as fh:
        text = fh.read()
    found = text.count(old)
    if found != count:
        raise RuntimeError('expected %d occurrence(s) of %r in %s, found %d' % (count, old[:70], os.path.basename(path), found))
    with open(path, 'w', encoding='utf-8') as fh:
        fh.write(text.replace(old, new))


def run_mutation(entry):
    mid, flaw, desc, edits = entry
    d = str(OUT / mid)
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    shutil.copytree(str(SRC), d + '/Runtime')
    try:
        for (name, old, new, count) in edits:
            edit_file('%s/Runtime/%s' % (d, name), old, new, count)
    except Exception as ex:  # a mutation that does not apply proves nothing
        return dict(id=mid, flaw=flaw, desc=desc, status='BAD-MUTATION', detail=str(ex), failed=[])

    build = subprocess.run(
        ['dotnet', 'build', str(RIG), '-p:RuntimeDir=%s/Runtime' % d, '-p:TestsDir=' + str(TESTS),
         '-p:NuGetAudit=false', '-p:BaseIntermediateOutputPath=%s/obj/' % d, '-p:MSBuildProjectExtensionsPath=%s/obj/' % d,
         '-p:UseSharedCompilation=false', '-nodeReuse:false', '-o', d + '/out',
         '-v:q', '-nologo'],
        capture_output=True, text=True)
    if build.returncode != 0:
        errors = sorted({m for m in re.findall(r'error CS\d+: [^\[]+', build.stdout)})[:3]
        return dict(id=mid, flaw=flaw, desc=desc, status='BUILD-ERROR', detail='; '.join(errors), failed=[])

    try:
        run = subprocess.run(['dotnet', d + '/out/rig.dll'], capture_output=True, text=True, timeout=240)
    except subprocess.TimeoutExpired:
        return dict(id=mid, flaw=flaw, desc=desc, status='KILLED', detail='timeout (hang)', failed=[])

    out = run.stdout
    summary = re.search(r'(\d+) passed, (\d+) failed', out)
    failed = re.findall(r'^\s+FAIL (\S+)', out, flags=re.M)
    if not summary:
        tail = (run.stderr or out).strip().splitlines()[:1]
        return dict(id=mid, flaw=flaw, desc=desc, status='KILLED', detail='process died: ' + (tail[0][:90] if tail else ''), failed=[])
    nfail = int(summary.group(2))
    if nfail == 0:
        return dict(id=mid, flaw=flaw, desc=desc, status='SURVIVED', detail='all %s tests still pass' % summary.group(1), failed=[])
    return dict(id=mid, flaw=flaw, desc=desc, status='KILLED', detail='%d failing' % nfail, failed=failed)


def main():
    if shutil.which('dotnet') is None:
        print('The .NET 8 SDK is required to run the mutation suite (dotnet was not found).', file=sys.stderr)
        return 2

    selected = set(sys.argv[1:])
    entries = [e for e in M if not selected or e[0] in selected]
    OUT.mkdir(parents=True, exist_ok=True)
    results = []
    with ThreadPoolExecutor(max_workers=2) as pool:
        for result in pool.map(run_mutation, entries):
            results.append(result)
            head = '%-4s %-9s %-13s %s' % (result['id'], result['flaw'], result['status'], result['desc'])
            print(head, flush=True)
            if result['status'] != 'KILLED' or not result['failed']:
                print('       ' + result['detail'], flush=True)
            else:
                print('       %s: %s' % (result['detail'], ', '.join(result['failed'][:3])), flush=True)
    with open(OUT / 'results.json', 'w') as fh:
        json.dump(results, fh, indent=1)
    summary = {}
    for r in results:
        summary[r['status']] = summary.get(r['status'], 0) + 1
    print('SUMMARY', summary, flush=True)


if __name__ == '__main__':
    raise SystemExit(main())

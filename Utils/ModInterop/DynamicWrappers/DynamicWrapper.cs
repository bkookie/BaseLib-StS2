using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace BaseLib.Utils.ModInterop.DynamicWrappers;

/// <summary>
/// Wraps objects for use by another mod using interfaces, and allowing two-way communication between them. The wrapped object must implement an identical/mirror interface to that of the other mod.
/// <br/>For this to work, you must first declare/register all such interfaces you are using by calling <see cref="DeclareAndRegisterInterfaces(string, string, IEnumerable{Type})"/>, or apply the <see cref="DynamicInteropAttribute"/> to them.
/// </summary>
/// <remarks>
/// Requires <see cref="ModManager"/> to be initialized first (ie. after all mods have been loaded).
/// </remarks>
public class DynamicWrapper
{
    private static Dictionary<string, List<Assembly>> LoadedModAssemblies
    {
        get
        {
            if (field == null)
            {
                // Maybe should wait until AssemblyInfo.Init() instead, to give time for multi-assembly mods to register all their assemblies.
                if (ModManager.State != ModManagerState.Initialized)
                    throw new InvalidOperationException($"{nameof(ModManager)} has not initialized yet (current state is '{ModManager.State}'). Please wait until all mods have been loaded.");

                // Not using ModInterop or WhatMod's assembly list; they may not be initialized in time.
                field = ModManager.GetLoadedMods().Where(mod => mod.manifest?.id != null).ToDictionary(mod => mod.manifest!.id!, mod => mod.assemblies);
            }
            return field;
        }
    }

    private readonly record struct InteropKey(Type TargetInterface, Type SourceType) { public override readonly string ToString() => $"{{Target = \"{ModIdsByRegisteredType[TargetInterface]}\", {TargetInterface.Name}}}, {{Source = \"{ModIdsByRegisteredType.GetValueOrDefault(SourceType)}\", {SourceType.Name}}}"; }
    private readonly record struct AltKey(string TargetModId, Type SourceType) { public override readonly string ToString() => $"Target = \"{TargetModId}\", SourceType = \"{SourceType.Name}\", {SourceType}"; }
    private record class InteropData(string SourceModId, Type SourceInterface, Type TargetInterface, DynamicWrapperFactory WrapperFactory) { public override string ToString() => $"\"{SourceModId}\", {SourceInterface.Name}, {TargetInterface}"; }

    private static HashSet<(string targetModId, string sourceModId, Type sourceInterface)> AttributeDeclarations = [];
#if DEBUG
    private static readonly SortedDictionary<string, HashSet<Type>> DeclaredInterfaces = [];
    private static readonly SortedDictionary<Type, string> ModIdsByRegisteredType = new(Comparer<Type>.Create((x, y) => (x.Name + x.FullName).CompareTo(y.Name + y.FullName)));
    private static readonly SortedDictionary<InteropKey, InteropData?> InteropLookup = new(Comparer<InteropKey>.Create((x, y) => (ModIdsByRegisteredType.GetValueOrDefault(x.TargetInterface, "zzzzz") + x.TargetInterface.Name + x.TargetInterface.FullName + ModIdsByRegisteredType.GetValueOrDefault(x.SourceType, "zzzzz") + x.SourceType.Name + x.SourceType.FullName).CompareTo(ModIdsByRegisteredType.GetValueOrDefault(y.TargetInterface, "zzzzz") + y.TargetInterface.Name + y.TargetInterface.FullName + ModIdsByRegisteredType.GetValueOrDefault(y.SourceType, "zzzzz") + y.SourceType.Name + y.SourceType.FullName)));
    private static readonly SortedDictionary<AltKey, InteropData?> AltLookup = new(Comparer<AltKey>.Create((x, y) => (x.TargetModId + x.SourceType.Name + x.SourceType.FullName).CompareTo(y.TargetModId + y.SourceType.Name + y.SourceType.FullName)));
#else
    private static readonly Dictionary<string, HashSet<Type>> DeclaredInterfaces = [];
    private static readonly Dictionary<Type, string> ModIdsByRegisteredType = [];
    private static readonly Dictionary<InteropKey, InteropData?> InteropLookup = [];
    private static readonly Dictionary<AltKey, InteropData?> AltLookup = [];
#endif

    /// <summary>
    /// Fired whenever a type is registered.
    /// </summary>
    public static event TypeRegisteredEventHandler? TypeRegistered;

    /// <summary>
    /// The mod that will be receiving objects.
    /// </summary>
    public string TargetModId { get; }

    /// <summary>
    /// The mod that will be creating and sending objects.
    /// </summary>
    public string? SourceModId { get; }

    /// <summary>
    /// Creates a local instance with the specified <paramref name="targetModId"/>. All method calls pass through to the static methods, using the supplied modId. No additional functionality.
    /// </summary>
    /// <remarks>Since you are not specifiying a sourceModId, you can only make calls to Wrap() (anything else will result in an <see cref="InvalidOperationException"/>).</remarks>
    /// <param name="targetModId">The mod that you are wrapping objects for. If you are using this overload, it is probably your own, and you might be expecting to receive external objects.</param>
    public DynamicWrapper(string targetModId) : this(targetModId, null) { }

    /// <summary>
    /// Creates a local instance with the specified <paramref name="targetModId"/> and <paramref name="sourceModId"/>. All method calls pass through to the static methods, using the supplied modIds. No additional functionality.
    /// </summary>
    /// <param name="targetModId">The mod that you are wrapping objects for (usually the optional mod).</param>
    /// <param name="sourceModId">The mod that will be supplying the objects. Usually your own mod. Supply <see langword="null"/> if you are just providing support for your mod to be used as the optional mod (in this case, <paramref name="targetModId"/> should be your mod).</param>
    public DynamicWrapper(string targetModId, string? sourceModId)
    {
        TargetModId = targetModId;
        SourceModId = sourceModId;
    }

    /// <inheritdoc cref="DeclareInterface(string, string, Type, string?)"/>
    public void DeclareInterface(Type sourceInterface, string? targetInterfaceFullName = null)
    {
        if (SourceModId == null)
            throw SourceModNullException();

        DeclareInterface(TargetModId, SourceModId, sourceInterface, targetInterfaceFullName);
    }

    /// <inheritdoc cref="DeclareInterfaces(string, string, IEnumerable{Type})"/>
    public void DeclareInterfaces(IEnumerable<Type> sourceInterfaces)
    {
        if (SourceModId == null)
            throw SourceModNullException();

        DeclareInterfaces(TargetModId, SourceModId, sourceInterfaces);
    }

    /// <inheritdoc cref="DeclareAndRegisterInterfaces(string, string, IEnumerable{Type})"/>
    public void DeclareAndRegisterInterfaces(IEnumerable<Type> sourceInterfaces)
    {
        if (SourceModId == null)
            throw SourceModNullException();

        DeclareAndRegisterInterfaces(TargetModId, SourceModId, sourceInterfaces);
    }

    /// <summary>
    /// Registers all previously declared interfaces.
    /// </summary>
    public void RegisterAllInterfaces()
    {
        if (SourceModId == null)
            throw SourceModNullException();

        RegisterAllInterfaces(TargetModId, SourceModId);
    }

    /// <summary>
    /// Marks a set of interfaces as mirrors of ones belonging to <paramref name="targetModId"/> (aka the optional mod), for use by dynamic mod interop.
    /// All such interfaces must be declared here before registering them, otherwise registration may fail.
    /// </summary>
    /// <param name="targetModId">The mod that defines these interfaces, that you intend to use for optional mod interop.</param>
    /// <param name="sourceModId">The mod that is defining the mirror interfaces.</param>
    /// <param name="sourceInterface">The mirror interface of that of the same name in <paramref name="targetModId"/>.</param>
    /// <param name="targetInterfaceFullName">The namespace qualified name of <paramref name="sourceInterface"/> in targetMod's assembly. The type name must still match exactly.
    /// If <see langword="null"/>, uses the type name of <paramref name="sourceInterface"/>.
    /// <br/> Use this in the rare case that the target assembly defines multiple interfaces of the same name.</param>
    public static void DeclareInterface(string targetModId, string sourceModId, Type sourceInterface, string? targetInterfaceFullName = null)
    {
        DeclareInterfaceInternal(targetModId, sourceModId, sourceInterface, targetInterfaceFullName);
    }

    /// <inheritdoc cref="DeclareInterface(string, string, Type, string?)"/>
    /// <param name="sourceInterfaces">The mirror interfaces of those of the same name belonging to <paramref name="targetModId"/>.</param>
    public static void DeclareInterfaces(string targetModId, string sourceModId, IEnumerable<Type> sourceInterfaces)
    {
        foreach (Type sourceInterface in sourceInterfaces)
        {
            DeclareInterfaceInternal(targetModId, sourceModId, sourceInterface);
        }
    }

    /// <summary>
    /// Declares then registers all the supplied interfaces.
    /// </summary>
    /// <inheritdoc cref="DeclareInterfaces(string, string, IEnumerable{Type})"/>
    public static void DeclareAndRegisterInterfaces(string targetModId, string sourceModId, IEnumerable<Type> sourceInterfaces)
    {
        DeclareInterfaces(targetModId, sourceModId, sourceInterfaces);
        RegisterAllInterfaces(targetModId, sourceModId);
    }

    /// <summary>
    /// Registers all previously declared interfaces from <paramref name="sourceModId"/> against <paramref name="targetModId"/>.
    /// </summary>
    /// <param name="targetModId">The mod to register against.</param>
    /// <param name="sourceModId">The mod whose previously decalared interfaces you are now registering.</param>
    public static void RegisterAllInterfaces(string targetModId, string sourceModId)
    {
        if (DeclaredInterfaces.TryGetValue(sourceModId, out HashSet<Type>? declaredTypes))
        {
            foreach (Type sourceInterface in declaredTypes)
            {
                RegisterType(targetModId, sourceModId, null, sourceInterface);
            }
        }
    }

    /// <inheritdoc cref="DeclareInterface(string, string, Type, string?)"/>
    private static void DeclareInterfaceInternal(string targetModId, string sourceModId, Type sourceInterface, string? targetInterfaceFullName = null)
    {
        if (TryGetTypeFromModId(targetModId, sourceInterface.Name, targetInterfaceFullName, out Type? targetInterface))
        {
            Declare(targetModId, targetInterface, out _);
            Declare(sourceModId, sourceInterface, out bool duplicate);

            if (duplicate)
                BaseLibMain.Logger.Warn($"[DynamicWrappers] Interface '{sourceInterface}' has already been declared");
            else
                BaseLibMain.Logger.Debug($"[DynamicWrappers] Declared mirror interfaces '{targetInterface}' and '{sourceInterface}' for dynamic interop");
        }
        else
        {
            BaseLibMain.Logger.Error($"[DynamicWrappers] Interface '{targetInterfaceFullName ?? sourceInterface.Name}' from '{targetModId}' could not be found");
        }

        static void Declare(string modId, Type @interface, out bool duplicate)
        {
            if (DeclaredInterfaces.TryGetValue(modId, out HashSet<Type>? types))
            {
                duplicate = !types.Add(@interface);
            }
            else
            {
                DeclaredInterfaces[modId] = [@interface];
                duplicate = false;
            }
        }
    }

    internal static void ProcessType(Type sourceInterface)
    {
        if (!sourceInterface.IsInterface)
            return;

        DynamicInteropAttribute? attr = sourceInterface.GetCustomAttribute<DynamicInteropAttribute>();
        if (attr == null || !LoadedModAssemblies.ContainsKey(attr.TargetModId))
            return;

        DeclareInterfaceInternal(attr.TargetModId, attr.SourceModId, sourceInterface, attr.TargetInterfaceFullName);
        AttributeDeclarations.Add((attr.TargetModId, attr.SourceModId, sourceInterface));
    }

    internal static void FinalizeTypeProcessing()
    {
        foreach (var (targetModId, sourceModId, sourceInterface) in AttributeDeclarations)
        {
            RegisterType(targetModId, sourceModId, null, sourceInterface);
        }
        AttributeDeclarations = null!; // Never used again
    }





    private static bool TryGetTypeFromModId(string modId, string typeName, string? namespaceQualifiedName, [NotNullWhen(true)] out Type? type)
    {
        if (LoadedModAssemblies.TryGetValue(modId, out var assemblies))
        {
            foreach (Assembly assembly in assemblies)
            {
                type = namespaceQualifiedName != null
                    ? assembly.GetType(namespaceQualifiedName)
                    : assembly.GetExportedTypes().FirstOrDefault(t => t.Name == typeName);
                if (type != null)
                    return true;
            }
        }
        type = null;
        return false;
    }





    /// <summary>
    /// Registers a type and/or interface as being a mirror for another mod's interface, and to generate dynamic wrappers that allow each others' interfaces to communicate.
    /// </summary>
    /// <remarks>
    /// Note that dynamic wrappers dont work with debugger step-through, since they have no source code (dissassembly only).
    /// </remarks>
    /// <param name="targetModId">The target mod (usually the optional mod).</param>
    /// <param name="sourceModId">Your own mod (usually).</param>
    /// <param name="sourceType">The type that you are registering, that implements <paramref name="sourceInterface"/>. Set to <see langword="null"/> if registering <paramref name="sourceInterface"/> itself.
    /// Will automatically register <paramref name="sourceInterface"/> if not already done.</param>
    /// <param name="sourceInterface">The interface that is a mirror for one in <paramref name="targetModId"/>.
    /// Must have same name and implementation as targetMod's (any other interfaces from <paramref name="targetModId"/> that are used as parameters or return types must be mirrored and registered as well).</param>
    /// <exception cref="ArgumentException"><paramref name="sourceInterface"/> is not an interface, or it does not implement all members of the target interface.</exception>
    /// <exception cref="InvalidOperationException">The interface '<paramref name="sourceInterface"/>' has not been registered.</exception>
    private static InteropData? RegisterType(string targetModId, string sourceModId, Type? sourceType, Type sourceInterface, bool isAutoRegistration = false)
    {
        if (!sourceInterface.IsInterface)
        {
            throw new ArgumentException($"The provided argument '{nameof(sourceInterface)}' of type '{sourceInterface.GetType()}' is not an interface. DynamicWrappers require an interface.", nameof(sourceInterface));
        }

        sourceType ??= sourceInterface;

        Type? targetInterface = null;
        if (DeclaredInterfaces.TryGetValue(targetModId, out HashSet<Type>? declaredTypes))
        {
            targetInterface = declaredTypes.FirstOrDefault(type => type.Name == sourceInterface.Name);
        }

        if (targetInterface != null)
        {
            // WARNING: Do not add concrete types to AltLookup here; types may implement multiple interfaces, but AltLookup only supports one per target mod
            if (InteropLookup.TryGetValue(new(targetInterface, sourceInterface), out InteropData? interopData))
            {
                if (interopData == null)
                    throw new InvalidOperationException($"Internal logic error: Trying to register '{sourceInterface}' with '{targetModId}', but it has already been marked as not wrappable.");

                InteropKey key;
                if (sourceType != sourceInterface && !InteropLookup.ContainsKey(key = new(targetInterface, sourceType)))
                {
                    // Already generated for this sourceInterface, we can reuse the data for this concrete type
                    // Nothing to add to reverse lookups; they only require the interface, not concrete types, and it too will have already been generated
                    ModIdsByRegisteredType[sourceType] = sourceModId;
                    InteropLookup[key] = interopData;
                    TypeRegistered?.Invoke(new TypeRegisteredEventArgs(targetModId, sourceModId, sourceType, sourceInterface, interopData.TargetInterface));
                }
                else
                {
                    BaseLibMain.Logger.Warn($"[DynamicWrappers] The type '{sourceType}' has already been registered with '{targetModId}' and interface '{sourceInterface.Name}'");
                }
                return interopData;
            }
            else if (sourceType != sourceInterface)
            {
                // Register the interface first, then the concrete type can copy that data
                interopData = RegisterType(targetModId, sourceModId, null, sourceInterface, isAutoRegistration);
                if (interopData == null)
                {
                    // Only returns null during auto-registration, so is safe to return null again here
                    return null;
                }
                ModIdsByRegisteredType[sourceType] = sourceModId;
                InteropLookup[new(targetInterface, sourceType)] = interopData;
                TypeRegistered?.Invoke(new TypeRegisteredEventArgs(targetModId, sourceModId, sourceType, sourceInterface, interopData.TargetInterface));
                return interopData;
            }

            Dictionary<string, Delegate> delegates = [];
            Dictionary<string, Delegate> reverseDelegates = [];

            // Generate the delegates that will map wrappers to their object instance
            // Could do this without delegates (they are from the original implementation), but I dont think it is worth rewriting
            foreach (MethodInfo targetMethod in targetInterface.GetMethods())
            {
                Type[] targetParamTypes = [.. targetMethod.GetParameters().Select(pInfo => pInfo.ParameterType)];
                Type[] sourceParamTypes = null!;
                MethodInfo? sourceMethod = sourceType.GetMethods().FirstOrDefault(method =>
                {
                    // Check for matching return and param types, unless the type is a declared interface (these will get wrapped in the emitted IL)
                    sourceParamTypes = [.. method.GetParameters().Select(pInfo => pInfo.ParameterType)];
                    if (method.Name == targetMethod.Name && sourceParamTypes.Length == targetParamTypes.Length && MatchTypes(method.ReturnType, targetMethod.ReturnType, declaredTypes!))
                    {
                        for (int i = 0; i < sourceParamTypes.Length; i++)
                        {
                            if (!MatchTypes(sourceParamTypes[i], targetParamTypes[i], declaredTypes!))
                            {
                                return false;
                            }
                        }
                        return true;
                    }
                    return false;

                    static bool MatchTypes(Type sourceType, Type targetType, HashSet<Type> declaredTypes)
                    {
                        // Note that it is not checking if declared types are actually mirrors of each other. If not mirrored, it will cause problems further down the track
                        // TODO: actually perform this check properly
                        return sourceType == targetType || sourceType.IsEnum && targetType.IsEnum || declaredTypes.Contains(targetType);
                    }
                });

                if (sourceMethod != null)
                {
                    Type delegateType = Expression.GetDelegateType([sourceType, .. sourceParamTypes, sourceMethod.ReturnType]); // Func<TParam1, ..., TReturn> -or- Func<TInstance, TParam1, ..., TReturn> if using an open (static) delegate (pass null for the target to Delegate.CreateDelegate(). Is an Action<> for void methods.
                    Delegate del = Delegate.CreateDelegate(delegateType, null, sourceMethod); // can load directly into fields of type Func<,>
                    delegates.Add(sourceMethod.Name, del);

                    Type reverseDelegateType = Expression.GetDelegateType([targetInterface, .. targetParamTypes, targetMethod.ReturnType]);
                    del = Delegate.CreateDelegate(reverseDelegateType, null, targetMethod);
                    reverseDelegates.Add(targetMethod.Name, del);
                }
                else
                {
                    throw new ArgumentException($"The provided type '{sourceType}' does not implement the required interface members (missing '{targetInterface}.{targetMethod.Name}').");
                }
            }

            var _delegates = delegates.AsReadOnly();
            var _reverseDelegates = reverseDelegates.AsReadOnly();

            // While its possible to define compile-time wrappers using dynamic objects, you can only do this for your own side, and I think it only makes sense if both parties do this.
            // Adds a bunch of complexity, and doesnt gain a whole lot (functionally identically, and you'll suffer the DLR kicking in on first use), so not giving the option.
            DynamicWrapperFactory factory = CreateWrapperFactory(targetInterface, sourceInterface, _delegates, targetModId, sourceModId);
            DynamicWrapperFactory reverseFactory = CreateWrapperFactory(sourceInterface, targetInterface, _reverseDelegates, sourceModId, targetModId);

            InteropData data = new(sourceModId, sourceInterface, targetInterface, factory);
            InteropData reverseData = new(targetModId, targetInterface, sourceInterface, reverseFactory);

            if (!isAutoRegistration) // Otherwise already done
            {
                ModIdsByRegisteredType[targetInterface] = targetModId;
                ModIdsByRegisteredType[sourceInterface] = sourceModId;
            }

            InteropLookup[new(targetInterface, sourceInterface)] = data;
            InteropLookup[new(sourceInterface, targetInterface)] = reverseData;

            AltLookup[new(targetModId, sourceInterface)] = data;
            AltLookup[new(sourceModId, targetInterface)] = reverseData;

            BaseLibMain.Logger.Info($"[DynamicWrappers] Built DynamicWrappers for interop between '{sourceModId}' and '{targetModId}' with interface '{targetInterface.Name}'{(isAutoRegistration ? " (auto-registration)" : "")}");

            TypeRegistered?.Invoke(new TypeRegisteredEventArgs(targetModId, sourceModId, sourceType, sourceInterface, targetInterface));

            if (!isAutoRegistration) // Dont recurse on auto rego
            {
                // Register with all other mods that have already registered with the same targetInterface. These mods may come into contact with each other via targetMod.
                // This does not register concrete types (will be fine so long as two such mods dont try to communicate directly somehow)
                // Note: If a lot of mods are registering against the same target interface, this can lead to a lot of combinations, though I dont imagine it will be an issue for sts2 modding.
                string[] otherModIds = [.. AltLookup.Keys.Where(key => key.SourceType == targetInterface && key.TargetModId != sourceModId).Select(key => key.TargetModId)];
                if (otherModIds.Length > 0)
                {
                    BaseLibMain.Logger.Debug($"[DynamicWrappers] Auto-registering interop between '{sourceModId}' and {otherModIds.Length} other mod(s) registered with '{targetInterface}'");
                    foreach (string modId in otherModIds)
                    {
                        RegisterType(modId, sourceModId, null, sourceInterface, isAutoRegistration: true);
                    }
                }
            }

            return data;
        }
        else if (isAutoRegistration)
        {
            BaseLibMain.Logger.Warn($"[DynamicWrappers] Could not auto-register interface '{sourceInterface}' with '{targetModId}'");
            return null;
        }
        else
        {
            throw new InvalidOperationException($"The interface '{sourceInterface}' has not been was registered with '{targetModId}'.");
        }
    }





    /// <inheritdoc cref="Wrap{T}(string, object?, Type?)"/>
    [return: NotNullIfNotNull(nameof(objectToWrap))]
    public object? Wrap(object? objectToWrap, Type? sourceInterface = null)
    {
        return WrapInternal(TargetModId, null, objectToWrap, sourceInterface);
    }

    /// <inheritdoc cref="Wrap{T}(string, object?, Type?)"/>
    [return: NotNullIfNotNull(nameof(objectToWrap))]
    public T? Wrap<T>(object? objectToWrap, Type? sourceInterface = null) where T : class
    {
        return (T?)WrapInternal(TargetModId, typeof(T), objectToWrap, sourceInterface);
    }

    /// <inheritdoc cref="Wrap{T}(string, object?, Type?)"/>
    [return: NotNullIfNotNull(nameof(objectToWrap))]
    public static object? Wrap(string targetModId, object? objectToWrap, Type? sourceInterface = null)
    {
        // WARNING: If you change this method signature, need to update the MethodInfo fields (look for the matching WARNING comment) and associated IL in CreateWrapperFactory
        return WrapInternal(targetModId, null, objectToWrap, sourceInterface);
    }

    /// <summary>
    /// Wraps <paramref name="objectToWrap"/> for <paramref name="targetModId"/> consumption.
    /// </summary>
    /// <typeparam name="T">The target interface to wrap <paramref name="objectToWrap"/> with. This interface must be native to <paramref name="targetModId"/>.</typeparam>
    /// <param name="targetModId">The modId that is the intended recipient of <paramref name="objectToWrap"/>.</param>
    /// <param name="objectToWrap">The object to wrap.</param>
    /// <param name="sourceInterface">The source interface to wrap by. Only required when the target interface is unknown and <paramref name="objectToWrap"/> implements multiple wrappable interfaces.</param>
    /// <returns>Either an <see cref="IDynamicWrapper"/> implenting an interface that <paramref name="targetModId"/> can consume, or the object instance itself if it is native to <paramref name="targetModId"/>.
    /// Returns <see langword="null"/> if <paramref name="objectToWrap"/> is <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The interface <typeparamref name="T"/> has not been registered by <paramref name="targetModId"/>.</exception>
    [return: NotNullIfNotNull(nameof(objectToWrap))]
    public static T? Wrap<T>(string? targetModId, object? objectToWrap, Type? sourceInterface = null)
    {
        // WARNING: If you change this method signature, need to update the MethodInfo fields (look for the matching WARNING comment) and associated IL in CreateWrapperFactory
        return (T?)WrapInternal(targetModId, typeof(T), objectToWrap, sourceInterface);
    }

    /// <inheritdoc cref="Wrap{T}(string, object?, Type?)"/>
    /// <param name="targetInterface">The target interface to wrap <paramref name="objectToWrap"/> with. This interface must be native to <paramref name="targetModId"/>. If <see langword="null"/>, <paramref name="targetModId"/> must be supplied.</param>
    /// <param name="returnNullIfNotWrapped">If <see langword="true"/>, returns <see langword="null"/> when the object is not able to be wrapped. Otherwise, throws an <see cref="InvalidOperationException"/>.</param>
    [return: NotNullIfNotNull(nameof(objectToWrap))]
    private static object? WrapInternal(string? targetModId, Type? targetInterface, object? objectToWrap, Type? sourceInterface, bool returnNullIfNotWrapped = false)
    {
        if (objectToWrap == null)
            return null;

        Type sourceType = objectToWrap.GetType();

        if (targetInterface != null && sourceType.IsAssignableTo(targetInterface))
        {
            return objectToWrap; // No wrapper required
        }
        else if (objectToWrap is IDynamicWrapper wrapper)
        {
            if (sourceInterface != null && sourceInterface != wrapper.WrapperInterface)
            {
                BaseLibMain.Logger.Warn($"[DynamicWrappers] Mismatch between expected and actual interface type while wrapping '{wrapper.GetType()}': expected {nameof(sourceInterface)}='{sourceInterface}', actual {nameof(wrapper.WrapperInterface)}='{wrapper.WrapperInterface}'");
            }

            if (wrapper.WrapperModId == targetModId)
            {
                return wrapper;
            }
            else if (wrapper.InstanceModId == targetModId)
            {
                return wrapper.Instance;
            }

            // An alternative to auto-registration is to just wrap the wrapper (instead of stripping it), but could get messy.

            objectToWrap = wrapper.Instance; // Strip the current wrapper
            sourceType = objectToWrap.GetType();

            if (targetInterface != null && sourceType.IsAssignableTo(targetInterface))
            {
                return objectToWrap;
            }

            sourceInterface = wrapper.InstanceInterface;
        }

        if (TryFindWrapper(targetModId, targetInterface, sourceType, sourceInterface, out InteropData? data))
        {
            if (targetInterface == null && sourceType.IsAssignableTo(data.TargetInterface)) // If targetInterface is not-null, then this has already been checked
            {
                return objectToWrap;
            }
            return data.WrapperFactory(objectToWrap); // Apply wrapper
        }

        if (returnNullIfNotWrapped)
            return null!; // internal use only

        if (targetModId == null)
            throw new InvalidOperationException($"The type '{objectToWrap.GetType()}' has not been registered with interface {targetInterface?.FullName ?? "<NotProvided>"}.");

        throw new InvalidOperationException($"No wrappers found for the type '{objectToWrap.GetType()}' with target mod '{targetModId}'{(targetInterface == null ? "" : $" and target interface '{targetInterface}'")}. You must call {nameof(DynamicWrapper)}.{nameof(DeclareAndRegisterInterfaces)}() first.");



        static bool TryFindWrapper(string? targetModId, Type? targetInterface, Type sourceType, Type? sourceInterface, [NotNullWhen(true)] out InteropData? data)
        {
            // A lookup value of 'null' indicates not wrappable for the provided target. A missing entry altogether means the type requires processing

            if (targetInterface != null)
            {
                if (InteropLookup.TryGetValue(new(targetInterface, sourceInterface ?? sourceType), out data))
                    return data != null;
                else if (targetModId != null || ModIdsByRegisteredType.TryGetValue(targetInterface, out targetModId))
                    return TryRegisterType(targetModId, targetInterface, sourceType, out data);

                throw new InvalidOperationException($"The target interface '{targetInterface}' has not been registered.");
            }
            else if (targetModId != null)
            {
                if (AltLookup.TryGetValue(new(targetModId, sourceInterface ?? sourceType), out data))
                    return data != null;
                else
                    return TryRegisterType(targetModId, null, sourceType, out data);
            }

            throw new InvalidOperationException($"No {nameof(targetModId)} or {nameof(targetInterface)} supplied. At least one of these is required in order to wrap an object.");



            static bool TryRegisterType(string targetModId, Type? targetInterface, Type sourceType, [NotNullWhen(true)] out InteropData? data)
            {
                data = null;
                InteropData? interopData = null;
                int numWrappableInterfaces = 0;

                foreach (Type sourceInterface in sourceType.GetInterfaces())
                {
                    if (ModIdsByRegisteredType.TryGetValue(sourceInterface, out string? sourceModId))
                    {
                        InteropData? tempData = RegisterType(targetModId, sourceModId, sourceType, sourceInterface, isAutoRegistration: true);

                        if (tempData != null)
                        {
                            interopData = tempData;
                            if (targetInterface == null || interopData.TargetInterface == targetInterface)
                            {
                                data = interopData;
                            }

                            numWrappableInterfaces++;
                        }
                    }
                }

                if (numWrappableInterfaces == 1)
                {
                    if (interopData == null)
                        throw new InvalidOperationException($"Internal logic error: '{nameof(interopData)}' is null, even though a wrappable interface was found.");

                    // Registration does not automatically add concrete types to this lookup. Can only do this when they type implements only a single wrappable interface.
                    AltLookup[new(targetModId, sourceType)] = interopData;
                }
                else if (targetInterface == null && numWrappableInterfaces > 1)
                {
                    throw new AmbiguousMatchException($"'{sourceType}' implements multiple interfaces for mod '{targetModId}'. Please specify an interface explicitly.");
                }

                if (data == null)
                {
                    // Mark as not wrappable
                    if (targetInterface != null)
                    {
                        InteropLookup[new(targetInterface, sourceType)] = null;
                    }
                    AltLookup[new(targetModId, sourceType)] = null;
                }

                return data != null;
            }
        }
    }

    /// <inheritdoc cref="TryWrap(string, object?, Type?, out object?)"/>
    public bool TryWrap(object? objectToWrap, Type? sourceInterface, [NotNullWhen(true)] out object? value)
    {
        return TryWrap(TargetModId, objectToWrap, sourceInterface, out value);
    }

    /// <inheritdoc cref="TryWrap{T}(string, object?, out T)"/>
    public bool TryWrap<T>(object? objectToWrap, [NotNullWhen(true)] out T? value)
    {
        return TryWrap(TargetModId, objectToWrap, out value);
    }

    /// <summary>
    /// Attempts to wrap an object for <paramref name="targetModId"/>.
    /// </summary>
    /// <param name="targetModId">The modId that is the intended recipient of <paramref name="objectToWrap"/>.</param>
    /// <param name="objectToWrap">The object to wrap.</param>
    /// <param name="sourceInterface">The source interface to wrap by. Only required when the target interface is unknown and <paramref name="objectToWrap"/> implements multiple wrappable interfaces.</param>
    /// <param name="wrapper">If the method returned <see langword="true"/>, then this is either an <see cref="IDynamicWrapper"/> implenting an interface that <paramref name="targetModId"/> can consume, or the object instance itself if it is native to <paramref name="targetModId"/>.</param>
    /// <returns><see langword="true"/> if the object was successfully wrapped (or wrapping wasn't required), otherwise <see langword="false"/>. Returns <see langword="false"/> if <paramref name="objectToWrap"/> is <see langword="null"/>.</returns>
    public static bool TryWrap(string targetModId, object? objectToWrap, Type? sourceInterface, [NotNullWhen(true)] out object? wrapper)
    {
        wrapper = WrapInternal(targetModId, null, objectToWrap, sourceInterface, returnNullIfNotWrapped: true);
        return wrapper != null;
    }

    /// <typeparam name="T">The interface to wrap <paramref name="objectToWrap"/> with. This interface must be native to <paramref name="targetModId"/>.</typeparam>
    /// <param name="targetModId">The modId that is the intended recipient of <paramref name="objectToWrap"/>. If not known, supply <see langword="null"/>, and it will seek the target mod using type <typeparamref name="T"/>.</param>
    /// <inheritdoc cref="TryWrap(string, object?, Type?, out object?)"/>
    public static bool TryWrap<T>(string? targetModId, object? objectToWrap, [NotNullWhen(true)] out T? wrapper)
    {
        wrapper = (T?)WrapInternal(targetModId, typeof(T), objectToWrap, null, returnNullIfNotWrapped: true);
        return wrapper != null;
    }











    private static InvalidOperationException SourceModNullException() => new InvalidOperationException($"{nameof(SourceModId)} not defined. Either set the {nameof(SourceModId)} property, or call the static overload and provide the name explicitly.");

    /// <summary>
    /// A factory method to create an <see cref="IDynamicWrapper"/> for wrapping a supplied instance object.
    /// </summary>
    /// <param name="instance">The underlying instance object.</param>
    /// <returns>A dynamically generated wrapper class for the supplied <paramref name="instance"/>.</returns>
    private delegate IDynamicWrapper DynamicWrapperFactory(object instance);

    /// <summary>
    /// Defines the constructor signature for a DynamicWrapper.
    /// </summary>
    /// <param name="wrapperModId">The mod that the wrapper is for.</param>
    /// <param name="instanceModId">The mod that the underlying instance object is from.</param>
    /// <param name="instance">The underlying instance object.</param>
    /// <param name="delegates">A collection of delegates that will be used to map the wrapper to the instance object.</param>
    private delegate void DynamicWrapperConstructor(string wrapperModId, string instanceModId, object instance, IReadOnlyDictionary<string, Delegate> delegates);











    // Here is the meat

    internal static void RegisterDynamicAssembly()
    {
        // Unsure about this...if any caller wants to know, I think "BaseLib" is the wrong answer...
        ModManager.AssociateAssemblyWithMod(BaseLibMain.ModId, _assemblyBuilder);
    }

    const string AssemblyName = "DynamicWrappers";

    private static readonly AssemblyBuilder _assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(AssemblyName), AssemblyBuilderAccess.Run);
    private static readonly ModuleBuilder _moduleBuilder = _assemblyBuilder.DefineDynamicModule(AssemblyName);

#if DEBUG
    private static readonly PersistedAssemblyBuilder _persistedAssemblyBuilder = new PersistedAssemblyBuilder(new AssemblyName(AssemblyName), typeof(object).Assembly);
    private static readonly ModuleBuilder _persistedModuleBuilder = _persistedAssemblyBuilder.DefineDynamicModule(AssemblyName);
#endif

    // WARNING: If you change the signature of either Wrap<T>(string, object?, Type?) or Wrap(string, object?, Type?), make sure the below two fields can still find them
    // Also make sure to adjust the IL in CreateWrapperFactory() accordingly
    private static readonly MethodInfo wrapNonGeneric = ((Func<string, object?, Type?, object?>)Wrap).Method;
    private static readonly MethodInfo wrapOpenGeneric = ((Func<string, object?, Type?, object?>)Wrap<object>).Method.GetGenericMethodDefinition();
    private static readonly MethodInfo getTypeFromHandle = ((Func<RuntimeTypeHandle, Type?>)Type.GetTypeFromHandle).Method;


    /// <inheritdoc cref="CreateWrapperFactory(ModuleBuilder, Type, Type, IReadOnlyDictionary{string, Delegate}, string, string)"/>
    private static DynamicWrapperFactory CreateWrapperFactory(Type wrapperInterface, Type instanceInterface, IReadOnlyDictionary<string, Delegate> delegates, string targetModId, string originModId)
    {
        return CreateWrapperFactory(_moduleBuilder, wrapperInterface, instanceInterface, delegates, targetModId, originModId);
    }

    /// <summary>
    /// Generates a dynamic type to be used as a wrapper between <paramref name="wrapperModId"/> and <paramref name="instanceModId"/>.
    /// </summary>
    /// <remarks>
    /// Wrappers come in pairs - the default or main one is the more visible one, that will wrap the sourceMod's objects for targetMod's consumption.
    /// The reverse wrappers wrap targetMod's objects for sourceMod consumption, and will primarily be used internally by the main wrapper (as mentioned).
    /// Both types will also handle any internal wrapping of objects passed in as arguments, and return values on the way back.
    /// This may produce some redundant wrapping attempts, but those should silently pass through the current wrapper as is (or it's underlying object instance).
    /// </remarks>
    /// <param name="moduleBuilder">Which module to use. For runtime types, the module from the standard <see cref="AssemblyBuilder"/> is required.
    /// Only use the module from <see cref="PersistedAssemblyBuilder"/> for debugging, which allows saving to disk for inspection in dnSpy (and has no runtime presence).</param>
    /// <param name="wrapperInterface">The interface that this wrapper will implement.</param>
    /// <param name="instanceInterface">The mirror interface that the underlying object implements.</param>
    /// <param name="delegates">The dictionary of delegates that map the wrapper's members to those of the underlying object.</param>
    /// <param name="wrapperModId">The modId that this wrapper is being used for (ie. the optional mod), regardless of the direction being wrapped.</param>
    /// <param name="instanceModId">The modId that the instance object belongs to. Only used to create a unique namespace in the assembly.</param>
    /// <returns>A <see cref="DynamicWrapperFactory"/> delegate that can be invoked to instantiate new instances of the generated dynamic type.</returns>
    private static DynamicWrapperFactory CreateWrapperFactory(ModuleBuilder moduleBuilder, Type wrapperInterface, Type instanceInterface, IReadOnlyDictionary<string, Delegate> delegates, string wrapperModId, string instanceModId)
    {
        string typeName = $"{wrapperModId}.{instanceModId}.{wrapperInterface.Name[1..]}Wrapper"; // Remove the leading 'I' from the interface name. Will be awkward if it doesnt start with I
        TypeBuilder typeBuilder = moduleBuilder.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object), [wrapperInterface, typeof(IDynamicWrapper)]);

        MethodAttributes getterSetterAttributes = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

        // IDynamicWrapper properties
        FieldBuilder wrapperModIdField = typeBuilder.DefineField(nameof(IDynamicWrapper.WrapperModId).AsBackingFieldCamelCase, typeof(string), FieldAttributes.Private | FieldAttributes.InitOnly);
        FieldBuilder instanceModIdField = typeBuilder.DefineField(nameof(IDynamicWrapper.InstanceModId).AsBackingFieldCamelCase, typeof(string), FieldAttributes.Private | FieldAttributes.InitOnly);
        FieldBuilder instanceField = typeBuilder.DefineField(nameof(IDynamicWrapper.Instance).AsBackingFieldCamelCase, instanceInterface, FieldAttributes.Private | FieldAttributes.InitOnly);

        DefineIDynamicWrapperProperty(typeBuilder, typeof(IDynamicWrapper).GetProperty(nameof(IDynamicWrapper.WrapperModId))!, wrapperModIdField, getterSetterAttributes, null, null);
        DefineIDynamicWrapperProperty(typeBuilder, typeof(IDynamicWrapper).GetProperty(nameof(IDynamicWrapper.InstanceModId))!, instanceModIdField, getterSetterAttributes, null, null);
        DefineIDynamicWrapperProperty(typeBuilder, typeof(IDynamicWrapper).GetProperty(nameof(IDynamicWrapper.Instance))!, instanceField, getterSetterAttributes, castGetTo: typeof(object), castSetTo: instanceInterface);
        DefineIDynamicWrapperProperty_InterfaceType(typeBuilder, typeof(IDynamicWrapper).GetProperty(nameof(IDynamicWrapper.WrapperInterface))!, wrapperInterface, getterSetterAttributes); // readonly, no backing field
        DefineIDynamicWrapperProperty_InterfaceType(typeBuilder, typeof(IDynamicWrapper).GetProperty(nameof(IDynamicWrapper.InstanceInterface))!, instanceInterface, getterSetterAttributes);

        static void DefineIDynamicWrapperProperty(TypeBuilder typeBuilder, PropertyInfo propInfo, FieldBuilder backingField, MethodAttributes attr, Type? castGetTo, Type? castSetTo)
        {
            PropertyBuilder propBuilder = typeBuilder.DefineProperty(propInfo.Name, PropertyAttributes.None, propInfo.PropertyType, Type.EmptyTypes);
            if (propInfo.GetMethod != null)
            {
                MethodBuilder propGetter = typeBuilder.DefineMethod(propInfo.GetMethod.Name, attr, CallingConventions.HasThis, propBuilder.PropertyType, Type.EmptyTypes);
                propBuilder.SetGetMethod(propGetter);

                ILGenerator il = propGetter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, backingField);
                if (castGetTo != null)
                    il.Emit(OpCodes.Castclass, castGetTo);
                il.Emit(OpCodes.Ret);
            }
            if (propInfo.SetMethod != null)
            {
                MethodBuilder propSetter = typeBuilder.DefineMethod(propInfo.SetMethod.Name, attr, CallingConventions.HasThis, typeof(void), [propBuilder.PropertyType]);
                propBuilder.SetSetMethod(propSetter);
                propSetter.DefineParameter(1, ParameterAttributes.None, "value");

                ILGenerator il = propSetter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                if (castSetTo != null)
                    il.Emit(OpCodes.Castclass, castSetTo);
                il.Emit(OpCodes.Stfld, backingField);
                il.Emit(OpCodes.Ret);
            }
        }

        static void DefineIDynamicWrapperProperty_InterfaceType(TypeBuilder typeBuilder, PropertyInfo propInfo, Type interfaceType, MethodAttributes attr)
        {
            PropertyBuilder propBuilder = typeBuilder.DefineProperty(propInfo.Name, PropertyAttributes.None, propInfo.PropertyType, Type.EmptyTypes);
            if (propInfo.GetMethod != null)
            {
                MethodBuilder propGetter = typeBuilder.DefineMethod(propInfo.GetMethod.Name, attr, CallingConventions.HasThis, propBuilder.PropertyType, Type.EmptyTypes);
                propBuilder.SetGetMethod(propGetter);

                ILGenerator il = propGetter.GetILGenerator();
                il.Emit(OpCodes.Ldtoken, interfaceType);
                il.Emit(OpCodes.Call, getTypeFromHandle);
                il.Emit(OpCodes.Ret);
            }
        }

        static bool RequireWrapping(Type type, string wrapperModId)
        {
            // Cant use the lookups because they may not be filled out yet (ie if this is the first type registered, the lookups will be empty)
            return DeclaredInterfaces.TryGetValue(wrapperModId, out var targetTypes) && targetTypes.Contains(type);
        }

        // Dont actually need to use the delegates here, since we already know the types of the wrapper and the underlying object, could just perform a direct pass through instead.
        // But they are more flexible (if the implementation should change), and using them doesnt hurt.
        // They are required though for writing compile-time wrappers, and since we have them, might as well use them. I also dont want to re-write the code.
        Dictionary<string, FieldInfo> delegateFields = [];

        // Get all properties and methods from the wrapperInterfaceType (methods exclude property getter and setters)
        IEnumerable<PropertyInfo> interfaceProps = wrapperInterface.GetProperties();
        IEnumerable<MethodInfo> interfaceMethods = wrapperInterface.GetMethods().Except(interfaceProps.SelectMany(prop => ((IEnumerable<MethodInfo>)[prop.GetMethod!, prop.SetMethod!]).Where(mInfo => mInfo != null)));

        foreach (PropertyInfo propInfo in interfaceProps)
        {
            PropertyBuilder propBuilder = typeBuilder.DefineProperty(propInfo.Name, PropertyAttributes.None, propInfo.PropertyType, Type.EmptyTypes);

            if (propInfo.GetMethod != null)
            {
                MethodBuilder propGetter = typeBuilder.DefineMethod(propInfo.GetMethod.Name, getterSetterAttributes, CallingConventions.HasThis, propInfo.PropertyType, Type.EmptyTypes);
                propBuilder.SetGetMethod(propGetter);

                string propName = propGetter.Name;
                Delegate del = delegates[propName];
                FieldBuilder delField = typeBuilder.DefineField(propName.AsBackingField, del.GetType(), FieldAttributes.Private | FieldAttributes.InitOnly);
                MethodInfo invoke = del.GetType().GetMethod("Invoke")!;
                delegateFields.Add(propName, delField);

                bool wrapReturn = RequireWrapping(propInfo.PropertyType, wrapperModId);
                MethodInfo? wrap = null;

                ILGenerator il = propGetter.GetILGenerator();
                if (wrapReturn)
                {
                    wrap = wrapOpenGeneric.MakeGenericMethod(propInfo.PropertyType); // T
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, wrapperModIdField); // targetModId
                }
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, delField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, instanceField);

                il.Emit(OpCodes.Callvirt, invoke);
                if (wrapReturn)
                {
                    il.Emit(OpCodes.Castclass, typeof(object)); // objectToWrap
                    il.Emit(OpCodes.Ldnull); // sourceInterface
                    il.Emit(OpCodes.Call, wrap!); // public static T? Wrap<T>(string targetModId, object? objectToWrap, Type? sourceInterface = null)
                }
                il.Emit(OpCodes.Ret);
            }
            if (propInfo.SetMethod != null)
            {
                MethodBuilder propSetter = typeBuilder.DefineMethod(propInfo.SetMethod.Name, getterSetterAttributes, CallingConventions.HasThis, typeof(void), [propInfo.PropertyType]);
                propBuilder.SetSetMethod(propSetter);
                propSetter.DefineParameter(1, ParameterAttributes.None, "value");

                string propName = propSetter.Name;
                Delegate del = delegates[propName];
                FieldBuilder delField = typeBuilder.DefineField(propName.AsBackingField, del.GetType(), FieldAttributes.Private | FieldAttributes.InitOnly);
                MethodInfo invoke = del.GetType().GetMethod("Invoke")!;
                delegateFields.Add(propName, delField);

                ILGenerator il = propSetter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, delField);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, instanceField);

                if (RequireWrapping(propInfo.PropertyType, wrapperModId))
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, instanceModIdField); // targetModId
                    il.Emit(OpCodes.Ldarg_1); // objectToWrap
                    il.Emit(OpCodes.Ldtoken, propInfo.PropertyType);
                    il.Emit(OpCodes.Call, getTypeFromHandle); // sourceInterface
                    il.Emit(OpCodes.Call, wrapNonGeneric); // public static object? Wrap(string targetModId, object? objectToWrap, Type? sourceInterface = null)
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_1);
                }

                il.Emit(OpCodes.Callvirt, invoke);
                il.Emit(OpCodes.Ret);
            }
        }

        foreach (MethodInfo method in interfaceMethods)
        {
            Delegate del = delegates[method.Name];
            FieldBuilder delField = typeBuilder.DefineField(method.Name.AsBackingField, del.GetType(), FieldAttributes.Private | FieldAttributes.InitOnly);
            MethodInfo invoke = del.GetType().GetMethod("Invoke")!;
            delegateFields.Add(method.Name, delField);

            ParameterInfo[] paramInfos = method.GetParameters();
            Type[]? paramTypes = [.. paramInfos.Select(p => p.ParameterType)];
            MethodBuilder methodBuilder = typeBuilder.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot, CallingConventions.HasThis, method.ReturnType, paramTypes);

            bool wrapReturn = method.ReturnType != typeof(void) && RequireWrapping(method.ReturnType, wrapperModId);
            MethodInfo? wrap = null;

            ILGenerator il = methodBuilder.GetILGenerator();
            if (wrapReturn)
            {
                // Need to wrap the return value so the caller can read it. Pre-stack some arguments now, but will perform the method call at the end.
                wrap = wrapOpenGeneric.MakeGenericMethod(method.ReturnType); // T
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, wrapperModIdField); // targetModId
            }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, delField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, instanceField);

            for (int i = 0; i < paramInfos.Length; i++)
            {
                if (RequireWrapping(paramInfos[i].ParameterType, wrapperModId))
                {
                    // Need to wrap the argument so the instance can read it
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, instanceModIdField); // targetModId
                    il.Emit(OpCodes.Ldarg, i + 1); // objectToWrap
                    il.Emit(OpCodes.Ldtoken, paramInfos[i].ParameterType);
                    il.Emit(OpCodes.Call, getTypeFromHandle); // sourceInterface
                    il.Emit(OpCodes.Call, wrapNonGeneric); // public static object? Wrap(string targetModId, object? objectToWrap, Type? sourceInterface = null)
                }
                else
                {
                    il.Emit(OpCodes.Ldarg, i + 1);
                }
                ParameterAttributes attr = paramInfos[i].IsOptional ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None;
                methodBuilder.DefineParameter(i + 1, attr, paramInfos[i].Name);
            }

            il.Emit(OpCodes.Callvirt, invoke); // Invoke the delegate
            if (wrapReturn)
            {
                il.Emit(OpCodes.Castclass, typeof(object)); // objectToWrap
                il.Emit(OpCodes.Ldnull); // sourceInterface
                il.Emit(OpCodes.Call, wrap!); // public static T? Wrap<T>(string targetModId, object? objectToWrap, Type? sourceInterface = null)
            }
            il.Emit(OpCodes.Ret);
        }

        // Constructor (do this last because it requires all the delegate fields to have already been defined)
        {
            MethodAttributes ctorAttr = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            ParameterInfo[] paramInfos = typeof(DynamicWrapperConstructor).GetMethod("Invoke")!.GetParameters();
            ConstructorBuilder ctorBuilder = typeBuilder.DefineConstructor(ctorAttr, CallingConventions.HasThis, [.. paramInfos.Select(p => p.ParameterType)]);

            for (int i = 0; i < paramInfos.Length; i++)
            {
                ctorBuilder.DefineParameter(i + 1, ParameterAttributes.None, paramInfos[i].Name);
            }

            ILGenerator il = ctorBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);

            // Init IDynamicWrapper fields
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, wrapperModIdField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, instanceModIdField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Castclass, instanceInterface);
            il.Emit(OpCodes.Stfld, instanceField);

            // Init all delegate fields. Delegates from the dictionary should map 1:1 with the fields
            MethodInfo dictIndexerGetter = typeof(IDictionary<string, Delegate>).IndexerGetter(null);
            foreach (var kvp in delegateFields)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_S, 4); // load the delegate dict
                il.Emit(OpCodes.Ldstr, kvp.Key);
                il.Emit(OpCodes.Callvirt, dictIndexerGetter); // get the delegate itself
                il.Emit(OpCodes.Stfld, kvp.Value);
            }

            il.Emit(OpCodes.Ret);
        }

        TypeInfo createdType = typeBuilder.CreateTypeInfo(); // Woot!

#if DEBUG
        if (moduleBuilder != _persistedModuleBuilder) // dont stuck in loop
        {
            if (!_assemblySaved)
            {
                // Run this through the persisted builder so we can save and inspect it in dnSpy if needed.
                CreateWrapperFactory(_persistedModuleBuilder, wrapperInterface, instanceInterface, delegates, wrapperModId, instanceModId);
            }
        }
        else
        {
            return null!;
        }
#endif

        return (instance) => (IDynamicWrapper)Activator.CreateInstance(createdType, wrapperModId, instanceModId, instance, delegates)!;
    }

    private static bool _assemblySaved = false;

    /// <summary>
    /// Saves the generated assembly to disk. Call this to inspect the generated types with dnSpy. Can only be saved once.
    /// </summary>
    /// <remarks>Requires debug build. In a release build, perform the following to inspect instead: dnSpy -> Debug Menu -> Attach to Process -> SlayTheSpire2.exe -> break/pause -> Debug Menu -> Windows -> Modules -> DynamicWrappers</remarks>
    /// <param name="filename">The file name to save to. Root directory is StS executable folder.</param>
    /// <returns><see langword="false"/> if BaseLib is not running a debug build (and no file is generated), or if the assembly has already been saved.</returns>
    internal static bool SaveAssembly(string filename)
    {
        // I dont think this belongs on the public API, but can call this via reflection from another mod if needed
#if DEBUG
        if (_assemblySaved)
            return false;

        _persistedAssemblyBuilder.Save(filename);
        _assemblySaved = true;
        BaseLibMain.Logger.Warn($"[DynamicWrappers] Saving persisted assembly to file; no further types can be created here (does not affect the runtime assembly)");
        return true;
#else
        // TODO: generate the persisted assembly on demand
        return false;
#endif
    }
}

file static class Extensions
{
    extension(string name)
    {
        public string AsBackingField => $"_{name}";
        public string AsBackingFieldCamelCase => $"_{Godot.StringExtensions.ToCamelCase(name)}";
        public string AsPropertyGetter => $"_get_{name}";
        public string AsPropertySetter => $"_set_{name}";
    }
}
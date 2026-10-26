using Mono.Cecil;
using Mono.Cecil.Cil;

const int TargetSize = 32;
const string AssemblyName = "ScriptsAssDef.dll";

if (args.Length != 2 || args[0] is not ("--inspect" or "--apply" or "--verify" or "--restore"))
{
    Console.Error.WriteLine("Usage: SWYFLobbyPatcher --inspect|--apply|--verify|--restore <game-folder>");
    return 2;
}

string assemblyPath = Path.Combine(Path.GetFullPath(args[1]),
    "Scam With Your Friends_Data", "Managed", AssemblyName);
string backupPath = assemblyPath + ".before-32-player-patch.bak";
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"Game assembly not found: {assemblyPath}");
    return 2;
}

try
{
    switch (args[0])
    {
        case "--inspect":
            using (var assembly = Open(assemblyPath))
                Inspect(assembly);
            break;
        case "--verify":
            using (var assembly = Open(assemblyPath))
                Verify(assembly);
            break;
        case "--apply":
            Apply(assemblyPath, backupPath);
            break;
        case "--restore":
            if (!File.Exists(backupPath))
                throw new InvalidOperationException("No original backup exists to restore.");
            File.Copy(backupPath, assemblyPath, overwrite: true);
            File.Delete(backupPath);
            Console.WriteLine("Original game assembly restored.");
            break;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Patch failed: {ex.Message}");
    return 1;
}

static AssemblyDefinition Open(string path)
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
    return AssemblyDefinition.ReadAssembly(path,
        new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = false,
            ReadingMode = ReadingMode.Deferred
        });
}

static TypeDefinition FindType(AssemblyDefinition assembly, string name) =>
    assembly.MainModule.Types.Single(t => t.Name == name);

static MethodDefinition FindMethod(TypeDefinition type, string name) =>
    type.Methods.Single(m => m.Name == name);

static int? Constant(Instruction i) => i.OpCode.Code switch
{
    Code.Ldc_I4_M1 => -1,
    Code.Ldc_I4_0 => 0,
    Code.Ldc_I4_1 => 1,
    Code.Ldc_I4_2 => 2,
    Code.Ldc_I4_3 => 3,
    Code.Ldc_I4_4 => 4,
    Code.Ldc_I4_5 => 5,
    Code.Ldc_I4_6 => 6,
    Code.Ldc_I4_7 => 7,
    Code.Ldc_I4_8 => 8,
    Code.Ldc_I4_S => (sbyte)i.Operand,
    Code.Ldc_I4 => (int)i.Operand,
    _ => null
};

static bool IsIntClamp(Instruction i) =>
    i.OpCode.Code is Code.Call or Code.Callvirt
    && i.Operand is MethodReference method
    && method.Name == "Clamp"
    && method.DeclaringType.FullName == "UnityEngine.Mathf"
    && method.Parameters.Count == 3
    && method.Parameters.All(p => p.ParameterType.FullName == "System.Int32");

static int[] ClampUpperBounds(MethodDefinition method)
{
    var il = method.Body.Instructions;
    return Enumerable.Range(1, il.Count - 1)
        .Where(i => IsIntClamp(il[i]))
        .Select(i => Constant(il[i - 1]) ?? -999)
        .ToArray();
}

static void PatchClamp(TypeDefinition type, string methodName)
{
    var method = FindMethod(type, methodName);
    var il = method.Body.Instructions;
    int patched = 0;
    for (int i = 1; i < il.Count; i++)
    {
        if (!IsIntClamp(il[i]) || Constant(il[i - 1]) != 8)
            continue;
        il[i - 1].OpCode = OpCodes.Ldc_I4;
        il[i - 1].Operand = TargetSize;
        patched++;
    }
    if (patched != 1)
        throw new InvalidOperationException($"{type.Name}.{methodName}: expected exactly one clamp upper bound of 8, found {patched}.");
    Console.WriteLine($"Patched {type.Name}.{methodName} clamp 8 -> {TargetSize}");
}

static void PatchOriginalMaxConnections(TypeDefinition type)
{
    var awake = FindMethod(type, "Awake");
    var matches = awake.Body.Instructions.Where(i => Constant(i) == 8).ToArray();
    if (matches.Length != 1)
        throw new InvalidOperationException($"Expected exactly one default 8 in {type.Name}.Awake; found {matches.Length}.");
    matches[0].OpCode = OpCodes.Ldc_I4;
    matches[0].Operand = TargetSize;
    Console.WriteLine("Patched NetworkManager.Awake default maxConnections.");
}

static void ReplaceOptions(TypeDefinition type)
{
    var options = type.Fields.Single(f => f.Name == "DefaultLobbySizeOptions");
    var cctor = FindMethod(type, ".cctor");
    var instructions = cctor.Body.Instructions;
    var returns = instructions.Where(i => i.OpCode.Code == Code.Ret).ToArray();
    if (returns.Length != 1)
        throw new InvalidOperationException($"Unexpected {type.Name} static initializer shape.");

    var il = cctor.Body.GetILProcessor();
    var ret = returns[0];
    il.InsertBefore(ret, Instruction.Create(OpCodes.Ldc_I4, TargetSize));
    il.InsertBefore(ret, Instruction.Create(OpCodes.Newarr, type.Module.TypeSystem.Int32));
    for (int i = 0; i < TargetSize; i++)
    {
        il.InsertBefore(ret, Instruction.Create(OpCodes.Dup));
        il.InsertBefore(ret, Instruction.Create(OpCodes.Ldc_I4, i));
        il.InsertBefore(ret, Instruction.Create(OpCodes.Ldc_I4, i + 1));
        il.InsertBefore(ret, Instruction.Create(OpCodes.Stelem_I4));
    }
    il.InsertBefore(ret, Instruction.Create(OpCodes.Stsfld, options));
    Console.WriteLine("Patched UI options to contiguous 1..32 (preserves selected-index mapping).");
}

static void PatchInGameDropdown(TypeDefinition type)
{
    const string dropdownName = "host-lobby-size-dropdown";
    var target = type.Methods.Single(method => method.HasBody
        && method.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldstr && Equals(i.Operand, dropdownName))
        && method.Body.Instructions.Any(i =>
            i.Operand is MethodReference call && call.Name == "set_choices"));
    var instructions = target.Body.Instructions;
    int anchor = instructions.ToList().FindIndex(i =>
        i.OpCode.Code == Code.Ldstr && Equals(i.Operand, dropdownName));
    var setter = instructions.Skip(anchor).First(i => i.Operand is MethodReference call
        && call.Name == "set_choices"
        && call.Parameters.Count == 1);
    int setterIndex = instructions.IndexOf(setter);
    var methods = instructions.Take(setterIndex)
        .Select(i => i.Operand).OfType<MethodReference>().ToArray();
    var constructor = methods.Last(m => m.Name == ".ctor"
        && m.DeclaringType.FullName.StartsWith("System.Collections.Generic.List\x60" + "1<System.String>", StringComparison.Ordinal));
    var add = methods.Last(m => m.Name == "Add"
        && m.DeclaringType.FullName.StartsWith("System.Collections.Generic.List\x60" + "1<System.String>", StringComparison.Ordinal));

    if (!Enumerable.Range(1, 8).All(n => instructions.Take(setterIndex).Any(
        i => i.OpCode.Code == Code.Ldstr && Equals(i.Operand, n.ToString()))))
        throw new InvalidOperationException("Pause-menu lobby size choices do not match expected 1..8 shape.");

    var setterRef = (MethodReference)setter.Operand;
    var optionsType = type.Module.ImportReference(setterRef.Parameters[0].ParameterType);
    var helper = new MethodDefinition("SWYF32_GetLobbySizeChoices",
        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, optionsType);
    helper.Parameters.Add(new ParameterDefinition("oldChoices", ParameterAttributes.None, optionsType));
    type.Methods.Add(helper);

    var gen = helper.Body.GetILProcessor();
    gen.Append(Instruction.Create(OpCodes.Newobj, constructor));
    for (int i = 1; i <= TargetSize; i++)
    {
        gen.Append(Instruction.Create(OpCodes.Dup));
        gen.Append(Instruction.Create(OpCodes.Ldstr, i.ToString()));
        gen.Append(Instruction.Create(OpCodes.Callvirt, add));
    }
    gen.Append(Instruction.Create(OpCodes.Ret));
    target.Body.GetILProcessor().InsertBefore(setter, Instruction.Create(OpCodes.Call, helper));
    Console.WriteLine("Patched in-game pause-menu dropdown choices to 1..32.");
}

static bool Has32Options(TypeDefinition type)
{
    var cctor = FindMethod(type, ".cctor");
    var il = cctor.Body.Instructions;
    // The patched initializer ends by assigning a new 32-element array to the field.
    var fieldWrites = il.Select((instruction, index) => (instruction, index))
        .Where(t => t.instruction.OpCode.Code == Code.Stsfld
            && t.instruction.Operand is FieldReference field
            && field.Name == "DefaultLobbySizeOptions").ToArray();
    if (fieldWrites.Length < 2)
        return false;
    int index = fieldWrites[^1].index;
    var prior = il.Take(index).ToArray();
    return prior.Length >= 3
        && prior.Any((instruction) => instruction.OpCode.Code == Code.Newarr
            && instruction.Operand is TypeReference t && t.FullName == "System.Int32"
            && Array.IndexOf(prior, instruction) > 0
            && Constant(prior[Array.IndexOf(prior, instruction) - 1]) == TargetSize);
}

static void Verify(AssemblyDefinition assembly)
{
    var max = FindType(assembly, "MaxLobbySizeSetting");
    var constant = max.Fields.Single(f => f.Name == "MaximumLobbySize");
    if (!constant.HasConstant || Convert.ToInt32(constant.Constant) != TargetSize
        || !Has32Options(max))
        throw new InvalidOperationException("Capacity metadata or 32-entry UI array is not patched.");

    foreach (string name in new[] { "ApplyLobbySizeLimit", "ReadSavedLobbySize", "LoadIndex" })
        if (!ClampUpperBounds(FindMethod(max, name)).Contains(TargetSize))
            throw new InvalidOperationException($"{max.Name}.{name} does not have a 32-player bound.");

    var steam = FindType(assembly, "SteamCreateLobbyForServer");
    if (!ClampUpperBounds(FindMethod(steam, "TryApplyLobbyMemberLimitFromHostSettings")).Contains(TargetSize))
        throw new InvalidOperationException("Steam member-limit clamp not patched.");

    var manager = FindType(assembly, "DeveloperSpectatorNetworkManager");
    if (!FindMethod(manager, "Awake").Body.Instructions.Any(i => Constant(i) == TargetSize))
        throw new InvalidOperationException("NetworkManager initial capacity not patched.");

    var pause = FindType(assembly, "PauseMenuToolkitController");
    var helper = pause.Methods.SingleOrDefault(m => m.Name == "SWYF32_GetLobbySizeChoices");
    if (helper == null || !helper.HasBody
        || helper.Body.Instructions.Count(i => i.OpCode.Code == Code.Ldstr
            && i.Operand is string value && int.TryParse(value, out int n)
            && n >= 1 && n <= TargetSize) != TargetSize)
        throw new InvalidOperationException("In-game lobby dropdown is not extended to 32.");

    Console.WriteLine("Verified 32-player UI (both menus), persisted preferences, Mirror, and Steam lobby limits.");
    Console.WriteLine("Real 9/16/32-player online compatibility is NOT established by this binary check.");
}

static void Inspect(AssemblyDefinition assembly)
{
    var max = FindType(assembly, "MaxLobbySizeSetting");
    Console.WriteLine($"Lobby limit metadata: {max.Fields.Single(f => f.Name == "MaximumLobbySize").Constant}");
    foreach (string name in new[] { "ApplyLobbySizeLimit", "ReadSavedLobbySize", "LoadIndex" })
        Console.WriteLine($"MaxLobbySizeSetting.{name} clamp maxima: [{string.Join(", ", ClampUpperBounds(FindMethod(max, name)))}]");
    var steam = FindType(assembly, "SteamCreateLobbyForServer");
    Console.WriteLine($"SteamCreateLobbyForServer.TryApplyLobbyMemberLimitFromHostSettings clamp maxima: [{string.Join(", ", ClampUpperBounds(FindMethod(steam, "TryApplyLobbyMemberLimitFromHostSettings")))}]");
    Console.WriteLine($"32 UI entries installed: {Has32Options(max)}");
}

static void Apply(string originalPath, string backupPath)
{
    if (File.Exists(backupPath))
        throw new InvalidOperationException($"Original backup already exists: {backupPath}. Refusing to overwrite it.");
    string temporaryPath = originalPath + ".patching.tmp";
    if (File.Exists(temporaryPath))
        throw new InvalidOperationException($"Temporary file already exists: {temporaryPath}");

    try
    {
        using (var assembly = Open(originalPath))
        {
            var max = FindType(assembly, "MaxLobbySizeSetting");
            var maxField = max.Fields.Single(f => f.Name == "MaximumLobbySize");
            if (!maxField.HasConstant || Convert.ToInt32(maxField.Constant) != 8)
                throw new InvalidOperationException("Unrecognized or already patched game build; leaving file unchanged.");

            maxField.Constant = TargetSize;
            ReplaceOptions(max);
            foreach (string name in new[] { "ApplyLobbySizeLimit", "ReadSavedLobbySize", "LoadIndex" })
                PatchClamp(max, name);
            PatchClamp(FindType(assembly, "SteamCreateLobbyForServer"), "TryApplyLobbyMemberLimitFromHostSettings");
            PatchOriginalMaxConnections(FindType(assembly, "DeveloperSpectatorNetworkManager"));
            PatchInGameDropdown(FindType(assembly, "PauseMenuToolkitController"));
            Verify(assembly);
            assembly.Write(temporaryPath, new WriterParameters { WriteSymbols = false });
        }
        using (var check = Open(temporaryPath))
            Verify(check);
        File.Copy(originalPath, backupPath, overwrite: false);
        File.Move(temporaryPath, originalPath, overwrite: true);
        Console.WriteLine($"Patched {originalPath}");
        Console.WriteLine($"Original backup: {backupPath}");
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

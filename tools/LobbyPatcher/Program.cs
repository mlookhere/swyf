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
            // Accept either the older 32-player patch (with the crashing helper)
            // or the corrected one. Never overwrite a Steam-updated original.
            using (var installed = Open(assemblyPath))
            using (var original = Open(backupPath))
            {
                Verify32Capacity(installed);
                var pause = FindType(installed, "PauseMenuToolkitController");
                if (!HasExpandedPauseMenu(pause) &&
                    !pause.Methods.Any(m => m.Name == "SWYF32_GetLobbySizeChoices"))
                    throw new InvalidOperationException("Installed game is not a recognized 32-player patch.");
                var field = FindType(original, "MaxLobbySizeSetting").Fields.Single(f => f.Name == "MaximumLobbySize");
                if (!field.HasConstant || Convert.ToInt32(field.Constant) != 8)
                    throw new InvalidOperationException("Backup does not contain the expected original eight-player assembly.");
                if (installed.MainModule.Mvid != original.MainModule.Mvid)
                    throw new InvalidOperationException("Backup belongs to a different game build (MVID mismatch).");
            }
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

// Use the same IList/Add IL emitted by the original UI builder.
// Adding a new helper method and redirecting a call inside BindHostSettings
// caused a reproducible Mono JIT crash when the pause menu opened.
static MethodDefinition PauseMenuBinder(TypeDefinition type)
{
    const string dropdownName = "host-lobby-size-dropdown";
    return type.Methods.Single(method => method.HasBody
        && method.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldstr && Equals(i.Operand, dropdownName))
        && method.Body.Instructions.Any(i =>
            i.Operand is MethodReference call && call.Name == "set_choices"));
}

static (MethodDefinition Method, Instruction Setter) FindPauseMenuSetter(TypeDefinition type)
{
    var method = PauseMenuBinder(type);
    var instructions = method.Body.Instructions;
    int anchor = instructions.ToList().FindIndex(i =>
        i.OpCode.Code == Code.Ldstr && Equals(i.Operand, "host-lobby-size-dropdown"));
    var setter = instructions.Skip(anchor).First(i =>
        i.Operand is MethodReference call && call.Name == "set_choices"
        && call.Parameters.Count == 1);
    return (method, setter);
}

static void PatchInGameDropdown(TypeDefinition type)
{
    var (method, setter) = FindPauseMenuSetter(type);
    var instructions = method.Body.Instructions;
    int setterIndex = instructions.IndexOf(setter);
    var previous = instructions.Take(setterIndex).ToArray();

    // Abort if the original game's IL has changed, rather than corrupting it.
    foreach (int i in Enumerable.Range(1, 8))
        if (!previous.Any(x => x.OpCode.Code == Code.Ldstr && Equals(x.Operand, i.ToString())))
            throw new InvalidOperationException($"Pause menu has no original choice {i}.");

    var addCalls = previous.Where(i =>
        i.Operand is MethodReference m
        && m.Name == "Add"
        && m.DeclaringType.FullName.StartsWith("System.Collections.Generic.List\x601<System.String>", StringComparison.Ordinal))
        .ToArray();
    if (addCalls.Length < 8)
        throw new InvalidOperationException("Pause-menu list initializer shape changed; cannot safely patch.");

    var add = (MethodReference)addCalls[^1].Operand;
    var callOpcode = addCalls[^1].OpCode;
    if (callOpcode.Code is not (Code.Call or Code.Callvirt))
        throw new InvalidOperationException("Unexpected List<string>.Add invocation.");
    // At set_choices the stack holds [dropdown, List<string>]. Keep that
    // original list on the stack while appending additional strings to it.
    var il = method.Body.GetILProcessor();
    foreach (int i in Enumerable.Range(9, TargetSize - 8))
    {
        il.InsertBefore(setter, Instruction.Create(OpCodes.Dup));
        il.InsertBefore(setter, Instruction.Create(OpCodes.Ldstr, i.ToString()));
        il.InsertBefore(setter, Instruction.Create(callOpcode, add));
    }
    Console.WriteLine("Patched in-game pause-menu dropdown choices in place (1..32), with no injected method calls.");
}

static bool HasExpandedPauseMenu(TypeDefinition type)
{
    var (method, setter) = FindPauseMenuSetter(type);
    var instructions = method.Body.Instructions;
    int index = instructions.IndexOf(setter);
    if (index < (TargetSize - 8) * 3)
        return false;
    // The 24 appended choices must be contiguous immediately before the
    // existing property setter, with balanced stack operations.
    for (int i = 9; i <= TargetSize; i++)
    {
        int offset = index - (TargetSize - i + 1) * 3;
        if (instructions[offset].OpCode.Code != Code.Dup
            || instructions[offset + 1].OpCode.Code != Code.Ldstr
            || !Equals(instructions[offset + 1].Operand, i.ToString())
            || instructions[offset + 2].Operand is not MethodReference m
            || m.Name != "Add")
            return false;
    }
    return !type.Methods.Any(m => m.Name == "SWYF32_GetLobbySizeChoices");
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

static void Verify32Capacity(AssemblyDefinition assembly)
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
}

static void Verify(AssemblyDefinition assembly)
{
    Verify32Capacity(assembly);
    var pause = FindType(assembly, "PauseMenuToolkitController");
    if (!HasExpandedPauseMenu(pause))
        throw new InvalidOperationException("In-game lobby dropdown IL is not safely extended to 32.");

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
        // Steam updates may restore the original DLL while leaving a previous backup behind.
        // Keep the previous backup instead of overwriting or deleting it.
        string? archivedBackup = null;
        if (File.Exists(backupPath))
        {
            archivedBackup = $"{backupPath}.archived-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
            File.Move(backupPath, archivedBackup);
            Console.WriteLine($"Existing backup preserved: {archivedBackup}");
        }

        try
        {
            File.Copy(originalPath, backupPath, overwrite: false);
            File.Move(temporaryPath, originalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            if (archivedBackup != null && !File.Exists(backupPath))
                File.Move(archivedBackup, backupPath);
            throw;
        }
        Console.WriteLine($"Patched {originalPath}");
        Console.WriteLine($"Original backup: {backupPath}");
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

using System.Reflection;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;

namespace DSPilot.TestConsole;

/// <summary>
/// PlcValue 타입의 실제 구조를 분석하는 유틸리티
/// </summary>
public static class PlcValueInspector
{
    public static void Inspect()
    {
        Console.WriteLine("=== PlcValue Type Inspector ===");
        Console.WriteLine();

        var plcValueType = typeof(PlcValue);

        Console.WriteLine($"Type: {plcValueType.FullName}");
        Console.WriteLine($"Is class: {plcValueType.IsClass}");
        Console.WriteLine($"Is sealed: {plcValueType.IsSealed}");
        Console.WriteLine($"Base type: {plcValueType.BaseType?.Name}");
        Console.WriteLine();

        // Constructors
        Console.WriteLine("Constructors:");
        var ctors = plcValueType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"  new PlcValue({paramStr})");
        }
        Console.WriteLine();

        // Static methods (F# discriminated union cases)
        Console.WriteLine("Static methods (Union Cases):");
        var staticMethods = plcValueType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name)
            .ToList();

        foreach (var method in staticMethods)
        {
            var parameters = method.GetParameters();
            var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"  static {method.ReturnType.Name} {method.Name}({paramStr})");
        }
        Console.WriteLine();

        // Properties
        Console.WriteLine("Properties:");
        var properties = plcValueType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name} {{ {(prop.CanRead ? "get; " : "")}{(prop.CanWrite ? "set; " : "")}}}");
        }
        Console.WriteLine();

        // Nested types (F# union tags)
        Console.WriteLine("Nested types:");
        var nestedTypes = plcValueType.GetNestedTypes(BindingFlags.Public);
        foreach (var nested in nestedTypes)
        {
            Console.WriteLine($"  {nested.Name}");
        }
        Console.WriteLine();

        // Try to create a Bool value
        Console.WriteLine("Attempting to create Bool values:");
        try
        {
            // Try NewBool
            var newBoolMethod = staticMethods.FirstOrDefault(m => m.Name == "NewBool");
            if (newBoolMethod != null)
            {
                var trueValue = newBoolMethod.Invoke(null, new object[] { true });
                var falseValue = newBoolMethod.Invoke(null, new object[] { false });
                Console.WriteLine($"  ✅ PlcValue.NewBool(true) = {trueValue}");
                Console.WriteLine($"  ✅ PlcValue.NewBool(false) = {falseValue}");
            }
            else
            {
                Console.WriteLine($"  ❌ NewBool method not found");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Error: {ex.Message}");
        }

        Console.WriteLine();
    }
}

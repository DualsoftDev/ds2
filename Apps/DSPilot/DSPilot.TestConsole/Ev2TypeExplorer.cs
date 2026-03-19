using Ev2.Backend.PLC;
using Microsoft.FSharp.Core;
using System.Reflection;

namespace DSPilot.TestConsole;

/// <summary>
/// Ev2.Backend.PLC 타입 탐색 및 사용 예제
/// </summary>
public static class Ev2TypeExplorer
{
    public static void ExplorePLCBackendService()
    {
        Console.WriteLine("=== Ev2 Type Explorer ===");
        Console.WriteLine();

        // PLCBackendService 생성자 파라미터 탐색
        var plcServiceType = typeof(PLCBackendService);
        var ctors = plcServiceType.GetConstructors();

        foreach (var ctor in ctors)
        {
            Console.WriteLine($"Constructor: {ctor}");
            var parameters = ctor.GetParameters();

            foreach (var param in parameters)
            {
                Console.WriteLine($"  Parameter: {param.Name}");
                Console.WriteLine($"    Type: {param.ParameterType.FullName}");
                Console.WriteLine($"    Is array: {param.ParameterType.IsArray}");

                if (param.ParameterType.IsArray)
                {
                    var elementType = param.ParameterType.GetElementType();
                    Console.WriteLine($"    Element type: {elementType?.FullName}");

                    // ScanConfiguration 타입 탐색
                    if (elementType != null)
                    {
                        ExploreScanConfiguration(elementType);
                    }
                }
                else if (param.ParameterType.IsGenericType)
                {
                    var genericArgs = param.ParameterType.GetGenericArguments();
                    Console.WriteLine($"    Generic args: {string.Join(", ", genericArgs.Select(t => t.FullName))}");
                }
            }
        }

        Console.WriteLine();
    }

    private static void ExploreScanConfiguration(Type scanConfigType)
    {
        Console.WriteLine($"  === Exploring {scanConfigType.Name} ===");

        // 프로퍼티
        var props = scanConfigType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Console.WriteLine($"  Properties ({props.Length}):");
        foreach (var prop in props)
        {
            Console.WriteLine($"    {prop.PropertyType.Name} {prop.Name}");
        }

        // 필드 (F# record는 필드로 표현될 수 있음)
        var fields = scanConfigType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        Console.WriteLine($"  Fields ({fields.Length}):");
        foreach (var field in fields)
        {
            Console.WriteLine($"    {field.FieldType.Name} {field.Name}");
        }

        // 생성자
        var ctors = scanConfigType.GetConstructors();
        Console.WriteLine($"  Constructors ({ctors.Length}):");
        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"    new {scanConfigType.Name}({paramStr})");
        }

        // Static factory methods (F#에서 자주 사용)
        var staticMethods = scanConfigType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        Console.WriteLine($"  Static methods ({staticMethods.Length}):");
        foreach (var method in staticMethods.Take(10))
        {
            var parameters = method.GetParameters();
            var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"    static {method.ReturnType.Name} {method.Name}({paramStr})");
        }

        Console.WriteLine();
    }

    public static void ExploreTagHistoricWAL()
    {
        var walType = typeof(TagHistoricWAL);
        Console.WriteLine($"=== Exploring {walType.Name} ===");

        var ctors = walType.GetConstructors();
        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            Console.WriteLine($"Constructor parameters:");
            foreach (var param in parameters)
            {
                Console.WriteLine($"  {param.ParameterType.FullName} {param.Name}");
            }
        }

        Console.WriteLine();
    }

    public static void ExploreConnectionTypes()
    {
        Console.WriteLine("=== Exploring Connection Types ===");

        try
        {
            // IConnectionConfiguration 찾기
            var assembly = typeof(PLCBackendService).Assembly;
            var commonAssembly = Assembly.Load("Ev2.Backend.Common");

            Type? connectionConfigType = null;
            try
            {
                connectionConfigType = commonAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "IConnectionConfiguration");
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 로드 가능한 타입만 추출
                connectionConfigType = ex.Types
                    .Where(t => t != null)
                    .FirstOrDefault(t => t!.Name == "IConnectionConfiguration");
            }

        if (connectionConfigType != null)
        {
            Console.WriteLine($"Found: {connectionConfigType.FullName}");
            Console.WriteLine($"Is interface: {connectionConfigType.IsInterface}");

            var props = connectionConfigType.GetProperties();
            Console.WriteLine($"Properties ({props.Length}):");
            foreach (var prop in props)
            {
                Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name}");
            }

            // 구현체 찾기
            List<Type> implementations = new();
            try
            {
                implementations = commonAssembly.GetTypes()
                    .Where(t => connectionConfigType.IsAssignableFrom(t) && t.IsClass)
                    .ToList();
            }
            catch (ReflectionTypeLoadException ex)
            {
                implementations = ex.Types
                    .Where(t => t != null && connectionConfigType.IsAssignableFrom(t) && t!.IsClass)
                    .Select(t => t!)
                    .ToList();
            }

            Console.WriteLine($"\nImplementations ({implementations.Count}):");
            foreach (var impl in implementations)
            {
                Console.WriteLine($"  {impl.FullName}");

                var implCtors = impl.GetConstructors();
                foreach (var ctor in implCtors)
                {
                    var parameters = ctor.GetParameters();
                    var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Console.WriteLine($"    new {impl.Name}({paramStr})");
                }
            }
        }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exploring IConnectionConfiguration: {ex.Message}");
        }

        Console.WriteLine();

        // TagSpec 찾기
        try
        {
            var plcCommonAssembly = Assembly.Load("Ev2.PLC.Common.FS");

            Type? tagSpecType = null;
            try
            {
                tagSpecType = plcCommonAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "TagSpec");
            }
            catch (ReflectionTypeLoadException ex)
            {
                tagSpecType = ex.Types
                    .Where(t => t != null)
                    .FirstOrDefault(t => t!.Name == "TagSpec");
            }

        if (tagSpecType != null)
        {
            Console.WriteLine($"Found: {tagSpecType.FullName}");

            var props = tagSpecType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Console.WriteLine($"Properties ({props.Length}):");
            foreach (var prop in props)
            {
                Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name}");
            }

            var ctors = tagSpecType.GetConstructors();
            Console.WriteLine($"Constructors ({ctors.Length}):");
            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                var paramStr = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"  new {tagSpecType.Name}({paramStr})");
            }
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exploring TagSpec: {ex.Message}");
        }

        Console.WriteLine();
    }
}
